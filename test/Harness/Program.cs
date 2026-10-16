using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

var opts = Options.Parse(args);

if (opts.Command == "gen")
{
    File.WriteAllText(opts.Out ?? "gen.cs", Generator.Generate(opts.Classes, opts.Methods, seed: 42));
    Console.WriteLine($"wrote {opts.Out} ({opts.Classes} classes x {opts.Methods} methods)");
    return 0;
}

if (opts.Patch)
    Console.WriteLine(VbcsPatch.Patcher.Apply());
else
    Console.WriteLine("VbcsPatch: not applied (baseline)");

Console.WriteLine($".NET {Environment.Version}, {Environment.ProcessorCount} cores, server GC={System.Runtime.GCSettings.IsServerGC}, Roslyn {typeof(CSharpCompilation).Assembly.GetName().Version}");

var source = Generator.Generate(opts.Classes, opts.Methods, seed: 42);
var trees = SplitIntoTrees(source, opts.Files);
var references = FrameworkReferences();
using var contention = new ContentionListener();

var iterationTimes = new List<double>();
for (var iteration = 1; iteration <= opts.Iterations; iteration++)
{
    contention.Reset();
    var monitorBefore = Monitor.LockContentionCount;
    var jitBefore = System.Runtime.JitInfo.GetCompiledMethodCount();
    var jitTimeBefore = System.Runtime.JitInfo.GetCompilationTime();
    var gcPauseBefore = GC.GetTotalPauseDuration();
    var stopwatch = Stopwatch.StartNew();

    var results = await Task.WhenAll(Enumerable.Range(0, opts.Parallel).Select(n => Task.Run(() => CompileOne(n))));

    stopwatch.Stop();
    var combined = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", results.Select(r => r.Hash))))).Substring(0, 16);
    var diagnostics = results.Sum(r => r.AnalyzerDiagnostics);
    var verify = opts.Patch ? " verify: " + VbcsPatch.Patcher.Verify() : "";
    Console.WriteLine(
        $"iter {iteration}: {stopwatch.Elapsed.TotalSeconds,6:F2}s  " +
        $"contentions={contention.Count,7} wait={contention.TotalWait.TotalSeconds,7:F2}s  monitor={Monitor.LockContentionCount - monitorBefore,7}  " +
        $"threads={ThreadPool.ThreadCount,3}  jit={System.Runtime.JitInfo.GetCompiledMethodCount() - jitBefore,6} ({(System.Runtime.JitInfo.GetCompilationTime() - jitTimeBefore).TotalSeconds:F1}s)  " +
        $"gcPause={(GC.GetTotalPauseDuration() - gcPauseBefore).TotalSeconds:F1}s  output={combined} analyzerDiags={diagnostics}{verify}");
    iterationTimes.Add(stopwatch.Elapsed.TotalSeconds);
}

if (iterationTimes.Count > 1)
{
    var warm = iterationTimes.Skip(1).ToList();
    Console.WriteLine($"warm mean {warm.Average():F2}s  min {warm.Min():F2}s");
}

return 0;

(string Hash, int AnalyzerDiagnostics) CompileOne(int n)
{
    var compilation = CSharpCompilation.Create(
        $"Gen{n}",
        trees,
        references,
        new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: OptimizationLevel.Release,
            deterministic: true,
            concurrentBuild: true,
            nullableContextOptions: NullableContextOptions.Disable));

    var analyzerDiagnostics = 0;
    if (opts.Analyzers)
    {
        var withAnalyzers = compilation.WithAnalyzers(
            [new CfgAnalyzer()],
            new CompilationWithAnalyzersOptions(new AnalyzerOptions([]), onAnalyzerException: null, concurrentAnalysis: true, logAnalyzerExecutionTime: false));
        var diags = withAnalyzers.GetAnalyzerDiagnosticsAsync().GetAwaiter().GetResult();
        analyzerDiagnostics = diags.Length;
    }

    using var peStream = new MemoryStream();
    var result = compilation.Emit(peStream);
    if (!result.Success)
    {
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Take(5);
        throw new InvalidOperationException("emit failed:\n" + string.Join("\n", errors));
    }

    return (Convert.ToHexString(SHA256.HashData(peStream.ToArray())), analyzerDiagnostics);
}

static ImmutableArray<SyntaxTree> SplitIntoTrees(string source, int files)
{
    // One namespace block per class chunk keeps the trees independent.
    var parts = source.Split("//--split--\n");
    var header = parts[0];
    var bodies = parts.Skip(1).ToArray();
    var perFile = (int)Math.Ceiling(bodies.Length / (double)files);
    var parseOptions = new CSharpParseOptions(LanguageVersion.Latest);
    return [.. bodies.Chunk(perFile).Select((chunk, i) =>
        CSharpSyntaxTree.ParseText(header + string.Concat(chunk), parseOptions, path: $"gen{i}.cs", encoding: Encoding.UTF8))];
}

static ImmutableArray<MetadataReference> FrameworkReferences()
{
    var frameworkDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
    var list = new List<MetadataReference>();
    foreach (var path in Directory.EnumerateFiles(frameworkDir, "*.dll"))
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                continue;
        }
        catch
        {
            continue;
        }

        list.Add(MetadataReference.CreateFromFile(path));
    }

    return [.. list];
}

