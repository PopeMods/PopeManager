using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace PopeManager;

internal record Headset(string Serial, string Name, string State, bool IsQuest, bool Installed, string Version, bool HostInstalled = false);

internal class AdbManager(ICommandRunner runner, string? executableOverride = null)
{
    string? executable;

    internal string Executable => executable ??= ResolveExecutable();

    string ResolveExecutable()
    {
        if (executableOverride != null) { EnsureExecutable(executableOverride); return executableOverride; }
        string? bundled = BundledExecutable();
        if (bundled != null) { EnsureExecutable(bundled); return bundled; }
        string found = AppPaths.FindTool("adb");
        EnsureExecutable(found);
        return found;
    }

    static string? BundledExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            string windows = Path.Combine(AppContext.BaseDirectory, "adb", "adb.exe");
            return File.Exists(windows) ? windows : null;
        }
        if (!OperatingSystem.IsLinux() || RuntimeInformation.OSArchitecture != Architecture.X64) return null;
        string linux = Path.Combine(AppContext.BaseDirectory, "adb-linux", "adb");
        return File.Exists(linux) ? linux : null;
    }

    internal static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            const UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            UnixFileMode mode = File.GetUnixFileMode(path);
            if ((mode & execute) != execute) File.SetUnixFileMode(path, mode | execute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException) { }
    }
    internal Task<CommandResult> Run(string serial, IEnumerable<string> args, CancellationToken token,
        Action<string>? log = null, bool check = true) => runner.RunAsync(Executable, new[] { "-s", serial }.Concat(args), token, log, check);
    internal static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
    internal Task<CommandResult> Shell(string serial, CancellationToken token, bool check, params string[] args) =>
        Run(serial, ["shell", string.Join(" ", args.Select(Quote))], token, check: check);

    internal async Task<List<string>> PackagePaths(string serial, CancellationToken token)
    {
        var result = await Shell(serial, token, false, "pm", "path", AppPaths.OriginalPackage);
        if (result.ExitCode == 0) return ParsePaths(result.Output);
        if (result.ExitCode == 1 && string.IsNullOrWhiteSpace(result.Output))
        {
            var state = await Run(serial, ["get-state"], token, check: false);
            if (state.ExitCode == 0 && state.Output.Trim() == "device") return [];
            throw new IOException("The headset disconnected while checking Gorilla Tag." + state.Output.Trim());
        }
        throw new IOException("Could not read Gorilla Tag's installed APK paths: " + result.Output.Trim());
    }

    internal static List<(string Serial, string State)> ParseDevices(string output) => output.Split('\n')
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && !line.StartsWith("List of devices", StringComparison.Ordinal) && !line.StartsWith('*'))
        .Select(line =>
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            string state = parts.Length >= 2 && parts[1] == "no" && line.Contains("no permissions", StringComparison.Ordinal)
                ? "no permissions"
                : parts.Length >= 2 ? parts[1] : "";
            return (parts[0], state);
        })
        .Where(device => device.Item2 is "device" or "offline" or "unauthorized" or "recovery" or "sideload" or "no permissions")
        .ToList();

    internal async Task<List<Headset>> Detect(CancellationToken token)
    {
        CommandResult result;
        try { result = await runner.RunAsync(Executable, ["devices", "-l"], token); }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new IOException($"Could not start ADB at \"{Executable}\". {e.Message}", e);
        }
        var list = new List<Headset>();
        foreach (var (serial, state) in ParseDevices(result.Output))
        {
            if (state != "device") { list.Add(new(serial, serial, state, false, false, "")); continue; }
            var model = (await Shell(serial, token, true, "getprop", "ro.product.model")).Output.Trim();
            var maker = (await Shell(serial, token, true, "getprop", "ro.product.manufacturer")).Output.Trim();
            bool quest = model.Contains("Quest", StringComparison.OrdinalIgnoreCase)
                || maker.Equals("Oculus", StringComparison.OrdinalIgnoreCase) || maker.Equals("Meta", StringComparison.OrdinalIgnoreCase);
            var paths = await Shell(serial, token, false, "pm", "path", AppPaths.OriginalPackage);
            var packagePaths = ParsePaths(paths.Output);
            bool installed = packagePaths.Count > 0;
            string version = "";
            if (installed)
            {
                var dump = await Shell(serial, token, false, "dumpsys", "package", AppPaths.OriginalPackage);
                version = Regex.Match(dump.Output, @"versionName=([^\s]+)").Groups[1].Value;
            }
            bool patched = false;
            if (installed)
            {
                foreach (string apk in packagePaths)
                {
                    var entry = await Shell(serial, token, false, "unzip", "-l", apk, "lib/arm64-v8a/libfusion.so");
                    if (entry.Output.Contains("lib/arm64-v8a/libfusion.so", StringComparison.Ordinal))
                    {
                        patched = true;
                        break;
                    }
                }
            }
            list.Add(new(serial, model.Replace('_', ' '), "device", quest, installed, version, patched));
        }
        return list;
    }
    internal static List<string> ParsePaths(string text) => text.Split('\n')
        .Select(s => s.Trim()).Where(s => s.StartsWith("package:/", StringComparison.Ordinal))
        .Select(s => s[8..]).Where(s => s.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
}
