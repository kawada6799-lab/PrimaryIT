namespace Precheck;

/// <summary>画面（と run.log）向けの簡単なログ。</summary>
public static class Log
{
    public static bool Verbose { get; set; }

    private static void Write(string level, string msg) =>
        Console.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}");

    public static void Debug(string msg) { if (Verbose) Write("DEBUG", msg); }
    public static void Info(string msg) => Write("INFO", msg);
    public static void Warn(string msg) => Write("WARNING", msg);
    public static void Error(string msg) => Write("ERROR", msg);
}
