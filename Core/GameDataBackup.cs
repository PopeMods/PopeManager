namespace PopeManager;

/// <summary>Preserves expansion data and plugins before an APK signing-key replacement.</summary>
internal sealed class GameDataBackup(AdbManager adb)
{
    const string ObbRoot = "/sdcard/Android/obb/" + AppPaths.OriginalPackage;
    internal string DirectoryPath { get; private set; } = "";
    internal string PluginsPath => Path.Combine(DirectoryPath, "plugins");
    readonly List<string> obbNames = [];

    internal async Task Capture(string serial, string workspace, bool requiresObb,
        Action<string> log, CancellationToken token)
    {
        DirectoryPath = Path.Combine(workspace, "game-data-backup");
        Directory.CreateDirectory(DirectoryPath);
        obbNames.AddRange(await CaptureFolder(serial, ObbRoot, "obb", name => name.EndsWith(".obb", StringComparison.OrdinalIgnoreCase), log, token));
        if (requiresObb && obbNames.Count == 0)
            throw new IOException("Gorilla Tag's expansion data is missing. Restore the original game and launch it to finish downloading its OBB files before patching. The game has not been uninstalled.");
        await CaptureFolder(serial, AppPaths.RemoteGameRoot + "/BepInEx/plugins", "plugins",
            name => name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase), log, token);
    }

    async Task<List<string>> CaptureFolder(string serial, string remote, string folder,
        Func<string, bool> include, Action<string> log, CancellationToken token)
    {
        if ((await adb.Shell(serial, token, false, "test", "-d", remote)).ExitCode != 0) return [];
        var listing = await adb.Shell(serial, token, true, "find", remote, "-maxdepth", "1", "-type", "f");
        var names = new List<string>();
        foreach (var path in listing.Output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0))
        {
            if (!path.StartsWith(remote + "/", StringComparison.Ordinal)) throw new IOException("Unexpected game-data backup path.");
            var name = path[(remote.Length + 1)..];
            ApkInspector.SafeName(name);
            if (name.Contains('/') || !include(name)) continue;
            var local = Path.Combine(DirectoryPath, folder, name);
            Directory.CreateDirectory(Path.GetDirectoryName(local)!);
            log("Backing up " + name);
            await adb.Run(serial, ["pull", path, local], token, log);
            await Verify(serial, path, local, token);
            names.Add(name);
        }
        return names;
    }

    static string Hash(string path) => FusionHost.Sha256(path);
    async Task Verify(string serial, string remote, string local, CancellationToken token)
    {
        var result = await adb.Shell(serial, token, true, "sha256sum", remote);
        var actual = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(actual, Hash(local), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Game-data checksum mismatch: " + Path.GetFileName(local));
    }

    internal async Task RestoreObb(string serial, Action<string> log, CancellationToken token)
    {
        if (obbNames.Count == 0) return;
        // Stage through shell-owned /data/local/tmp: direct adb push to an
        // app-owned directory may fail its fchown step on Quest.
        string staging = "/data/local/tmp/popemanager-obb-" + Guid.NewGuid().ToString("N");
        await adb.Shell(serial, token, true, "mkdir", "-p", staging, ObbRoot);
        foreach (var name in obbNames)
        {
            string local = Path.Combine(DirectoryPath, "obb", name);
            string pending = staging + "/" + name;
            string remote = ObbRoot + "/" + name;
            await adb.Run(serial, ["push", local, pending], token, log);
            await adb.Shell(serial, token, true, "cp", pending, remote);
            await adb.Shell(serial, token, true, "chmod", "0644", remote);
            await Verify(serial, remote, local, token);
            await adb.Shell(serial, token, true, "rm", pending);
            log("Restored and verified " + name);
        }
        await adb.Shell(serial, token, true, "rmdir", staging);
    }
}
