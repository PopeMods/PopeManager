namespace PopeManager;

internal class ApkMaker(ICommandRunner runner)
{
    string Root => Path.Combine(
        AppContext.BaseDirectory,
        "tools",
        "android-build-tools",
        OperatingSystem.IsLinux() ? "android-14-linux" : "android-14");

    string ZipAlign => Path.Combine(
        Root,
        OperatingSystem.IsWindows() ? "zipalign.exe" : "zipalign");

    string ApkSigner => Path.Combine(
        Root,
        OperatingSystem.IsWindows() ? "apksigner.bat" : "apksigner");

    string KeyStore => Path.Combine(AppPaths.Data, "signing", "popemanager-gorillatag.jks");
    const string Alias = "popemanager";
    const string Password = "popenheimer";

    internal void Validate()
    {
        if (!File.Exists(ZipAlign) || !File.Exists(ApkSigner))
            throw new IOException(
                $"Android build tools are missing for {System.Runtime.InteropServices.RuntimeInformation.OSDescription}. " +
                $"Expected zipalign at '{ZipAlign}' and apksigner at '{ApkSigner}'.");
    }

    internal async Task PrepareSigningKey(CancellationToken token, Action<string> log)
    {
        Validate();
        string keytool = AppPaths.FindTool(
            OperatingSystem.IsWindows() ? "keytool.exe" : "keytool");
        if (!File.Exists(keytool)) keytool = AppPaths.FindTool("keytool");
        if (!File.Exists(KeyStore))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(KeyStore)!);
            await runner.RunAsync(keytool, ["-genkeypair", "-keystore", KeyStore, "-storepass", Password, "-keypass", Password,
                "-alias", Alias, "-keyalg", "RSA", "-keysize", "4096", "-validity", "10000", "-dname", "CN=PopeManager, OU=PopeMods, O=Local, C=GB"], token, log);
        }
        var check = await runner.RunAsync(keytool,
            ["-list", "-keystore", KeyStore, "-storepass", Password, "-alias", Alias], token, check: false);
        if (check.ExitCode != 0)
            throw new IOException("Cannot open the existing APK signing key. This is bad. Keystore: " + KeyStore);
        log("keystore done");
    }

    internal async Task<List<string>> SignSet(IReadOnlyList<string> apks, string output, CancellationToken token, Action<string> log)
    {
        await PrepareSigningKey(token, log); Directory.CreateDirectory(output);
        var signed = new List<string>();
        foreach (string input in apks)
        {
            string name = Path.GetFileNameWithoutExtension(input), aligned = Path.Combine(output, name + ".aligned.apk"), final = Path.Combine(output, name + ".apk");
            await runner.RunAsync(ZipAlign, ["-f", "-p", "4", input, aligned], token, log);
            await runner.RunAsync(ApkSigner, ["sign", "--ks", KeyStore, "--ks-key-alias", Alias, "--ks-pass", "pass:" + Password,
                "--key-pass", "pass:" + Password, "--out", final, aligned], token, log);
            await runner.RunAsync(ApkSigner, ["verify", "--verbose", final], token, log);
            signed.Add(final);
        }
        return signed;
    }
}
