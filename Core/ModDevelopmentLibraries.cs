using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml.Linq;

namespace PopeManager;

internal static class ModDevelopmentLibraries
{
    static readonly object Gate = new();
    const UnixFileMode ExecuteMode = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
    internal static string Root => Path.Combine(AppPaths.Data, "libraries");
    internal static string PropsPath => Path.Combine(Root, "ModLibraries.props");

    internal static void Prepare(ManagerSettings settings)
    {
        PublishRuntime(settings.LoaderPath);
        PublishAdb(AppContext.BaseDirectory);
        string sessions = Path.Combine(AppPaths.Data, "sessions");
        if (!Directory.Exists(sessions)) return;
        var generation = Directory.EnumerateDirectories(sessions)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .Select(path => Path.Combine(path, "generation"))
            .FirstOrDefault(path => File.Exists(Path.Combine(path, "PopeManager-generation.json"))
                && File.Exists(Path.Combine(path, "interop", "Assembly-CSharp.dll")));
        if (generation != null) PublishGame(generation);
    }

    internal static void PublishRuntime(string loaderApk)
    {
        using var apk = ZipFile.OpenRead(loaderApk);
        var entry = apk.GetEntry("assets/BepInEx-arm64.zip")
            ?? throw new IOException("The FusionCore APK has no BepInEx libraries.");
        using var buffer = new MemoryStream();
        using (var input = entry.Open()) input.CopyTo(buffer);
        string id = Convert.ToHexString(SHA256.HashData(buffer.ToArray())).ToLowerInvariant();
        string destination = Path.Combine(Root, "runtime-" + id);
        string[] required = ["BepInEx.Core.dll", "BepInEx.Unity.IL2CPP.dll", "Il2CppInterop.Runtime.dll", "0Harmony.dll"];
        destination = PublishSnapshot(destination, path => required.All(name => HasFile(Path.Combine(path, name))), staging =>
        {
            buffer.Position = 0;
            using var runtime = new ZipArchive(buffer, ZipArchiveMode.Read, true);
            foreach (var file in runtime.Entries.Where(file => file.FullName.StartsWith("core/", StringComparison.Ordinal) && file.Name.Length > 0))
            {
                string name = file.FullName[5..];
                ApkInspector.SafeName(name);
                string output = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                file.ExtractToFile(output);
            }
            foreach (var name in required)
                RequireFile(Path.Combine(staging, name));
        });
        UpdateProperty("FusionGeneratorDir", destination);
    }

    internal static void PublishGame(string generation)
    {
        var manifestBytes = File.ReadAllBytes(Path.Combine(generation, "PopeManager-generation.json"));
        var manifest = JsonSerializer.Deserialize<GeneratedManifest>(manifestBytes)
            ?? throw new IOException("Generated game library manifest is invalid.");
        string id = Convert.ToHexString(SHA256.HashData(manifestBytes)).ToLowerInvariant();
        string destination = Path.Combine(Root, "game-" + id);
        destination = PublishSnapshot(destination, path => HasFile(Path.Combine(path, "Assembly-CSharp.dll"))
            && manifest.Files.All(file => HasFile(Path.Combine(path, file.Name))
                && new FileInfo(Path.Combine(path, file.Name)).Length == file.Size), staging =>
        {
            foreach (var file in manifest.Files)
            {
                ApkInspector.SafeName(file.Name);
                if (file.Name.Contains('/')) throw new IOException("Invalid generated library name.");
                string source = Path.Combine(generation, "interop", file.Name);
                string output = Path.Combine(staging, file.Name);
                File.Copy(source, output);
                if (new FileInfo(output).Length != file.Size || DependencyGenerator.Hash(output) != file.Sha512)
                    throw new IOException("Generated library checksum mismatch: " + file.Name);
            }
            RequireFile(Path.Combine(staging, "Assembly-CSharp.dll"));
            File.WriteAllBytes(Path.Combine(staging, "PopeManager-generation.json"), manifestBytes);
        });
        UpdateProperty("GorillaTagInteropDir", destination);
    }

    internal static void PublishAdb(string managerDirectory)
    {
        string folder = OperatingSystem.IsWindows() ? "adb" : "adb-linux";
        string executable = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        string source = Path.Combine(managerDirectory, folder);
        if (!File.Exists(Path.Combine(source, executable))) return;
        string destination = Path.Combine(Root, "tools");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            string output = Path.Combine(destination, Path.GetFileName(file));
            // Avoid touching a cached executable while a template's ADB process is running.
            if (File.Exists(output) && FusionHost.Sha256(output) == FusionHost.Sha256(file)) continue;
            string pending = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.Copy(file, pending);
            if (!OperatingSystem.IsWindows()
                && (Path.GetFileName(file) == executable || (File.GetUnixFileMode(file) & ExecuteMode) != 0))
                File.SetUnixFileMode(pending, File.GetUnixFileMode(file) | ExecuteMode);
            File.Move(pending, output, true);
        }
        UpdateProperty("AdbPath", Path.Combine(destination, executable));
    }

    static void RequireFile(string path)
    {
        if (!HasFile(path))
            throw new IOException("Required development library is missing or empty: " + Path.GetFileName(path));
    }

    static bool HasFile(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    static string PublishSnapshot(string destination, Func<string, bool> usable, Action<string> populate)
    {
        lock (Gate)
        {
            if (File.Exists(Path.Combine(destination, ".complete")) && usable(destination)) return destination;
            Directory.CreateDirectory(Root);
            var repaired = Directory.EnumerateDirectories(Root, Path.GetFileName(destination) + "-repaired-*")
                .FirstOrDefault(path => File.Exists(Path.Combine(path, ".complete")) && usable(path));
            if (repaired != null) return repaired;
            if (Directory.Exists(destination)) destination += "-repaired-" + Guid.NewGuid().ToString("N");
            string staging = destination + "." + Guid.NewGuid().ToString("N") + ".pending";
            Directory.CreateDirectory(staging);
            populate(staging);
            File.WriteAllText(Path.Combine(staging, ".complete"), "ok\n");
            Directory.Move(staging, destination);
            return destination;
        }
    }

    static void UpdateProperty(string name, string value)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Root);
            var document = File.Exists(PropsPath) ? XDocument.Load(PropsPath) : new XDocument(new XElement("Project", new XElement("PropertyGroup")));
            var group = document.Root!.Element("PropertyGroup")!;
            var property = group.Element(name);
            if (property == null) { property = new XElement(name); group.Add(property); }
            property.SetAttributeValue("Condition", "'$(" + name + ")' == ''");
            property.Value = value;
            string pending = PropsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            document.Save(pending);
            File.Move(pending, PropsPath, true);
        }
    }
}