sealed class Options
{
    public string Command = "bench";
    public bool Patch;
    public bool Analyzers;
    public int Parallel = 8;
    public int Classes = 1500;
    public int Methods = 6;
    public int Files = 16;
    public int Iterations = 4;
    public string? Out;

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "bench": case "gen": o.Command = args[i]; break;
                case "--patch": o.Patch = true; break;
                case "--analyzers": o.Analyzers = true; break;
                case "--parallel": o.Parallel = int.Parse(args[++i]); break;
                case "--classes": o.Classes = int.Parse(args[++i]); break;
                case "--methods": o.Methods = int.Parse(args[++i]); break;
                case "--files": o.Files = int.Parse(args[++i]); break;
                case "--iterations": o.Iterations = int.Parse(args[++i]); break;
                case "--out": o.Out = args[++i]; break;
                default: throw new ArgumentException("unknown argument " + args[i]);
            }
        }

        return o;
    }
}

static class Generator
{
    /// <summary>
    /// Lots of methods referencing lots of *distinct* symbols (generic instantiations over random class pairs),
    /// so most TokenMap lookups miss the fast path and go through AddItem.
    /// </summary>
    public static string Generate(int classes, int methods, int seed)
    {
        var rng = new Random(seed);
        var sb = new StringBuilder();
        sb.Append("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            //--split--
            namespace Gen
            {
                public static class H
                {
                    public static T G<T>() where T : new() => new T();
                    public static int G2<T1, T2>(T1 a, T2 b) => a is null ? 0 : 1;
                }
            }

            """);

        for (var i = 0; i < classes; i++)
        {
            sb.Append("//--split--\n");
            sb.Append($"namespace Gen\n{{\n    public sealed class C{i}\n    {{\n");
            for (var j = 0; j < 5; j++)
                sb.Append($"        public static int F{j} = {j};\n        public int P{j} {{ get; set; }}\n        public static int S{j}(int x) => x + F{j};\n");

            for (var m = 0; m < methods; m++)
            {
                int a = rng.Next(classes), b = rng.Next(classes), c = rng.Next(classes), k = rng.Next(5);
                sb.Append($$"""
                            public int M{{m}}(int x)
                            {
                                var l = new List<C{{a}}>();
                                l.Add(new C{{a}}());
                                var d = new Dictionary<C{{b}}, List<C{{c}}>>();
                                d[new C{{b}}()] = new List<C{{c}}>();
                                int s = C{{b}}.S{{k}}(x) + C{{c}}.F{{k}} + H.G2<C{{a}}, C{{c}}>(l[0], null) + H.G<C{{b}}>().P{{k}};
                                Func<int, int> f = y => y + C{{a}}.S{{k}}(y) + s;
                                foreach (var kv in d)
                                {
                                    s += kv.Value.Count;
                                }
                                s += l.Select(e => e.P{{k}}).Sum();
                                return s + f(x) + (x > 0 ? new C{{c}}().M{{(m + 1) % methods}}(x - 1) : 0);
                            }

                    """);
            }

            sb.Append("    }\n}\n");
        }

        // The raw string literals above inherit this file's line endings; the split marker expects '\n'.
        return sb.ToString().ReplaceLineEndings("\n");
    }
}

/// <summary>Operation-block analyzer that asks for the control flow graph, i.e. exercises AnalyzerExecutor's CFG cache.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
sealed class CfgAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor Rule = new("CFG001", "big method", "Method has {0} basic blocks", "Test", DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterOperationBlockAction(ctx =>
        {
            foreach (var block in ctx.OperationBlocks)
            {
                // Operation blocks of a method are its body blocks; the CFG is built for (and cached by) their root.
                if (ctx.OwningSymbol is not IMethodSymbol || block.Kind != OperationKind.Block)
                    continue;

                var cfg = ctx.GetControlFlowGraph(block);
                if (cfg.Blocks.Length > 4)
                    ctx.ReportDiagnostic(Diagnostic.Create(Rule, block.Syntax.GetLocation(), cfg.Blocks.Length));
            }
        });
    }
}

/// <summary>Sums runtime ContentionStop events (DurationNs) for this process.</summary>
sealed class ContentionListener : EventListener
{
    private long _count;
    private long _waitNs;

    public long Count => Interlocked.Read(ref _count);
    public TimeSpan TotalWait => TimeSpan.FromTicks(Interlocked.Read(ref _waitNs) / 100);

    public void Reset()
    {
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Exchange(ref _waitNs, 0);
    }

    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "Microsoft-Windows-DotNETRuntime")
            EnableEvents(source, EventLevel.Informational, (EventKeywords)0x4000 /* Contention */);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName?.StartsWith("ContentionStop", StringComparison.Ordinal) != true || e.Payload is null)
            return;

        Interlocked.Increment(ref _count);
        var index = e.PayloadNames?.IndexOf("DurationNs") ?? -1;
        if (index >= 0 && e.Payload[index] is double ns)
            Interlocked.Add(ref _waitNs, (long)ns);
    }
}
