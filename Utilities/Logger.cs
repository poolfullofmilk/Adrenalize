using System.Text;

namespace Adrenalize.Utilities;

internal static class Logger
{
    // Indent Matching The Timestamp Width
    private static readonly string s_indent = new(' ', 11);

    internal static readonly string s_logFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Adrenalize",
        "log.txt"
    );

    internal static void StartLogFile()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(s_logFilePath)!);

            // Everything Printed Is Mirrored, Colors Stay Console Only
            var fileWriter = new StreamWriter(s_logFilePath, append: false) { AutoFlush = true };
            Console.SetOut(TextWriter.Synchronized(new TeeWriter(Console.Out, fileWriter)));
        }
        catch { }
    }

    internal static void Log(string message, ConsoleColor color = ConsoleColor.White)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"[{DateTime.Now:HH:mm:ss}] ");
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ResetColor();
    }

    internal static void LogItem(string item, ConsoleColor color = ConsoleColor.Gray)
    {
        Console.Write(s_indent);
        Console.ForegroundColor = color;
        Console.WriteLine($"- {item}");
        Console.ResetColor();
    }

    internal static void SelfTest()
    {
        var consoleWriter = new StringWriter();
        var fileWriter = new StringWriter();

        // Base Overloads Must Route Through Write(char)
        using (var teeWriter = new TeeWriter(consoleWriter, fileWriter))
            teeWriter.WriteLine("Tee");

        if (consoleWriter.ToString() != fileWriter.ToString() || fileWriter.ToString().Length == 0)
            throw new InvalidOperationException("SelfTest Failed: Logger.TeeWriter");
    }

    private sealed class TeeWriter(TextWriter consoleWriter, TextWriter fileWriter) : TextWriter
    {
        public override Encoding Encoding => consoleWriter.Encoding;

        public override void Write(char value)
        {
            consoleWriter.Write(value);
            fileWriter.Write(value);
        }
    }
}
