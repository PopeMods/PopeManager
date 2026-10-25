using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PopeManager;

internal record ReleaseCheck(bool Available, string Version, string Url, string Message);

internal static class UpdateChecker
{
    internal const string RepositoryUrl = "https://github.com/PopeMods/PopeManager";
    internal const string LatestReleaseApi = "https://api.github.com/repos/PopeMods/PopeManager/releases/latest";
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(12) };
    internal static string CurrentVersion => typeof(UpdateChecker).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(UpdateChecker).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    // Release labels and build metadata do not change the numeric app version.
    internal static Version? ParseVersion(string? text)
    {
        if (text == null) return null;
        var match = Regex.Match(text.Trim(), @"^[vV]?(\d+)\.(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:[-+][0-9A-Za-z.+-]+)?$");
        if (!match.Success) return null;
        int[] parts = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (match.Groups[i + 1].Success && !int.TryParse(match.Groups[i + 1].Value, out parts[i])) return null;
        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }

    internal static async Task<ReleaseCheck> CheckAsync(string currentVersion, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("PopeManager/" + (ParseVersion(currentVersion)?.ToString(3) ?? "1.0.0"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
        using var response = await (client ?? Client).SendAsync(request);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return new(false, "", "", "");
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            return new(false, "", "", "GitHub is limiting update checks. Try again later.");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var release = document.RootElement;
        if (release.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid release response.");
        if ((release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
            || (release.TryGetProperty("prerelease", out var prerelease) && prerelease.ValueKind == JsonValueKind.True))
            return new(false, "", "", "No stable release is available yet.");
        string tag = release.TryGetProperty("tag_name", out var tagValue) && tagValue.ValueKind == JsonValueKind.String
            ? tagValue.GetString()! : "";
        var latest = ParseVersion(tag);
        var current = ParseVersion(currentVersion);
        if (latest == null || current == null)
            return new(false, "", "", "Couldn't compare release versions.");
        if (latest <= current) return new(false, "", "", "You're up to date.");
        // Construct the destination from the known repository and the returned tag.
        return new(true, tag, RepositoryUrl + "/releases/tag/" + Uri.EscapeDataString(tag), "Version " + tag + " is available.");
    }
}
