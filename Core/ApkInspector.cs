using System.Buffers.Binary;
using System.IO.Compression;

namespace PopeManager;

internal record ApkInput(string Path, AndroidManifest Manifest, HashSet<string> Entries);

internal static class ApkInspector
{
    internal static void SafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.IndexOfAny(['\\', ':', '\r', '\n']) >= 0 || name.StartsWith('/') ||
            name.Split('/').Any(p => p is ".." or "." or "")) throw new InvalidDataException("Unsafe archive entry: " + name);
    }
    internal static void CheckArm64(string path)
    {
        using var stream = File.OpenRead(path); var header = new byte[64]; stream.ReadExactly(header);
        if (!header.AsSpan(0, 6).SequenceEqual(new byte[] { 0x7f, 69, 76, 70, 2, 1 }) || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18)) != 183)
            throw new InvalidDataException("Expected an ARM64 ELF lib: " + Path.GetFileName(path));
    }
    internal static AndroidManifest ReadManifest(ZipArchive zip)
    {
        using var stream = zip.GetEntry("AndroidManifest.xml")?.Open() ?? throw new InvalidDataException("APK manifest is missing.");
        using var buffer = new MemoryStream(); stream.CopyTo(buffer); return new(buffer.ToArray());
    }
    internal static List<ApkInput> Inspect(IReadOnlyList<string> paths)
    {
        var items = new List<ApkInput>();
        foreach (string path in paths)
        {
            using var zip = ZipFile.OpenRead(path); var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in zip.Entries.Where(e => e.Name.Length != 0))
            { SafeName(entry.FullName); if (!names.Add(entry.FullName)) throw new InvalidDataException("Duplicate apk zip entries."); }
            if (names.Contains("assets/lemon_patch_date.txt") || names.Any(n => n.StartsWith("assets/MelonLoader/") || n.EndsWith("/libBootstrap.so")))
                throw new IOException("The game is already patched.");
            items.Add(new(path, ReadManifest(zip), names));
        }
        var bases = items.Where(i => i.Manifest.Split.Length == 0).ToList();
        if (bases.Count != 1) throw new InvalidDataException("Expected exactly one base APK.");
        if (items.Any(i => i.Manifest.Package != AppPaths.OriginalPackage || i.Manifest.VersionCode != bases[0].Manifest.VersionCode))
            throw new InvalidDataException("APK package/version mismatch.");
        if (items.Select(i => i.Manifest.Split).Distinct().Count() != items.Count) throw new InvalidDataException("Duplicate APK splits.");
        foreach (string name in new[] { "libil2cpp.so", "libunity.so" })
            if (items.Count(i => i.Entries.Contains("lib/arm64-v8a/" + name)) != 1)
                throw new InvalidDataException("The installed APK set has no unique ARM64 " + name);
        return items;
    }
}
