using System;
using System.IO;
using System.Reflection;

/// <summary>
/// .NET startup hook (https://github.com/dotnet/runtime/blob/main/docs/design/features/host-startup-hook.md).
/// Runs before VBCSCompiler's Main, i.e. before any Roslyn code has executed, and applies the runtime patches.
/// Never throws: a failure here must not take down the compiler server.
/// </summary>
internal static class StartupHook
{
    public static void Initialize()
    {
        try
        {
            if (Environment.GetEnvironmentVariable("VBCSPATCH_DISABLE") == "1")
                return;

            if (!IsCompilerServer() && Environment.GetEnvironmentVariable("VBCSPATCH_FORCE") != "1")
                return;

            var dir = Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!;

            // The patch's dependencies (MonoMod, Mono.Cecil, ...) live next to it, not on the app's probing paths.
            AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
            {
                var candidate = Path.Combine(dir, new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
            };

            var patchAssembly = Assembly.LoadFrom(Path.Combine(dir, "VbcsPatch.dll"));
            var patcher = patchAssembly.GetType("VbcsPatch.Patcher", throwOnError: true)!;
            patcher.GetMethod("Apply", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, null);
        }
        catch (Exception ex)
        {
            TryLog("startup hook failed: " + ex);
        }
    }

    private static bool IsCompilerServer()
    {
        var entryName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (string.Equals(entryName, "VBCSCompiler", StringComparison.OrdinalIgnoreCase))
            return true;

        var args = Environment.GetCommandLineArgs();
        foreach (var arg in args)
        {
            var file = Path.GetFileName(arg.Trim('"'));
            if (string.Equals(file, "VBCSCompiler.dll", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(file, "VBCSCompiler.exe", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void TryLog(string message)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("VBCSPATCH_LOG");
            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Path.GetDirectoryName(typeof(StartupHook).Assembly.Location)!, "vbcspatch.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [pid {System.Diagnostics.Process.GetCurrentProcess().Id}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
