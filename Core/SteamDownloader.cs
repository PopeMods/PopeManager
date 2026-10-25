using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PopeManager;

internal sealed class SteamDownloader(string? executableOverride = null)
{
    readonly object gate = new();
    Process? process;
    CancellationTokenSource? attempt;
    (string User, string Password)? credentials;
    bool restart;
    internal void LoginWithPassword(string user, string password)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrEmpty(password)) return;
        lock (gate) { credentials = (user.Trim(), password); restart = true; attempt?.Cancel(); }
    }
    internal void SendInput(string value)
    {
        if (value.IndexOfAny(['\r', '\n']) >= 0) return;
        lock (gate)
        {
            try { if (process is { HasExited: false }) { process.StandardInput.WriteLine(value); process.StandardInput.Flush(); } }
            catch (Exception e) when (e is IOException or InvalidOperationException) { }
        }
    }
    internal static List<string> Arguments(string directory, string? user = null, string? password = null)
    {
        var args = new List<string> { "-app", "1533390", "-depot", "1533392", "-branch", "public", "-all-platforms", "-all-archs", "-dir", directory, "-validate" };
        if (user == null) args.Add("-qr");
        else args.AddRange(["-username", user, "-password", password!]);
        return args;
    }
    internal static string ResolveExecutable()
    {
        string name = OperatingSystem.IsWindows() ? "DepotDownloader.exe" : "DepotDownloader";
        string folder = OperatingSystem.IsWindows() ? "depotdownloader" : "depotdownloader-linux";
        string path = Path.Combine(AppContext.BaseDirectory, folder, name);
        if (!File.Exists(path)) path = AppPaths.FindTool("DepotDownloader");
        AdbManager.EnsureExecutable(path);
        return path;
    }
    internal async Task<List<string>> Download(string directory, Action<string> log,
        Action<string> qr, Action<float> progress, Action<string> prompt, CancellationToken token, bool consoleOutput = false, Action? authenticated = null)
    {
        Directory.CreateDirectory(directory);
        while (true)
        {
            token.ThrowIfCancellationRequested();
            using var current = CancellationTokenSource.CreateLinkedTokenSource(token);
            (string User, string Password)? login;
            lock (gate) { attempt = current; login = credentials; credentials = null; restart = false; }
            try
            {
                qr(""); prompt("");
                var start = new ProcessStartInfo(executableOverride ?? ResolveExecutable()) {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
                    RedirectStandardError = true, RedirectStandardInput = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = directory
                };
                start.Environment.Remove("DOTNET_STARTUP_HOOKS");
                foreach (string arg in Arguments(directory, login?.User, login?.Password)) start.ArgumentList.Add(arg);
                using var child = new Process { StartInfo = start };
                current.Token.ThrowIfCancellationRequested();
                if (!child.Start()) throw new IOException("Could not start DepotDownloader.");
                lock (gate) process = child;
                using var kill = current.Token.Register(() => {
                    try { if (!child.HasExited) child.Kill(true); }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                });
                var parser = new SteamOutputParser(log, qr, progress, prompt, login?.Password, authenticated);
                Task readers = Task.WhenAll(Read(child.StandardOutput.BaseStream, line => { if (consoleOutput) Console.WriteLine(line); parser.Accept(line); }), Read(child.StandardError.BaseStream, line => parser.Accept(line)));
                try { await child.WaitForExitAsync(current.Token); }
                finally { await readers; }
                current.Token.ThrowIfCancellationRequested();
                parser.Finish();
                if (child.ExitCode != 0) throw new IOException("Steam download failed.");
                var apks = FindApks(directory);
                progress(100);
                return apks;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                lock (gate) { if (!restart) throw; }
            }
            finally { lock (gate) { process = null; attempt = null; } }
        }
    }
    internal static string DecodeOutput(byte[] bytes)
    {
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            int codePage = OperatingSystem.IsWindows()
                ? System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage : 437;
            return Encoding.GetEncoding(codePage).GetString(bytes);
        }
    }
    static async Task Read(Stream stream, Action<string> accept)
    {
        var buffer = new byte[4096]; var line = new List<byte>();
        int count;
        while ((count = await stream.ReadAsync(buffer)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                byte c = buffer[i];
                if (c is 10 or 13) { if (line.Count > 0) accept(DecodeOutput(line.ToArray())); line.Clear(); }
                else line.Add(c);
            }
            string pending = DecodeOutput(line.ToArray());
            if (SteamOutputParser.IsPrompt(pending)) { accept(pending); line.Clear(); }
        }
        if (line.Count > 0) accept(DecodeOutput(line.ToArray()));
    }
    internal static List<string> FindApks(string directory)
    {
        var apks = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal).ToList();
        if (apks.Count == 0) throw new InvalidDataException("The Gorilla Tag Android depot did not contain any APKs.");
        _ = ApkInspector.Inspect(apks);
        return apks;
    }
}

