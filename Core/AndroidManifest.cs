using System.Buffers.Binary;
using System.Text;

namespace PopeManager;

internal class AndroidManifest
{
    const uint None = uint.MaxValue;
    const string Android = "http://schemas.android.com/apk/res/android";
    readonly List<(ushort Type, byte[] Data)> chunks = [];
    readonly List<string> strings = [];
    readonly List<uint> resources = [];
    readonly bool utf8;
    readonly byte[] styleOffsets, styleData;
    readonly uint poolFlags;
    internal string Package { get; }
    internal string Split { get; } = "";
    internal long VersionCode { get; }
    internal string UnityVersion { get; set; } = "";
    internal bool HasActivityCategory(string activityName, string category)
    {
        string? activity = null;
        foreach (var chunk in chunks)
        {
            if (chunk.Type == 0x102 && NodeName(chunk.Data) == "activity")
                activity = Text(Attributes(chunk.Data).FirstOrDefault(a => Name(a) == "name"));
            if (chunk.Type == 0x103 && NodeName(chunk.Data) == "activity") activity = null;
            if (chunk.Type == 0x102 && NodeName(chunk.Data) == "category" && activity == activityName &&
                Text(Attributes(chunk.Data).FirstOrDefault(a => Name(a) == "name")) == category) return true;
        }
        return false;
    }
    static ushort U16(byte[] b, int p) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(p, 2));
    static uint U32(byte[] b, int p) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(p, 4));
    static void Put16(byte[] b, int p, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p, 2), v);
    static void Put32(byte[] b, int p, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p, 4), v);

    internal AndroidManifest(byte[] data)
    {
        if (data.Length < 8 || U16(data, 0) != 3 || U16(data, 2) != 8 || U32(data, 4) != data.Length)
            throw new InvalidDataException("The APK has an invalid manifest.");
        for (int p = 8; p < data.Length;)
        {
            if (p + 8 > data.Length) throw new InvalidDataException("Truncated XML chunk.");
            int size = checked((int)U32(data, p + 4));
            if (size < U16(data, p + 2) || size < 8 || size > data.Length - p)
                throw new InvalidDataException("Invalid XML chunk bounds.");
            chunks.Add((U16(data, p), data[p..(p + size)])); p += size;
        }
        var pools = chunks.Where(c => c.Type == 1).ToList();
        if (pools.Count != 1) throw new InvalidDataException("Unsupported string pool.");
        var pool = pools[0].Data;
        if (U16(pool, 2) != 28) throw new InvalidDataException("Unsupported string pool header.");
        int count = checked((int)U32(pool, 8)), styles = checked((int)U32(pool, 12));
        poolFlags = U32(pool, 16); utf8 = (poolFlags & 0x100) != 0;
        int start = checked((int)U32(pool, 20)), styleStart = checked((int)U32(pool, 24));
        styleOffsets = pool[(28 + 4 * count)..(28 + 4 * (count + styles))];
        styleData = styleStart == 0 ? [] : pool[styleStart..];
        for (int i = 0; i < count; ++i)
        {
            int p = checked(start + (int)U32(pool, 28 + 4 * i));
            int units = ReadLength(pool, ref p, utf8);
            int bytes = utf8 ? ReadLength(pool, ref p, true) : checked(units * 2);
            strings.Add((utf8 ? Encoding.UTF8 : Encoding.Unicode).GetString(pool, p, bytes));
        }
        foreach (var (_, map) in chunks.Where(c => c.Type == 0x180))
            for (int p = 8; p < map.Length; p += 4) resources.Add(U32(map, p));
        var root = chunks.First(c => c.Type == 0x102 && NodeName(c.Data) == "manifest").Data;
        var attrs = Attributes(root);
        Package = Text(attrs.First(a => Name(a) == "package")) ?? throw new InvalidDataException("Manifest package is missing.");
        Split = Text(attrs.FirstOrDefault(a => Name(a) == "split")) ?? "";
        uint low = attrs.FirstOrDefault(a => Name(a) == "versionCode")?.Data ?? 0;
        uint high = attrs.FirstOrDefault(a => Name(a) == "versionCodeMajor")?.Data ?? 0;
        VersionCode = ((long)high << 32) | low;
    }
    static int ReadLength(byte[] bytes, ref int p, bool utf8)
    {
        if (utf8) { int a = bytes[p++]; return (a & 128) == 0 ? a : ((a & 127) << 8) | bytes[p++]; }
        int n = U16(bytes, p); p += 2;
        if ((n & 0x8000) == 0) return n;
        int tail = U16(bytes, p); p += 2; return ((n & 0x7fff) << 16) | tail;
    }
    static void WriteLength(BinaryWriter w, int value, bool utf8)
    {
        if (utf8) { if (value > 127) w.Write((byte)((value >> 8) | 128)); w.Write((byte)value); }
        else { if (value > 32767) w.Write((ushort)((value >> 16) | 0x8000)); w.Write((ushort)value); }
    }
    uint Index(string value)
    {
        int i = strings.IndexOf(value);
        if (i < 0) { i = strings.Count; strings.Add(value); }
        return (uint)i;
    }
    string NodeName(byte[] node) => strings[(int)U32(node, 20)];
    string Name(Attr? attr) => attr == null ? "" : strings[(int)attr.Name];
    string? Text(Attr? attr) => attr?.Type == 3 ? strings[(int)attr.Data] : null;
    record Attr(uint Ns, uint Name, uint Raw, byte Type, uint Data);
    List<Attr> Attributes(byte[] node)
    {
        int offset = 16 + U16(node, 24), count = U16(node, 28), size = U16(node, 26);
        if (size != 20 || offset + count * size > node.Length) throw new InvalidDataException("Unsupported attributes.");
        var attrs = new List<Attr>();
        for (int i = 0; i < count; ++i)
        {
            int p = offset + i * size;
            attrs.Add(new(U32(node, p), U32(node, p + 4), U32(node, p + 8), node[p + 15], U32(node, p + 16)));
        }
        return attrs;
    }
    uint Resource(string name, uint id)
    {
        uint index = Index(name);
        while (resources.Count <= index) resources.Add(0);
        if (resources[(int)index] is not 0 && resources[(int)index] != id) throw new InvalidDataException("Conflicting resource.");
        resources[(int)index] = id; return index;
    }
    Attr StringAttr(string name, string value, uint id = 0) => new(id == 0 ? None : Index(Android),
        id == 0 ? Index(name) : Resource(name, id), Index(value), 3, Index(value));
    static byte[] Chunk(ushort type, byte[] body, ushort header = 8)
    {
        var data = new byte[body.Length + 8]; Put16(data, 0, type); Put16(data, 2, header);
        Put32(data, 4, (uint)data.Length); body.CopyTo(data, 8); return data;
    }
    byte[] RewriteNode(byte[] original, List<Attr> attrs)
    {
        if (U16(original, 24) != 20) throw new InvalidDataException("Unsupported XML attribute offset.");
        var oldAttrs = Attributes(original);
        attrs = attrs.OrderBy(a => a.Name < resources.Count ? resources[(int)a.Name] : 0).ToList();
        var result = new byte[36 + attrs.Count * 20]; original.AsSpan(0, 36).CopyTo(result);
        Put32(result, 4, (uint)result.Length); Put16(result, 28, checked((ushort)attrs.Count));
        foreach (int special in new[] { 30, 32, 34 })
        {
            int oldIndex = U16(original, special);
            if (oldIndex != 0)
            {
                var target = oldAttrs[oldIndex - 1];
                Put16(result, special, (ushort)(attrs.FindIndex(a => a.Ns == target.Ns && a.Name == target.Name) + 1));
            }
        }
        for (int i = 0; i < attrs.Count; i++)
        {
            int p = 36 + i * 20; var a = attrs[i];
            Put32(result, p, a.Ns); Put32(result, p + 4, a.Name); Put32(result, p + 8, a.Raw);
            Put16(result, p + 12, 8); result[p + 15] = a.Type; Put32(result, p + 16, a.Data);
        }
        return result;
    }
    byte[] Permission(string permission)
    {
        uint tag = Index("uses-permission");
        var node = new byte[56]; Put16(node, 0, 0x102); Put16(node, 2, 16); Put32(node, 4, 56);
        Put32(node, 8, None); Put32(node, 12, None); Put32(node, 16, None); Put32(node, 20, tag);
        Put16(node, 24, 20); Put16(node, 26, 20); Put16(node, 28, 1);
        var rewritten = RewriteNode(node, [StringAttr("name", permission, 0x01010003)]);
        byte[] end = new byte[16]; Put32(end, 0, None); Put32(end, 4, None); Put32(end, 8, None); Put32(end, 12, tag);
        return [.. rewritten, .. Chunk(0x103, end, 16)];
    }
    byte[] StringPool()
    {
        using var body = new MemoryStream(); using var writer = new BinaryWriter(body);
        var offsets = new List<uint>();
        foreach (string text in strings)
        {
            offsets.Add((uint)body.Length); var encoded = (utf8 ? Encoding.UTF8 : Encoding.Unicode).GetBytes(text);
            WriteLength(writer, text.Length, utf8);
            if (utf8) WriteLength(writer, encoded.Length, true);
            writer.Write(encoded); writer.Write(utf8 ? new byte[1] : new byte[2]);
        }
        while (body.Length % 4 != 0) writer.Write((byte)0);
        int start = 28 + offsets.Count * 4 + styleOffsets.Length;
        using var output = new MemoryStream(); using var w = new BinaryWriter(output);
        w.Write(strings.Count); w.Write(styleOffsets.Length / 4); w.Write(poolFlags & ~1u); w.Write(start);
        w.Write(styleData.Length == 0 ? 0 : start + (int)body.Length);
        foreach (uint offset in offsets) w.Write(offset);
        w.Write(styleOffsets); w.Write(body.ToArray()); w.Write(styleData);
        return Chunk(1, output.ToArray(), 28);
    }
    internal byte[] Patch(string targetPackage, bool isBase)
    {
        if (!strings.Contains(Android)) throw new InvalidDataException("Missing namespace.");
        var edited = new List<byte[]>();
        var existingPermissions = chunks.Where(c => c.Type == 0x102 && NodeName(c.Data) == "uses-permission")
            .SelectMany(c => Attributes(c.Data)).Where(a => Name(a) == "name").Select(Text).ToHashSet();
        string[] permissions = ["android.permission.READ_EXTERNAL_STORAGE", "android.permission.WRITE_EXTERNAL_STORAGE",
            "android.permission.MANAGE_EXTERNAL_STORAGE", "android.permission.INTERNET"];
        string Bound(string s) => s.Replace(Package, targetPackage, StringComparison.Ordinal);
        foreach (var (type, data) in chunks)
        {
            if (type is 1 or 0x180) continue;
            if (type != 0x102) { edited.Add(data); continue; }
            string tag = NodeName(data); var attrs = Attributes(data);
            for (int i = 0; i < attrs.Count; i++)
            {
                var a = attrs[i]; string name = Name(a), text = Text(a) ?? "";
                bool className = (name == "name" && tag is "application" or "activity" or "activity-alias" or "service" or "receiver" or "provider" or "instrumentation")
                    || name is "targetActivity" or "parentActivityName" or "backupAgent" or "manageSpaceActivity" or "appComponentFactory";
                bool bound = name is "authorities" or "taskAffinity" or "permission" or "readPermission" or "writePermission" or "targetPackage"
                    || (name == "name" && tag is "permission" or "permission-tree" or "permission-group" or "uses-permission" or "action" or "category");
                if (bound && a.Type == 1) throw new InvalidDataException($"Package-bound manifest field {tag}.{name} uses a resource reference; cannot safely clone this APK.");
                string? replacement = null;
                if (tag == "manifest" && name == "package") replacement = targetPackage;
                else if (className && a.Type == 3)
                    replacement = text.StartsWith('.') ? Package + text : !text.Contains('.') ? Package + "." + text : text;
                else if (bound && a.Type == 3) replacement = Bound(text);
                if (replacement != null) attrs[i] = a with { Raw = Index(replacement), Type = 3, Data = Index(replacement) };
            }
            if (tag == "application")
            {
                if (isBase) foreach (var permission in permissions.Where(p => !existingPermissions.Contains(p))) edited.Add(Permission(permission));
                foreach (var (name, id) in new[] { ("debuggable", 0x0101000Fu), ("extractNativeLibs", 0x010104EAu), ("requestLegacyExternalStorage", 0x01010603u) })
                {
                    var replacement = new Attr(Index(Android), Resource(name, id), None, 0x12, None);
                    int i = attrs.FindIndex(a => Name(a) == name && a.Ns == Index(Android));
                    if (i < 0) attrs.Add(replacement); else attrs[i] = replacement;
                }
                if (isBase)
                {
                    var label = StringAttr("label", "Gorilla Tag (PopeModded)", 0x01010001);
                    int i = attrs.FindIndex(a => Name(a) == "label" && a.Ns == Index(Android));
                    if (i < 0) attrs.Add(label); else attrs[i] = label;
                }
            }
            edited.Add(RewriteNode(data, attrs));
        }
        using var output = new MemoryStream(); output.Write(StringPool());
        using var map = new MemoryStream(); using (var w = new BinaryWriter(map, Encoding.UTF8, true)) foreach (uint id in resources) w.Write(id);
        output.Write(Chunk(0x180, map.ToArray())); foreach (var bytes in edited) output.Write(bytes);
        return Chunk(3, output.ToArray());
    }
    internal static byte[] RenameResourcePackage(byte[] data, string original, string target)
    {
        var result = (byte[])data.Clone();
        if (result.Length < 12 || U16(result, 0) != 2 || U32(result, 4) != result.Length) throw new InvalidDataException("Invalid resources.arsc.");
        var name = Encoding.Unicode.GetBytes(target);
        if (name.Length > 254) throw new InvalidDataException("Package name is too long.");
        for (int p = U16(result, 2); p < result.Length;)
        {
            int size = checked((int)U32(result, p + 4));
            if (size < 8 || size > result.Length - p) throw new InvalidDataException("Invalid resource table chunk.");
            if (U16(result, p) == 0x200)
            {
                if (U16(result, p + 2) < 268) throw new InvalidDataException("Invalid resource package header.");
                string existing = Encoding.Unicode.GetString(result, p + 12, 256).TrimEnd('\0');
                if (existing == original) { Array.Clear(result, p + 12, 256); name.CopyTo(result, p + 12); }
            }
            p += size;
        }
        return result;
    }
}
