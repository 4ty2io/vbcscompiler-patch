using System.Diagnostics;

namespace VbcsPatch;

internal static class Log
{
    private static readonly Lock s_gate = new();

    public static string FilePath { get; } = GetPath();

    public static void Write(string message)
    {
        try
        {
            lock (s_gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [pid {Environment.ProcessId}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never break the compiler server.
        }
    }

    private static string GetPath()
    {
        var path = Environment.GetEnvironmentVariable("VBCSPATCH_LOG");
        if (!string.IsNullOrEmpty(path))
            return path;

        // Next to the installed binaries (install.ps1 puts them in a user-writable folder).
        return Path.Combine(Path.GetDirectoryName(typeof(Log).Assembly.Location)!, "vbcspatch.log");
    }
}
