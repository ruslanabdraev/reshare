namespace Reshare.Core;

public static class Log
{
    private static readonly object Gate = new();
    public static bool Verbose { get; set; }

    public static void Debug(string msg) { if (Verbose) Write(ConsoleColor.DarkGray, "dbg", msg); }
    public static void Info(string msg) => Write(ConsoleColor.Gray, "inf", msg);
    public static void Success(string msg) => Write(ConsoleColor.Green, "ok ", msg);
    public static void Warn(string msg) => Write(ConsoleColor.Yellow, "wrn", msg);
    public static void Error(string msg) => Write(ConsoleColor.Red, "err", msg);

    private static void Write(ConsoleColor color, string level, string msg)
    {
        lock (Gate)
        {
            var old = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {level} {msg}");
            Console.ForegroundColor = old;
        }
    }
}
