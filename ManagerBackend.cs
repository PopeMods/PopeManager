using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace PopeManager;

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
    bool scanning, refreshing, refreshRequested, busy, installed, hostInstalled;
    long refreshEpoch;
    bool checkingUpdates;
    ReleaseCheck update = new(false, "", "", "");
    string selected = "", status = "Connect your Quest with a USB cable.", error = "", workspace = "", lastOutput = "";
    SteamFrameDownloader? steam;
    bool steamLogin, steamDownloading, headless;
    string steamQr = "", steamPrompt = "";
    float steamProgress;
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
                steamLogin, steamDownloading, steamQr, steamPrompt, steamProgress,
                appVersion = UpdateChecker.CurrentVersion, checkingUpdates,
                updateAvailable = update.Available, updateVersion = update.Version, updateUrl = update.Url, updateStatus = update.Message,
                puzzleSoundUrl = new Uri(Path.Combine(AppContext.BaseDirectory, "puzzlesolved.ogg")).AbsoluteUri,
                logoUrl = new Uri(Path.Combine(AppContext.BaseDirectory, "logo.png")).AbsoluteUri,
                devices = devices.Select(d => new { serial = d.Serial, name = d.Name, state = d.State, quest = d.IsQuest, installed = d.Installed, version = d.Version }),
                canInstall = !busy && !scanning && headset is { State: "device", IsQuest: true },
                mods = mods.Select(mod => new { fileName = mod.FileName, name = mod.Name, enabled = mod.Enabled }),
                log = string.Join('\n', logs),
                settings = new { loader = settings.LoaderPath, generators = settings.GeneratorPath, unity = settings.UnityLibrariesPath }
            });
        }
    }
    public void CheckForUpdates()
    {
        lock (gate)
        {
            if (checkingUpdates) return;
            checkingUpdates = true;
            revision++;
        }
        _ = Task.Run(async () =>
        {
            ReleaseCheck result;
            try { result = await UpdateChecker.CheckAsync(UpdateChecker.CurrentVersion); }
            catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
            { result = new(false, "", "", "Couldn't check for updates. Try again later."); }
            lock (gate) { update = result; checkingUpdates = false; revision++; }
        });
    }
    public void Refresh() => StartRefresh(false);
    public void RefreshSilently() => StartRefresh(true);
    void StartRefresh(bool silent)
    {
        long epoch;
        lock (gate)
        {
            if (busy) return;
            if (refreshing) { if (!silent) refreshRequested = true; return; }
            refreshing = true;
            epoch = refreshEpoch;
            if (!silent) { scanning = true; revision++; }
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var found = await adb.Detect(timeout.Token);
                lock (gate)
                {
                    if (busy || epoch != refreshEpoch) return;
                    var previous = devices.FirstOrDefault(d => d.Serial == selected);
                    bool changed = !devices.SequenceEqual(found);
                    devices = found;
                    if (!devices.Any(d => d.Serial == selected)) { selected = ""; installed = false; mods = []; }
                    var ready = devices.Where(d => d is { State: "device", IsQuest: true }).ToList();
                    if (selected.Length == 0 && ready.Count == 1) selected = ready[0].Serial;
                    hostInstalled = devices.FirstOrDefault(d => d.Serial == selected)?.HostInstalled ?? false;
                    installed = installed || hostInstalled;
                    if (step < 0 && (!silent || previous != devices.FirstOrDefault(d => d.Serial == selected)))
                    {
                        var current = devices.FirstOrDefault(d => d.Serial == selected);
                        status = devices.Count == 0 ? "No headset detected. Connect your Quest via USB."
                            : devices.Any(d => d.State == "no permissions") ? "Linux cannot access the headset. Install Android udev rules, then reconnect it."
                            : devices.Any(d => d.State == "unauthorized") ? "Put on your headset and allow USB debugging."
                            : ready.Count > 1 && selected.Length == 0 ? "Choose the headset you want to use."
                            : current is { Installed: true } && hostInstalled ? "Already patched."
                            : current is { Installed: true } ? "Ready."
                            : current != null ? "Ready to download Gorilla Tag from Steam."
                            : "No supported Quest is ready.";
                    }
                    if (!silent || changed) revision++;
                }
                string serial; lock (gate) serial = selected;
                if (found.Any(device => device.Serial == serial && device.State == "device" && device.IsQuest))
                {
                    var plugins = await new PluginService(adb).List(serial, timeout.Token);
                    lock (gate)
                    {
                        if (!busy && epoch == refreshEpoch && serial == selected && !mods.SequenceEqual(plugins))
                        { mods = plugins; revision++; }
                    }
                }
            }
            catch (Exception e)
            {
                lock (gate)
                {
                    if (!busy && epoch == refreshEpoch && !silent)
                    { error = e is OperationCanceledException ? "ADB detection timed out." : e.Message; revision++; }
                }
            }
            finally
            {
                bool repeat;
                lock (gate)
                {
                    refreshing = false;
                    repeat = refreshRequested;
                    refreshRequested = false;
                    if (scanning) { scanning = false; revision++; }
                }
                if (repeat) Refresh();
            }
        });
    }
    public void SelectDevice(string serial)
    {
        lock (gate) { if (busy || !devices.Any(d => d.Serial == serial)) return; refreshEpoch++; selected = serial; installed = false; mods = []; step = -1; error = ""; revision++; }
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
            if (devices.FirstOrDefault(d => d.Serial == selected) is not { State: "device", IsQuest: true })
            { error = "Choose an authorized Quest headset."; revision++; return; }
            refreshEpoch++;
            serial = selected;
            config = new() { LoaderPath = settings.LoaderPath, GeneratorPath = settings.GeneratorPath, UnityLibrariesPath = settings.UnityLibrariesPath };
            busy = true; installed = false; error = ""; step = 0; lastOutput = "";
            operation = new(); token = operation.Token; steam = new(); headless = false;
            steamLogin = true; steamDownloading = false; steamQr = ""; steamPrompt = ""; steamProgress = 0;
            status = "Sign in with Steam to download the latest Steam Frame build"; revision++;
        }
        _ = Task.Run(() => InstallAsync(serial, config, token));
    }
    internal async Task<int> RunHeadlessInstall(CancellationToken token)
    {
        var ready = (await adb.Detect(token))
            .Where(device => device is { State: "device", IsQuest: true }).ToList();
        if (ready.Count != 1)
        {
            string message = ready.Count == 0
                ? "Connect an authorized Quest headset."
                : "Multiple eligible headsets are connected. Pick one.";
            Log(message);
            Console.Error.WriteLine(message);
            return 1;
        }

        steam = new(); headless = true;
        await InstallAsync(ready[0].Serial, settings, token);
        Console.WriteLine(Snapshot());
        lock (gate) return installed && error.Length == 0 ? 0 : 1;
    }
    async Task InstallAsync(string serial, ManagerSettings config, CancellationToken token)
    {
        List<string>? restoreSet = null;
        bool officialAppRemoved = false;
        var dataBackup = new GameDataBackup(adb);
        var steamData = new GameDataBackup(adb);
        try
        {
            Stage(0, "Preparing..."); FusionHost.ValidateEnvironment(config);
            await new ApkMaker(runner).PrepareSigningKey(token, Log);
            var online = await adb.Detect(token);
            if (!online.Any(d => d.Serial == serial && d.State == "device" && d.IsQuest)) throw new IOException("The selected headset disconnected.");
            string work = Path.Combine(AppPaths.Data, "sessions", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(work); lock (gate) { workspace = work; revision++; }
            Stage(1, "Sign in with Steam to download the latest Steam Frame build");
            lock (gate) { steamLogin = true; steamProgress = 0; steamQr = ""; steamPrompt = ""; revision++; }
            var local = await steam!.Download(Path.Combine(work, "steam-frame"), Log,
                image => { lock (gate) { steamQr = image; revision++; } },
                value => { lock (gate) { steamProgress = value; status = value > 0 ? "Downloading Steam Frame build... " + value.ToString("F1") + "%" : "Downloading Steam Frame build..."; revision++; } },
                prompt => { lock (gate) { steamPrompt = prompt; revision++; } }, token, headless,
                () => { lock (gate) { steamLogin = false; steamDownloading = true; status = "Downloading Steam Frame build..."; revision++; } });
            lock (gate) { steamLogin = false; steamDownloading = false; steamQr = ""; steamPrompt = ""; revision++; }
            var apkInputs = ApkInspector.Inspect(local);
            string baseApk = apkInputs.Single(apk => apk.Manifest.Split.Length == 0).Path;
            bool requiresObb;
            using (var apk = ZipFile.OpenRead(baseApk)) requiresObb = apk.GetEntry("assets/unity_obb_guid") != null;
            steamData.UseDepotExpansion(Path.Combine(work, "steam-frame"), Path.Combine(work, "steam-data"), requiresObb);

            Stage(1, "Backing up the headset installation and plugins");
            var paths = await adb.PackagePaths(serial, token);
            string pulled = Path.Combine(work, "original"); Directory.CreateDirectory(pulled);
            if (paths.Count == 0) Log("Gorilla Tag is not installed; continuing without an APK backup.");
            restoreSet = [];
            for (int i = 0; i < paths.Count; ++i)
            {
                string file = Path.Combine(pulled, i.ToString("D2") + "-" + Path.GetFileName(paths[i]));
                await adb.Run(serial, ["pull", paths[i], file], token, Log); restoreSet.Add(file);
            }
            await dataBackup.Capture(serial, work, false, Log, token);
            var inputs = await DependencyGenerator.Extract(local, Path.Combine(work, "inputs"), token);
            Log("Detected Unity " + inputs.UnityVersion + ". Generating libs...");
            string libraries = await new DependencyGenerator(runner).Generate(inputs, work, config, Stage, Log, token);
            lock (gate) { lastOutput = work; revision++; }
            token.ThrowIfCancellationRequested(); Stage(3, "Building the payload");
            string patched = Path.Combine(work, "patched", "base.apk");
            GtagPayload.Build(baseApk, config.LoaderPath, libraries, patched, dataBackup.PluginsPath);
            var patchSet = apkInputs.Select(apk => apk.Manifest.Split.Length == 0 ? patched : apk.Path).ToList();
            Stage(4, "Aligning and signing the APK...");
            var signed = await new ApkMaker(runner).SignSet(patchSet, Path.Combine(work, "install"), token, Log);
            token.ThrowIfCancellationRequested(); Stage(5, "Installing the patched Steam Frame build...");
            if (restoreSet.Count > 0)
            {
                // Observe cancellation after uninstall so recovery can restore the package.
                await adb.Run(serial, ["uninstall", AppPaths.OriginalPackage], CancellationToken.None, Log);
                officialAppRemoved = true;
                token.ThrowIfCancellationRequested();
            }
            var install = await adb.Run(serial, ["install-multiple", "-r", "-g", .. signed], token, Log);
            if (!install.Output.Contains("Success", StringComparison.OrdinalIgnoreCase)) throw new IOException("ADB did not report a successful installation.");
            await steamData.RestoreObb(serial, Log, token);
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
                    if (AdbManager.ParsePaths(present.Output).Count > 0)
                        await adb.Run(serial, ["uninstall", AppPaths.OriginalPackage], CancellationToken.None, Log);
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
                    var present = await adb.Shell(serial, CancellationToken.None, false, "pm", "path", AppPaths.OriginalPackage);
                    if (AdbManager.ParsePaths(present.Output).Count > 0)
                        await adb.Run(serial, ["uninstall", AppPaths.OriginalPackage], CancellationToken.None, Log);
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
        finally { lock (gate) { busy = false; steamLogin = false; steamDownloading = false; steamQr = ""; steamPrompt = ""; steam = null; operation?.Dispose(); operation = null; revision++; } }
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
            refreshEpoch++;
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
    public void LoginWithPassword(string username, string password)
    {
        lock (gate) { if (busy && steamLogin) steam?.LoginWithPassword(username, password); }
    }
    public void SubmitSteamCode(string code)
    {
        lock (gate) { if (busy && steamLogin) { steam?.SendInput(code); steamPrompt = ""; revision++; } }
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
