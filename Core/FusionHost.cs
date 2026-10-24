using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PopeManager;

internal class FusionHost(AdbManager adb)
{
    internal static string Sha256(string path)
    { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    internal static void ValidateEnvironment(ManagerSettings settings)
    {
        _ = AppPaths.FindTool("dotnet");
        using var apk = ZipFile.OpenRead(settings.LoaderPath);
        string receipt = Path.Combine(settings.GeneratorPath, "host-receipt.json");
        using var parsed = JsonDocument.Parse(File.ReadAllText(receipt));
        using var payloadStream = apk.GetEntry("assets/BepInEx-arm64.zip")!.Open();
        using var buffer = new MemoryStream(); payloadStream.CopyTo(buffer); buffer.Position = 0;
        using var payload = new ZipArchive(buffer);
        foreach (var entry in payload.Entries.Where(e => e.FullName.StartsWith("core/") && e.Name.EndsWith(".dll")))
        {
            string file = Path.Combine(settings.GeneratorPath, entry.Name);
            if (!File.Exists(file)) throw new IOException("Missing Fusion generator dependency: " + entry.Name);
            using var input = entry.Open(); string expected = Convert.ToHexString(SHA256.HashData(input));
        }
    }

    internal async Task UploadAndCommit(string serial, string generation, string workspace,
        IReadOnlyList<string> originalPaths, Action<int, string> stage, Action<string> log, CancellationToken token)
    {
        string id = Path.GetFileName(workspace);
        string remote = AppPaths.RemoteGameRoot + "/PopeManager/generations/" + id;
        var files = Directory.EnumerateFiles(generation, "*", SearchOption.AllDirectories)
            .Select(path => (Path: path, Name: Path.GetRelativePath(generation, path).Replace('\\', '/'))).OrderBy(x => x.Name).ToList();
        foreach (var file in files) ApkInspector.SafeName(file.Name);
        string checksum = Path.Combine(generation, "SHA256SUMS");
        await File.WriteAllTextAsync(checksum, string.Join('\n', files.Select(file => Sha256(file.Path) + "  " + file.Name)) + "\n", new UTF8Encoding(false), token);
        stage(4, "Transferring generated libs...");
        await adb.Shell(serial, token, true, "mkdir", "-p", remote);
        await adb.Run(serial, ["push", generation + Path.DirectorySeparatorChar + ".", remote + "/"], token, log);
        await adb.Run(serial, ["shell", "cd " + AdbManager.Quote(remote) + " && sha256sum -c SHA256SUMS"], token);

        var currentPaths = AdbManager.ParsePaths((await adb.Shell(serial, token, true, "pm", "path", AppPaths.OriginalPackage)).Output);
        string configDir = AppPaths.RemoteGameRoot + "/BepInEx/config", config = configDir + "/BepInEx.cfg";
        string localConfig = Path.Combine(workspace, "BepInEx.cfg"), text = "";
        await adb.Shell(serial, token, true, "am", "force-stop", AppPaths.TargetPackage);
        if ((await adb.Shell(serial, token, false, "test", "-f", config)).ExitCode == 0)
        {
            string backup = Path.Combine(workspace, "BepInEx.cfg.before");
            await adb.Run(serial, ["pull", config, backup], token);
            text = await File.ReadAllTextAsync(backup, token);
            await adb.Shell(serial, token, true, "cp", config, config + ".PopeManager-backup-" + id);
        }
        text = IniConfig.SetValues(text, "IL2CPP", new Dictionary<string, string>
        {
            ["IL2CPPInteropAssembliesPath"] = remote,
            ["UpdateInteropAssemblies"] = "false"
        });
        await File.WriteAllTextAsync(localConfig, text, new UTF8Encoding(false), token);
        await adb.Shell(serial, token, true, "mkdir", "-p", configDir);
        string pending = config + ".PopeManager-" + id + ".tmp";
        await adb.Run(serial, ["push", localConfig, pending], token);
        var remoteHash = (await adb.Shell(serial, token, true, "sha256sum", pending)).Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        token.ThrowIfCancellationRequested();
        stage(5, "Activating verified PC libraries");
        await adb.Shell(serial, token, true, "mv", pending, config);
        log("Activated " + files.Count + " verified files in " + remote);
    }
}
