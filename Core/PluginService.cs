using System.Reflection.PortableExecutable;

namespace PopeManager;

internal record PluginEntry(string FileName, string Name, bool Enabled);

internal class PluginService(AdbManager adb)
{
    internal const string Root = AppPaths.RemoteGameRoot + "/BepInEx/plugins";
    internal static string ValidateName(string name)
    {
        ApkInspector.SafeName(name);
        if (name.Contains('/') || !(name.EndsWith(".dll", StringComparison.Ordinal) || name.EndsWith(".dll.disabled", StringComparison.Ordinal)))
            throw new InvalidDataException("Invalid plugin filename.");
        return name;
    }
    internal async Task<List<PluginEntry>> List(string serial, CancellationToken token)
    {
        var result = await adb.Shell(serial, token, false, "find", Root, "-maxdepth", "1", "-type", "f");
        if (result.ExitCode != 0)
        {
            if (result.Output.Contains("No such file", StringComparison.OrdinalIgnoreCase)) return [];
            throw new IOException("Couldn't read mods: " + result.Output.Trim());
        }
        return result.Output.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.StartsWith(Root + "/", StringComparison.Ordinal))
            .Select(line => line[(Root.Length + 1)..]).Where(name => name.EndsWith(".dll", StringComparison.Ordinal) || name.EndsWith(".dll.disabled", StringComparison.Ordinal))
            .Select(ValidateName).Select(name => new PluginEntry(name, name.EndsWith(".disabled") ? name[..^13] : name[..^4], !name.EndsWith(".disabled")))
            .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }
    internal async Task Import(string serial, string fileUrl, CancellationToken token)
    {
        string path = Uri.TryCreate(fileUrl, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : fileUrl;
        path = Path.GetFullPath(path);
        if (!path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) throw new IOException("Choose a BepInEx plugin DLL.");
        using (var stream = File.OpenRead(path))
        using (var pe = new PEReader(stream))
            if (!pe.HasMetadata) throw new IOException("The selected file is not a managed plugin DLL.");
        string name = ValidateName(Path.GetFileNameWithoutExtension(path) + ".dll");
        var existing = await List(serial, token);
        if (existing.Any(plugin => plugin.Name.Equals(Path.GetFileNameWithoutExtension(path), StringComparison.OrdinalIgnoreCase)))
            throw new IOException("That mod is already installed.");
        await adb.Shell(serial, token, true, "mkdir", "-p", Root);
        string pending = Root + "/.PopeManager-" + Guid.NewGuid().ToString("N") + ".partial";
        await adb.Run(serial, ["push", path, pending], token);
        string? hash = (await adb.Shell(serial, token, true, "sha256sum", pending)).Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (!string.Equals(hash, FusionHost.Sha256(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("Mod transfer verification failed.");
        await adb.Shell(serial, token, true, "mv", pending, Root + "/" + name);
    }
    internal Task<CommandResult> SetEnabled(string serial, string name, bool enabled, CancellationToken token)
    {
        ValidateName(name);
        string target = enabled ? name.EndsWith(".disabled") ? name[..^9] : name : name.EndsWith(".disabled") ? name : name + ".disabled";
        if (name == target) return Task.FromResult(new CommandResult(0, ""));
        return adb.Shell(serial, token, true, "mv", Root + "/" + name, Root + "/" + target);
    }
    internal async Task Remove(string serial, string name, CancellationToken token)
    {
        ValidateName(name);
        string removed = AppPaths.RemoteGameRoot + "/PopeManager/removed-mods";
        await adb.Shell(serial, token, true, "mkdir", "-p", removed);
        await adb.Shell(serial, token, true, "mv", Root + "/" + name, removed + "/" + Guid.NewGuid().ToString("N") + "-" + name);
    }
}
