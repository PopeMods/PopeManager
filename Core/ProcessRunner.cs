using System.Diagnostics;
using System.Text;

namespace PopeManager;

internal record CommandResult(int ExitCode, string Output);

internal interface ICommandRunner
{
    Task<CommandResult> RunAsync(string executable, IEnumerable<string> args, CancellationToken token,
        Action<string>? log = null, bool check = true, string? workingDirectory = null);
}

internal class ProcessRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string executable, IEnumerable<string> args, CancellationToken token,
        Action<string>? log = null, bool check = true, string? workingDirectory = null)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? AppPaths.Data,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        info.Environment.Remove("DOTNET_STARTUP_HOOKS");
        foreach (var arg in args) info.ArgumentList.Add(arg);
        info.Environment["ANDROID_USER_HOME"] = Path.Combine(AppPaths.Data, "android");
        info.Environment["NO_COLOR"] = "1";
        Directory.CreateDirectory(info.Environment["ANDROID_USER_HOME"]!);
        using var process = new Process { StartInfo = info };
        if (!process.Start()) throw new IOException($"Could not start {Path.GetFileName(executable)}.");
        using var cancel = token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        var output = new StringBuilder();
        async Task Drain(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (output) output.AppendLine(line);
                log?.Invoke(line);
            }
        }
        var readers = Task.WhenAll(Drain(process.StandardOutput), Drain(process.StandardError));
        try { await process.WaitForExitAsync(token); }
        finally { await readers; }
        token.ThrowIfCancellationRequested();
        string text = output.ToString();
        if (check && process.ExitCode != 0)
            throw new IOException($"{Path.GetFileName(executable)} failed ({process.ExitCode}). {text.Trim()}");
        return new(process.ExitCode, text);
    }
}
