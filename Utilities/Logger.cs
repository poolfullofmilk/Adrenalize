namespace Adrenalize.Utilities;

internal static class Logger
{
    // Indent Matching The Timestamp Width
    private static readonly string s_indent = new(' ', 11);

    internal static void Log(string message, ConsoleColor color = ConsoleColor.White)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    internal static void LogList(
        string header,
        IReadOnlyList<string> items,
        ConsoleColor itemColor = ConsoleColor.Gray
    )
    {
        Log(header);

        foreach (var item in items)
            LogItem(item, itemColor);
    }

    internal static void LogItem(string item, ConsoleColor color = ConsoleColor.Gray)
    {
        Console.Write(s_indent);
        Console.ForegroundColor = color;
        Console.WriteLine($"- {item}");
        Console.ResetColor();
    }
}
