using System.Text.Json;

namespace PopeManager;

internal static class AppPaths
{
    internal const string OriginalPackage = "com.AnotherAxiom.GorillaTag";
    internal const string TargetPackage = OriginalPackage;
    internal const string RemoteGameRoot = "/sdcard/Android/data/" + OriginalPackage + "/files/PopeManager/FusionCore";
    internal static readonly string Data = MakeDataDirectory();
    private static string MakeDataDirectory()
    {
        string path = Environment.GetEnvironmentVariable("POPEMANAGER_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PopeMods", "PopeManager");
        Directory.CreateDirectory(path);
        return path;
    }
    internal static string FindTool(string name)
    {
        string executable = OperatingSystem.IsWindows() && !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name + ".exe" : name;
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        return paths.Select(p => Path.Combine(p.Trim('"'), executable)).FirstOrDefault(File.Exists)
            ?? throw new FileNotFoundException($"{name} was not found on PATH.");
    }
    internal static string FindJavaTool(string name)
    {
        string executable = OperatingSystem.IsWindows() ? name + ".exe" : name;
        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            string path = Path.Combine(javaHome.Trim('"'), "bin", executable);
            if (File.Exists(path)) return path;
        }
        try { return FindTool(name); }
        catch (FileNotFoundException)
        {
            throw new FileNotFoundException($"Java tool '{name}' was not found. Install a JDK and set JAVA_HOME to its folder or add its bin folder to PATH.");
        }
    }

}

internal class ManagerSettings
{
    public string LoaderPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "loader", "FusionCore-Quest.apk");
    public string GeneratorPath { get; set; } = Path.Combine(AppContext.BaseDirectory, "tools", "fusion-generator");
    public string UnityLibrariesPath { get; set; } = "";
    internal static string FilePath => Path.Combine(AppPaths.Data, "settings.json");
    internal static ManagerSettings Load()
    {
        try { return JsonSerializer.Deserialize<ManagerSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch (Exception e) when (e is IOException or JsonException) { return new(); }
    }
    internal void Save()
    {
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, true);
    }
}
