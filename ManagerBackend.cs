using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace PopeManager;

// QML polls a locked snapshot; worker threads never touch QObject properties.
public class ManagerBackend
{
    readonly object gate = new();
    readonly ICommandRunner runner = new ProcessRunner();
    readonly AdbManager adb;
    readonly List<string> logs = [];
    List<Headset> devices = [];
    List<PluginEntry> mods = [];
    ManagerSettings settings = ManagerSettings.Load();
    CancellationTokenSource? operation;
    bool scanning, busy, installed, hostInstalled;
    string selected = "", status = "Connect your Quest with a USB cable.", error = "", workspace = "", lastOutput = "";
    int step = -1;
    long revision;
    readonly string logFile;
    public ManagerBackend()
    {
        adb = new(runner);
        string dir = Path.Combine(AppPaths.Data, "logs"); Directory.CreateDirectory(dir);
        logFile = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
        Log("PopeManager ready. Target: " + AppPaths.TargetPackage);
        try
        {
            ModDevelopmentLibraries.Prepare(settings);
            Log("Mod development libraries ready: " + ModDevelopmentLibraries.Root);
        }
        catch (Exception e) { Log("Could not prepare mod development libraries: " + e.Message); }
    }
    public string Snapshot()
    {
        lock (gate)
        {
            var headset = devices.FirstOrDefault(d => d.Serial == selected);
            return JsonSerializer.Serialize(new
            {
                revision, busy, scanning, installed, hostInstalled, selected, status, error, step, workspace, lastOutput,
                devices = devices.Select(d => new { serial = d.Serial, name = d.Name, state = d.State, quest = d.IsQuest, installed = d.Installed, version = d.Version }),
                canInstall = !busy && !scanning && headset is { State: "device", IsQuest: true, Installed: true },
                mods = mods.Select(mod => new { fileName = mod.FileName, name = mod.Name, enabled = mod.Enabled }),
                log = string.Join('\n', logs),
                settings = new { loader = settings.LoaderPath, generators = settings.GeneratorPath, unity = settings.UnityLibrariesPath }
            });
        }
    }
    public void Refresh()
    {
        lock (gate) { if (busy || scanning) return; scanning = true; revision++; }
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var found = await adb.Detect(timeout.Token);
                lock (gate)
                {
                    devices = found;
                    if (!devices.Any(d => d.Serial == selected)) { selected = ""; installed = false; mods = []; }
                    var ready = devices.Where(d => d is { State: "device", IsQuest: true }).ToList();
                    if (selected.Length == 0 && ready.Count == 1) selected = ready[0].Serial;
                    hostInstalled = devices.FirstOrDefault(d => d.Serial == selected)?.HostInstalled ?? false;
                    installed = installed || hostInstalled;
                    if (step < 0)
                    {
                        var current = devices.FirstOrDefault(d => d.Serial == selected);
                        status = devices.Count == 0 ? "No headset detected. Connect your Quest via USB."
                            : devices.Any(d => d.State == "no permissions") ? "Linux cannot access the headset. Install Android udev rules, then reconnect it."
                            : devices.Any(d => d.State == "unauthorized") ? "Put on your headset and allow USB debugging."
                            : ready.Count > 1 && selected.Length == 0 ? "Choose the headset you want to use."
                            : current is { Installed: true } && hostInstalled ? "Already patched."
                            : current is { Installed: true } ? "Ready."
                            : current != null ? "Install the game on this headset first."
                            : "No supported Quest is ready.";
                    }
                    revision++;
                }
                string serial; lock (gate) serial = selected;
                if (found.Any(device => device.Serial == serial && device.State == "device" && device.IsQuest))
                {
                    var plugins = await new PluginService(adb).List(serial, timeout.Token);
                    lock (gate) { if (serial == selected) { mods = plugins; revision++; } }
                }
            }
            catch (Exception e) { lock (gate) { error = e is OperationCanceledException ? "ADB detection timed out." : e.Message; revision++; } }
            finally { lock (gate) { scanning = false; revision++; } }
        });
    }
    public void SelectDevice(string serial)
    {
        lock (gate) { if (busy || !devices.Any(d => d.Serial == serial)) return; selected = serial; installed = false; mods = []; step = -1; error = ""; revision++; }
        Refresh();
    }
    public void SaveSettings(string loader, string generators, string unity)
    {
        lock (gate)
        {
            if (busy) return;
            settings = new() { LoaderPath = loader.Trim(), GeneratorPath = generators.Trim(), UnityLibrariesPath = unity.Trim() };
            try { settings.Save(); ModDevelopmentLibraries.Prepare(settings); error = ""; } catch (Exception e) { error = e.Message; }
            revision++;
        }
    }
    public void Install()
    {
        string serial; ManagerSettings config; CancellationToken token;
        lock (gate)
        {
            if (busy || scanning) return;
            if (devices.FirstOrDefault(d => d.Serial == selected) is not { State: "device", IsQuest: true, Installed: true })
            { error = "Choose a headset with with Gorilla Tag installed."; revision++; return; }
            serial = selected;
            config = new() { LoaderPath = settings.LoaderPath, GeneratorPath = settings.GeneratorPath, UnityLibrariesPath = settings.UnityLibrariesPath };
            busy = true; installed = false; error = ""; step = 0; lastOutput = "";
            operation = new(); token = operation.Token; revision++;
        }
        _ = Task.Run(() => InstallAsync(serial, config, token));
    }
    internal async Task<int> RunHeadlessInstall(CancellationToken token)
    {
        var ready = (await adb.Detect(token))
            .Where(device => device is { State: "device", IsQuest: true, Installed: true }).ToList();
        if (ready.Count != 1)
        {
            string message = ready.Count == 0
                ? "Connect a Quest with Gorilla Tag installed."
                : "Multiple eligible headsets are connected. Pick one.";
            Log(message);
            Console.Error.WriteLine(message);
            return 1;
        }

        await InstallAsync(ready[0].Serial, settings, token);
        Console.WriteLine(Snapshot());
        lock (gate) return installed && error.Length == 0 ? 0 : 1;
    }
    async Task InstallAsync(string serial, ManagerSettings config, CancellationToken token)
    {
        List<string>? restoreSet = null;
        bool officialAppRemoved = false;
        var dataBackup = new GameDataBackup(adb);
        try
        {
            Stage(0, "Preparing..."); FusionHost.ValidateEnvironment(config);
            await new ApkMaker(runner).PrepareSigningKey(token, Log);
            var online = await adb.Detect(token);
            if (!online.Any(d => d.Serial == serial && d.State == "device" && d.IsQuest && d.Installed)) throw new IOException("The selected headset disconnected or Gorilla Tag is missing.");
            string work = Path.Combine(AppPaths.Data, "sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(work); lock (gate) { workspace = work; revision++; }
            var paths = AdbManager.ParsePaths((await adb.Shell(serial, token, true, "pm", "path", AppPaths.OriginalPackage)).Output);
            if (paths.Count == 0) throw new IOException("Gorilla Tag has no readable APK paths.");
            string pulled = Path.Combine(work, "original"); Directory.CreateDirectory(pulled); var local = new List<string>();
            for (int i = 0; i < paths.Count; ++i)
            {
                string file = Path.Combine(pulled, i.ToString("D2") + "-" + Path.GetFileName(paths[i]));
                Stage(1, $"Pulling APK {i + 1} of {paths.Count}");
                await adb.Run(serial, ["pull", paths[i], file], token, Log); local.Add(file);
            }
            restoreSet = local;
            _ = ApkInspector.Inspect(local);
            bool requiresObb = false;
            foreach (var apkPath in local)
            {
                using var apk = ZipFile.OpenRead(apkPath);
                requiresObb |= apk.GetEntry("assets/unity_obb_guid") != null;
            }
            Stage(1, "Backing up data");
            await dataBackup.Capture(serial, work, requiresObb, Log, token);
            var inputs = await DependencyGenerator.Extract(local, Path.Combine(work, "inputs"), token);
            Log("Detected Unity " + inputs.UnityVersion + ". Generating libs...");
            string libraries = await new DependencyGenerator(runner).Generate(inputs, work, config, Stage, Log, token);
            lock (gate) { lastOutput = work; revision++; }
            token.ThrowIfCancellationRequested(); Stage(3, "Building the payload");
            string patched = Path.Combine(work, "patched", "base.apk");
            GtagPayload.Build(local.Single(apk => ApkInspector.ReadManifest(ZipFile.OpenRead(apk)).Split.Length == 0), config.LoaderPath, libraries, patched, dataBackup.PluginsPath);
            var patchSet = local.Select(apk => ApkInspector.ReadManifest(ZipFile.OpenRead(apk)).Split.Length == 0 ? patched : apk).ToList();
            Stage(4, "Aligning and signing the APK...");
            var signed = await new ApkMaker(runner).SignSet(patchSet, Path.Combine(work, "install"), token, Log);
            token.ThrowIfCancellationRequested(); Stage(5, "Insatlling...");
            await adb.Run(serial, ["uninstall", AppPaths.OriginalPackage], token, Log);
            officialAppRemoved = true;
            var install = await adb.Run(serial, ["install-multiple", "-r", "-g", .. signed], token, Log);
            if (!install.Output.Contains("Success", StringComparison.OrdinalIgnoreCase)) throw new IOException("ADB did not report a successful installation.");
            await dataBackup.RestoreObb(serial, Log, token);
            var verify = await adb.Shell(serial, token, true, "pm", "path", AppPaths.OriginalPackage);
            if (AdbManager.ParsePaths(verify.Output).Count == 0) throw new IOException("The patched Gorilla Tag package could not be verified.");
            lock (gate) installed = true;
            Stage(6, "Done!");
            Log("Done!");
        }
        catch (OperationCanceledException)
        {
            if (officialAppRemoved && restoreSet is { Count: > 0 })
            {
                try
                {
                    Log("Preparation cancelled");
                    var present = await adb.Shell(serial, CancellationToken.None, false, "pm", "path", AppPaths.OriginalPackage);
                    if (AdbManager.ParsePaths(present.Output).Count == 0)
                        await adb.Run(serial, ["install-multiple", "-r", .. restoreSet], CancellationToken.None, Log);
                    await dataBackup.RestoreObb(serial, Log, CancellationToken.None);
                }
                catch (Exception restoreError) { Log("Recovery failed, here are backups: " + restoreError.Message); }
            }
            lock (gate) { status = "Preparation cancelled"; revision++; }
            Log("Operation cancelled.");
        }
        catch (Exception e)
        {
            if (officialAppRemoved && restoreSet is { Count: > 0 })
            {
                try
                {
                    Log("Patched install failed. Restoring...");
                    await adb.Run(serial, ["install-multiple", "-r", .. restoreSet], CancellationToken.None, Log);
                    await dataBackup.RestoreObb(serial, Log, CancellationToken.None);
                    Log("Original stuff.");
                }
                catch (Exception restoreError) { Log("Automatic original-APK restore failed: " + restoreError.Message); }
            }
            string message = e.Message;
            if (message.Contains("INSTALL_FAILED_UPDATE_INCOMPATIBLE")) message = "The patched APK set does not have the same signature";
            lock (gate) { error = message; status = "Couldnt complete preparation."; revision++; }
            Log(e.ToString());
        }
        finally { lock (gate) { busy = false; operation?.Dispose(); operation = null; revision++; } }
    }
    public void LaunchHost()
    {
        string serial;
        lock (gate) { if (busy || selected.Length == 0) return; serial = selected; }
        _ = Task.Run(async () =>
        {
            try { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await adb.Shell(serial, timeout.Token, true, "am", "start", "-n", AppPaths.TargetPackage + "/.SelectorActivity"); }
            catch (Exception e) { lock (gate) { error = e.Message; revision++; } }
        });
    }
    public void ImportMod(string fileUrl) => RunModOperation("Installing mod...", (service, serial, token) => service.Import(serial, fileUrl, token));
    public void SetModEnabled(string fileName, bool enabled) => RunModOperation(enabled ? "Enabling mod..." : "Disabling mod...", async (service, serial, token) => { await service.SetEnabled(serial, fileName, enabled, token); });
    public void RemoveMod(string fileName) => RunModOperation("Removing mod...", (service, serial, token) => service.Remove(serial, fileName, token));
    void RunModOperation(string message, Func<PluginService, string, CancellationToken, Task> action)
    {
        string serial; CancellationToken token;
        lock (gate)
        {
            if (busy || scanning || !devices.Any(device => device.Serial == selected && device.State == "device" && device.IsQuest)) return;
            serial = selected; busy = true; error = ""; status = message; operation = new(); token = operation.Token; revision++;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var service = new PluginService(adb);
                await action(service, serial, token);
                var plugins = await service.List(serial, token);
                lock (gate) { mods = plugins; status = "Done."; revision++; }
            }
            catch (Exception e) { lock (gate) { error = e is OperationCanceledException ? "Cancelled." : e.Message; revision++; } }
            finally { lock (gate) { busy = false; operation?.Dispose(); operation = null; revision++; } }
        });
    }
    public void Cancel() { lock (gate) operation?.Cancel(); }
    public void OpenOutput() => OpenFolder(lastOutput.Length > 0 ? lastOutput : AppPaths.Data);
    public void OpenLogs() => OpenFolder(Path.GetDirectoryName(logFile)!);
    void OpenFolder(string path)
    {
        try { var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open") { UseShellExecute = false, CreateNoWindow = true }; start.ArgumentList.Add(path); Process.Start(start); }
        catch (Exception e) { lock (gate) { error = e.Message; revision++; } }
    }
    void Stage(int index, string text) { lock (gate) { step = index; status = text; revision++; } Log(text); }
    void Log(string line)
    {
        lock (gate)
        {
            logs.Add($"{DateTime.Now:HH:mm:ss}  {line}"); if (logs.Count > 350) logs.RemoveRange(0, logs.Count - 350);
            try { File.AppendAllText(logFile, $"{DateTime.Now:O} {line}{Environment.NewLine}"); } catch (IOException) { }
            revision++;
        }
    }
}
