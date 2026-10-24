using System.Text.RegularExpressions;

namespace PopeManager;

internal static class IniConfig
{
    internal static string SetValues(string text, string section, IReadOnlyDictionary<string, string> values)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        string? Section(string line) => Regex.Match(line, @"^\s*\[([^\]]+)\]\s*(?:#.*)?$") is { Success: true } match ? match.Groups[1].Value.Trim() : null;
        var matches = lines.Select((line, index) => (line, index)).Where(x => string.Equals(Section(x.line), section, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count > 1) throw new InvalidDataException($"Duplicate [{section}] sections in BepInEx.cfg.");
        if (matches.Count == 0) { lines.Add($"[{section}]"); matches.Add((lines[^1], lines.Count - 1)); }
        int begin = matches[0].index + 1, end = begin;
        while (end < lines.Count && Section(lines[end]) == null) end++;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = begin; i < end; i++)
        {
            var key = Regex.Match(lines[i], @"^\s*([^#;=\s][^=]*?)\s*=").Groups[1].Value.Trim();
            var update = values.FirstOrDefault(pair => pair.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (update.Key == null) continue;
            if (!seen.Add(update.Key)) throw new InvalidDataException("Duplicate BepInEx setting: " + update.Key);
            lines[i] = update.Key + " = " + update.Value;
        }
        lines.InsertRange(end, values.Where(pair => !seen.Contains(pair.Key)).Select(pair => pair.Key + " = " + pair.Value));
        return string.Join('\n', lines).TrimEnd('\n') + "\n";
    }
}
