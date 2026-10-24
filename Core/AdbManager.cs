using System.Text.RegularExpressions;

namespace PopeManager;

internal record Headset(string Serial, string Name, string State, bool IsQuest, bool Installed, string Version, bool HostInstalled = false);

internal class AdbManager(ICommandRunner runner, string? executableOverride = null)
{
    internal string Executable
    {
        get
        {
            if (executableOverride != null) return executableOverride;
            string folder = OperatingSystem.IsWindows() ? "adb" : "adb-linux";
            string file = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
            var bundled = Path.Combine(AppContext.BaseDirectory, folder, file);
            return File.Exists(bundled) ? bundled : AppPaths.FindTool("adb");
        }
    }
    internal Task<CommandResult> Run(string serial, IEnumerable<string> args, CancellationToken token,
        Action<string>? log = null, bool check = true) => runner.RunAsync(Executable, new[] { "-s", serial }.Concat(args), token, log, check);
    internal static string Quote(string text) => "'" + text.Replace("'", "'\"'\"'") + "'";
    internal Task<CommandResult> Shell(string serial, CancellationToken token, bool check, params string[] args) =>
        Run(serial, ["shell", string.Join(" ", args.Select(Quote))], token, check: check);

    internal static List<(string Serial, string State)> ParseDevices(string output) => output.Split('\n')
        .Select(line => line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .Where(parts => parts.Length >= 2 && parts[0] != "List" && parts[0] != "*"
            && parts[1] is "device" or "offline" or "unauthorized" or "recovery" or "sideload")
        .Select(parts => (parts[0], parts[1])).ToList();

    internal async Task<List<Headset>> Detect(CancellationToken token)
    {
        var result = await runner.RunAsync(Executable, ["devices", "-l"], token);
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
