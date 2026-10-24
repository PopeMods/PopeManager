using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace PopeManager;

internal static class GtagPayload
{
    internal static void Build(string baseApk, string fusionApk, string generatedLibraries, string outputApk, string? pluginsPath = null)
    {
        if (!Directory.Exists(Path.Combine(generatedLibraries, "interop")))
            throw new InvalidDataException("Il2CPP libs are missing.");

        using var source = ZipFile.OpenRead(baseApk);
        using var fusion = ZipFile.OpenRead(fusionApk);

        string temporary = outputApk + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(outputApk)!);
        if (File.Exists(temporary)) File.Delete(temporary);
        using (var destination = ZipFile.Open(temporary, ZipArchiveMode.Create))
        {
            var replacements = fusion.Entries
                .Where(e => e.FullName.StartsWith("lib/arm64-v8a/", StringComparison.Ordinal) && e.FullName.EndsWith(".so", StringComparison.Ordinal))
                .Select(e => e.FullName).ToHashSet(StringComparer.Ordinal);
            foreach (var entry in source.Entries)
            {
                ApkInspector.SafeName(entry.FullName);
                if (replacements.Contains(entry.FullName)) continue;
                if (entry.FullName.StartsWith("assets/PopeMods/runtime/", StringComparison.Ordinal)) continue;
                var compression = entry.FullName == "resources.arsc" ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                Copy(entry, destination.CreateEntry(entry.FullName, compression));
            }
            foreach (string name in replacements.OrderBy(n => n, StringComparer.Ordinal))
            {
                var entry = fusion.GetEntry(name) ?? throw new InvalidDataException("Fusion payload is missing " + name);
                Copy(entry, destination.CreateEntry(name, CompressionLevel.NoCompression));
            }
            ExpandRuntime(fusion, "assets/BepInEx-arm64.zip", "assets/PopeMods/runtime/BepInEx/", destination, generatedLibraries);
            ExpandRuntime(fusion, "assets/dotnet-arm64.zip", "assets/PopeMods/runtime/dotnet/", destination, null);
            if (pluginsPath != null && Directory.Exists(pluginsPath))
            {
                foreach (var file in Directory.EnumerateFiles(pluginsPath))
                {
                    var name = Path.GetFileName(file);
                    ApkInspector.SafeName(name);
                    if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".dll.disabled", StringComparison.OrdinalIgnoreCase)) continue;
                    using var input = File.OpenRead(file);
                    using var output = destination.CreateEntry("assets/PopeMods/runtime/BepInEx/plugins/" + name, CompressionLevel.Optimal).Open();
                    input.CopyTo(output);
                }
            }
        }
        File.Move(temporary, outputApk, true);
    }

    static void ExpandRuntime(ZipArchive fusion, string archiveName, string targetRoot, ZipArchive output, string? generated)
    {
        var input = fusion.GetEntry(archiveName) ?? throw new InvalidDataException("Fusion payload is missing " + archiveName);
        using (var zip = new ZipArchive(input.Open(), ZipArchiveMode.Read))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0 || entry.FullName == "config/BepInEx.cfg") continue;
                ApkInspector.SafeName(entry.FullName);
                Copy(entry, output.CreateEntry(targetRoot + entry.FullName, CompressionLevel.Optimal));
            }
            if (generated != null)
            {
                using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(generated, "PopeManager-generation.json")));
                string unityVersion = manifest.RootElement.GetProperty("UnityVersion").GetString()
                    ?? throw new InvalidDataException("Generated libraries have no Unity version.");
                using (var writer = new StreamWriter(output.CreateEntry(targetRoot + "UnityVersion.txt").Open(), new UTF8Encoding(false))) writer.Write(unityVersion);
                string config = "[IL2CPP]\nUpdateInteropAssemblies = false\nIL2CPPInteropAssembliesPath = {BepInEx}\nPreloadIL2CPPInteropAssemblies = true\n";
                using (var writer = new StreamWriter(output.CreateEntry(targetRoot + "config/BepInEx.cfg").Open(), new UTF8Encoding(false))) writer.Write(config);
                foreach (string file in Directory.EnumerateFiles(Path.Combine(generated, "interop"), "*.dll", SearchOption.TopDirectoryOnly))
                {
                    using var from = File.OpenRead(file);
                    using var destination = output.CreateEntry(targetRoot + "interop/" + Path.GetFileName(file), CompressionLevel.Optimal).Open();
                    from.CopyTo(destination);
                }
            }
        }
    }
    static void Copy(ZipArchiveEntry source, ZipArchiveEntry destination)
    { using var input = source.Open(); using var output = destination.Open(); input.CopyTo(output); }
}