internal sealed class SteamOutputParser(Action<string> log, Action<string> qr, Action<float> progress,
    Action<string> prompt, string? password = null, Action? authenticated = null)
{
    readonly object gate = new();
    readonly List<string> rows = [];
    bool readingQr, loggedIn;
    static readonly Regex Ansi = new("\\x1b\\[[0-?]*[ -/]*[@-~]", RegexOptions.Compiled);
    static readonly Regex Percent = new(@"(?<![\d.])(\d{1,3}(?:\.\d+)?)%", RegexOptions.Compiled);
    internal static bool IsPrompt(string text) => text.Contains(':') &&
        (text.Contains("enter", StringComparison.OrdinalIgnoreCase) || text.Contains("password", StringComparison.OrdinalIgnoreCase));
    internal void Accept(string raw)
    {
        lock (gate)
        {
            string line = Ansi.Replace(raw, "");
            if (line.Contains("QR code", StringComparison.OrdinalIgnoreCase))
            { FlushQr(); readingQr = true; return; }
            if (readingQr && line.Length > 0 && line.All(c => c is ' ' or '\u2588' or '\u2580' or '\u2584'))
            {
                rows.Add(line);
                if (rows.Count == line.Length / 2 && rows.All(r => r.Length == line.Length)) FlushQr();
                return;
            }
            if (readingQr) FlushQr();
            if (IsPrompt(line)) prompt(line.Trim());
            if (line.Contains("licenses for account", StringComparison.OrdinalIgnoreCase) || line.Contains("Downloading depot", StringComparison.OrdinalIgnoreCase))
            { qr(""); prompt(""); if (!loggedIn) { loggedIn = true; authenticated?.Invoke(); } }
            var match = Percent.Match(line);
            if (match.Success && float.TryParse(match.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float value)) progress(Math.Clamp(value, 0, 100));
            if (!string.IsNullOrWhiteSpace(line)) log(string.IsNullOrEmpty(password) ? line : line.Replace(password, "[redacted]", StringComparison.Ordinal));
        }
    }
    internal void Finish() { lock (gate) FlushQr(); }
    void FlushQr()
    {
        readingQr = false;
        if (rows.Count == 0) return;
        bool half = rows.Any(r => r.Contains('\u2580') || r.Contains('\u2584'));
        int width = rows.Max(r => r.Length);
        int expectedRows = half ? (width + 1) / 2 : width / 2;
        if (rows.Count != expectedRows || !rows.Any(r => r.Any(c => c is '\u2588' or '\u2580' or '\u2584')))
        { rows.Clear(); return; }
        var grid = new List<byte[]>();
        foreach (string row in rows)
        {
            int stride = half ? 1 : 2;
            var top = new byte[(width + stride - 1) / stride];
            var bottom = new byte[top.Length];
            for (int x = 0; x < width; x += stride)
            {
                char c = x < row.Length ? row[x] : ' ';
                top[x / stride] = c is '\u2588' or '\u2580' ? (byte)1 : (byte)0;
                bottom[x / stride] = c is '\u2588' or '\u2584' ? (byte)1 : (byte)0;
            }
            grid.Add(top); if (half) grid.Add(bottom);
        }
        rows.Clear();
        qr("data:image/png;base64," + Convert.ToBase64String(SteamQr.BuildQrPng(grid.ToArray())));
    }
}
