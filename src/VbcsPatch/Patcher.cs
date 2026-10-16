using System.Diagnostics;
using System.Reflection;
using Microsoft.CodeAnalysis.CodeGen;

namespace VbcsPatch;

/// <summary>
/// Runtime patches for https://github.com/dotnet/roslyn/issues/86006.
///
/// Environment switches (read by the compiler server, which inherits them from whoever started it):
///   VBCSPATCH_DISABLE=1    do nothing (checked by the startup hook)
///   VBCSPATCH_TOKENMAP=0   skip the TokenMap.AddItem patch
///   VBCSPATCH_ANALYZER=0   skip the AnalyzerExecutor.OnOperationBlockActionsExecuted patch
///   VBCSPATCH_VERIFY=1     track every TokenMap so <see cref="Verify"/> can check its invariants (tests only, leaks)
///   VBCSPATCH_LOG=path     log file (default %LOCALAPPDATA%\VbcsPatch\vbcspatch.log)
/// </summary>
public static class Patcher
{
    private static int s_applied;
    private static string s_summary = "not applied";

    // Hooks are undone when disposed/finalized; keep them alive for the life of the process.
    private static readonly List<IDisposable> s_hooks = [];

    public static string Apply()
    {
        if (Interlocked.Exchange(ref s_applied, 1) != 0)
            return s_summary;

        var stopwatch = Stopwatch.StartNew();
        var results = new List<string>();

        Run(results, "TokenMap.AddItem" + (TokenMapPatch.VerifyMode ? " (verify mode)" : ""), "VBCSPATCH_TOKENMAP", TokenMapPatch.Apply);
        Run(results, "AnalyzerExecutor.OnOperationBlockActionsExecuted", "VBCSPATCH_ANALYZER", AnalyzerExecutorPatch.Apply);

        var roslyn = typeof(TokenMap).Assembly;
        var roslynVersion = roslyn.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? roslyn.GetName().Version?.ToString();
        s_summary = $"VbcsPatch: .NET {Environment.Version}, Roslyn {roslynVersion}; {string.Join("; ", results)} ({stopwatch.ElapsedMilliseconds} ms)";
        Log.Write(s_summary);
        return s_summary;
    }

    /// <summary>Checks the invariants of every TokenMap seen so far. Requires VBCSPATCH_VERIFY=1.</summary>
    public static string Verify() => TokenMapPatch.VerifyMaps();

    private static void Run(List<string> results, string name, string switchName, Func<IDisposable> apply)
    {
        if (Environment.GetEnvironmentVariable(switchName) == "0")
        {
            results.Add($"{name}: skipped");
            return;
        }

        try
        {
            s_hooks.Add(apply());
            results.Add($"{name}: patched");
        }
        catch (Exception ex)
        {
            results.Add($"{name}: FAILED ({ex.GetType().Name}: {ex.Message})");
            Log.Write($"{name} patch failed: {ex}");
        }
    }

    internal static void RequireField(Type type, string name, Type fieldType)
    {
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new MissingFieldException(type.FullName, name);
        if (field.FieldType != fieldType)
            throw new InvalidOperationException($"{type.Name}.{name} is {field.FieldType}, expected {fieldType}");
    }
}
