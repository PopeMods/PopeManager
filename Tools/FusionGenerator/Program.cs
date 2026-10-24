using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Cpp2IL.Core;
using Cpp2IL.Core.Api;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.ProcessingLayers;
using Il2CppInterop.Generator;
using Il2CppInterop.Common;
using Il2CppInterop.Generator.Runners;
using LibCpp2IL;
using Microsoft.Extensions.Logging;

try
{
    InstructionSetRegistry.RegisterInstructionSet<NewArmV8InstructionSet>(DefaultInstructionSets.ARM_V8);
    LibCpp2IlBinaryRegistry.RegisterBuiltInBinarySupport();
    if (args.SequenceEqual(new[] { "--self-test" }))
    {
        Console.WriteLine("Cpp2IL " + typeof(Cpp2IlApi).Assembly.GetName().Version);
        Console.WriteLine("Interop " + typeof(GeneratorOptions).Assembly.GetName().Version);
        Console.WriteLine("ARM64 ELF support registered; generator assemblies loaded.");
        return 0;
    }
    if (args.Length != 5) throw new ArgumentException("Usage: FusionGenerator <libil2cpp.so> <global-metadata.dat> <Unity-version> <Unity-libs> <output-root>");
    string binary = Path.GetFullPath(args[0]), metadata = Path.GetFullPath(args[1]);
    string unity = Path.GetFullPath(args[3]), root = Path.GetFullPath(args[4]);
    if (!File.Exists(binary) || !File.Exists(metadata) || !Directory.EnumerateFiles(unity, "UnityEngine*.dll").Any())
        throw new IOException("Binary, metadata or Unity reference libraries are missing.");
    string output = Path.Combine(root, "interop");
    Directory.CreateDirectory(output);
    if (Directory.EnumerateFiles(output).Any()) throw new IOException("Output must be empty; use a fresh generation directory.");
    Cpp2IL.Core.Logging.Logger.InfoLog += (message, source) => Console.WriteLine($"[{source}] {message.Trim()}");
    Cpp2IL.Core.Logging.Logger.WarningLog += (message, source) => Console.WriteLine($"[WARN:{source}] {message.Trim()}");
    Cpp2IL.Core.Logging.Logger.ErrorLog += (message, source) => Console.Error.WriteLine($"[{source}] {message.Trim()}");
    Cpp2IlApi.RuntimeOptions = new Cpp2IlRuntimeArgs { LowMemoryMode = true };
    Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, AssetRipper.Primitives.UnityVersion.Parse(args[2]));
    List<Cpp2IlProcessingLayer> processors = [new AttributeInjectorProcessingLayer()];
    foreach (var processor in processors) processor.PreProcess(Cpp2IlApi.CurrentAppContext, processors);
    foreach (var processor in processors) processor.Process(Cpp2IlApi.CurrentAppContext);
    var assemblies = new AsmResolverDllOutputFormatDefault().BuildAssemblies(Cpp2IlApi.CurrentAppContext);
    LibCpp2IlMain.Reset();
    Cpp2IlApi.CurrentAppContext = null;

    using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(new TerminalLoggerProvider()));
    Il2CppInteropGenerator.Create(new GeneratorOptions
    {
        Source = assemblies, OutputDir = output, UnityBaseLibsDir = unity,
        GameAssemblyPath = null, NoXrefCache = true, Parallel = false
    }).AddLogger(loggerFactory.CreateLogger("Interop")).AddInteropAssemblyGenerator().Run();
    if (!File.Exists(Path.Combine(output, "Assembly-CSharp.dll"))) throw new IOException("The generator did not produce Assembly-CSharp.dll.");

    // Match Fusion's versioned cache format. Config also disables on-device regeneration:
    // Android directory enumeration order can differ from Windows, causing a harmless hash mismatch.
    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    void HashFile(string path)
    {
        using var input = File.OpenRead(path);
        var buffer = new byte[81920]; int read;
        while ((read = input.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
    }
    HashFile(binary);
    foreach (string file in Directory.EnumerateFiles(unity, "*.dll"))
    { hash.AppendData(Encoding.UTF8.GetBytes(Path.GetFileName(file))); HashFile(file); }
    foreach (Assembly assembly in new[] { typeof(GeneratorOptions).Assembly, typeof(Cpp2IlApi).Assembly })
        hash.AppendData(Encoding.UTF8.GetBytes(assembly.GetName().Version!.ToString()));
    File.WriteAllText(Path.Combine(output, "assembly-hash.txt"), Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    Console.WriteLine("Generation complete: " + output);
    return 0;
}
catch (Exception error) { Console.Error.WriteLine(error); return 1; }

sealed class TerminalLoggerProvider : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new TerminalLogger();
    public void Dispose() { }
    sealed class TerminalLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(level)) Console.WriteLine(formatter(state, exception)); }
    }
}
