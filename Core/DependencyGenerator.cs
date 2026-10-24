using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PopeManager;

internal class GeneratedManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Architecture { get; set; } = "arm64-v8a";
    public string UnityVersion { get; set; } = "";
    public string GameAssemblyHash { get; set; } = "";
    public string MetadataHash { get; set; } = "";
    public object Generator { get; set; } = new { App = "PopeManager", Runtime = "BepInExFusion", Platform = "Android ARM64" };
    public List<GeneratedFile> Files { get; set; } = [];
}
internal record GeneratedFile(string Name, string Sha512, long Size);
internal record GameInputs(string Binary, string Metadata, string UnityVersion);

internal class DependencyGenerator(ICommandRunner runner)
{
    internal static string Hash(string path)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexString(SHA512.HashData(stream));
    }
    internal static async Task<GameInputs> Extract(IReadOnlyList<string> apks, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination); var versions = new HashSet<string>();
        var found = new Dictionary<string, string>();
        foreach (var apk in apks)
        {
            using var zip = ZipFile.OpenRead(apk);
            foreach (var entry in zip.Entries)
            {
                string? target = entry.FullName switch
                {
                    "lib/arm64-v8a/libil2cpp.so" => "libil2cpp.so",
                    "assets/bin/Data/Managed/Metadata/global-metadata.dat" or
                    "assets/bin/Data/il2cpp_data/Metadata/global-metadata.dat" or
                    "assets/bin/Data/il2cpp/Metadata/global-metadata.dat" => "global-metadata.dat",
                    _ => null
                };
                if (target != null)
                {
                    string path = Path.Combine(destination, target), temp = path + ".part";
                    await using (var input = entry.Open())
                    await using (var output = File.Create(temp)) await input.CopyToAsync(output, token);
                    string hash = Hash(temp);
                    if (found.TryGetValue(target, out var previous) && previous != hash) throw new InvalidDataException("Invalid IL2CPP data.");
                    File.Move(temp, path, true); found[target] = hash;
                }
                if (entry.FullName is "assets/bin/Data/globalgamemanagers" or "assets/bin/Data/data.unity3d")
                {
                    using var input = entry.Open(); var bytes = new byte[256]; int count = 0, read;
                    while (count < bytes.Length && (read = await input.ReadAsync(bytes.AsMemory(count), token)) > 0) count += read;
                    foreach (Match match in Regex.Matches(System.Text.Encoding.ASCII.GetString(bytes, 0, count), @"(?:6000|20[12][0-9])\.\d+\.\d+[abcfpx]\d+(?:c\d+)?")) versions.Add(match.Value);
                }
            }
        }
        if (!found.ContainsKey("libil2cpp.so") || !found.ContainsKey("global-metadata.dat")) throw new InvalidDataException("Missing ARM64 binary/metadata.");
        if (versions.Count != 1) throw new InvalidDataException("Could not find Unity version.");
        string binary = Path.Combine(destination, "libil2cpp.so"), metadata = Path.Combine(destination, "global-metadata.dat");
        ApkInspector.CheckArm64(binary);
        using var stream = File.OpenRead(metadata); var header = new byte[8]; stream.ReadExactly(header);
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header) != 0xFAB11BAF) throw new InvalidDataException("Invalid metadata.");
        return new(binary, metadata, versions.Single());
    }
    internal async Task<string> Generate(GameInputs inputs, string workspace, ManagerSettings settings,
        Action<int, string> stage, Action<string> log, CancellationToken token)
    {
        string unity = settings.UnityLibrariesPath;
        if (string.IsNullOrWhiteSpace(unity))
        {
            stage(2, "Downloading unity libs...");
            string numeric = Regex.Match(inputs.UnityVersion, @"^\d+\.\d+\.\d+").Value;
            string url = $"https://unity.bepinex.dev/libraries/{numeric}.zip";
            unity = Path.Combine(workspace, "unity-download"); Directory.CreateDirectory(unity);
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            if (!response.IsSuccessStatusCode) throw new IOException($"Unity {numeric} libs are unavailable.");
            string archivePath = Path.Combine(workspace, "unity.zip");
            await using (var input = await response.Content.ReadAsStreamAsync(token))
            await using (var output = File.Create(archivePath)) await input.CopyToAsync(output, token);
            using var zip = ZipFile.OpenRead(archivePath); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries.Where(e => e.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))
            {
                ApkInspector.SafeName(entry.Name);
                await using var input = entry.Open(); await using var output = File.Create(Path.Combine(unity, entry.Name));
                await input.CopyToAsync(output, token);
            }
        }
        string outputDir = Path.Combine(workspace, "generation");
        string references = Path.Combine(outputDir, "unity-libs"); Directory.CreateDirectory(references);
        foreach (string file in Directory.EnumerateFiles(unity, "*.dll"))
        { ApkInspector.SafeName(Path.GetFileName(file)); File.Copy(file, Path.Combine(references, Path.GetFileName(file))); }
        var generator = Path.Combine(settings.GeneratorPath, "FusionGenerator.dll");
        var dotnet = AppPaths.FindTool("dotnet");
        stage(2, "Generating FusionCore libs...");
        await runner.RunAsync(dotnet, [generator, inputs.Binary, inputs.Metadata, inputs.UnityVersion, references, outputDir], token, log, workingDirectory: settings.GeneratorPath);
        string interop = Path.Combine(outputDir, "interop");
        if (!File.Exists(Path.Combine(interop, "Assembly-CSharp.dll"))) throw new IOException("Fusion generation produced no game assembly.");
        await File.WriteAllTextAsync(Path.Combine(outputDir, "PopeManager-generation.json"),
            JsonSerializer.Serialize(CreateManifest(inputs, interop), new JsonSerializerOptions { WriteIndented = true }), token);
        ModDevelopmentLibraries.PublishGame(outputDir);
        log("Mod development libraries saved to " + ModDevelopmentLibraries.Root);
        return outputDir;
    }
    internal static GeneratedManifest CreateManifest(GameInputs inputs, string libraries)
    {
        var manifest = new GeneratedManifest { UnityVersion = inputs.UnityVersion, GameAssemblyHash = Hash(inputs.Binary), MetadataHash = Hash(inputs.Metadata) };
        foreach (var path in Directory.EnumerateFiles(libraries).Order())
        {
            var file = new FileInfo(path); if (file.Length == 0) throw new InvalidDataException("A generated file is empty.");
            manifest.Files.Add(new(file.Name, Hash(path), file.Length));
        }
        return manifest;
    }
}
