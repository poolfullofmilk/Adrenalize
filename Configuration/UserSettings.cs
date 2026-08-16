namespace Adrenalize.Configuration;

internal sealed class UserSettings
{
    // Settings File Location
    private static readonly string s_settingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Adrenalize"
    );
    private static readonly string s_settingsFilePath = Path.Combine(
        s_settingsDirectory,
        "settings.ini"
    );

    internal bool StartupEnabled { get; set; }
    internal bool MinimizeToTray { get; set; } = true;
    internal bool StartMinimized { get; set; }
    internal bool NotificationsEnabled { get; set; } = true;

    internal static UserSettings Load()
    {
        try
        {
            return File.Exists(s_settingsFilePath)
                ? Parse(File.ReadAllLines(s_settingsFilePath))
                : new UserSettings();
        }
        catch
        {
            // A Locked Or Corrupt File Must Not Block Startup
            return new UserSettings();
        }
    }

    internal static UserSettings Parse(IEnumerable<string> lines)
    {
        var settings = new UserSettings();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            var separatorIndex = trimmedLine.IndexOf('=');
            if (trimmedLine.StartsWith('#') || separatorIndex < 0)
                continue;

            var key = trimmedLine[..separatorIndex].Trim();
            var isTrue = trimmedLine[(separatorIndex + 1)..]
                .Trim()
                .Equals("true", StringComparison.OrdinalIgnoreCase);

            if (key.Equals("StartupEnabled", StringComparison.OrdinalIgnoreCase))
                settings.StartupEnabled = isTrue;
            else if (key.Equals("MinimizeToTray", StringComparison.OrdinalIgnoreCase))
                settings.MinimizeToTray = isTrue;
            else if (key.Equals("StartMinimized", StringComparison.OrdinalIgnoreCase))
                settings.StartMinimized = isTrue;
            else if (key.Equals("NotificationsEnabled", StringComparison.OrdinalIgnoreCase))
                settings.NotificationsEnabled = isTrue;
        }

        return settings;
    }

    internal void Save()
    {
        Directory.CreateDirectory(s_settingsDirectory);

        File.WriteAllLines(
            s_settingsFilePath,
            [
                $"StartupEnabled={(StartupEnabled ? "true" : "false")}",
                $"MinimizeToTray={(MinimizeToTray ? "true" : "false")}",
                $"StartMinimized={(StartMinimized ? "true" : "false")}",
                $"NotificationsEnabled={(NotificationsEnabled ? "true" : "false")}",
            ]
        );
    }

    internal static void SelfTest()
    {
        var parsed = Parse(
            ["# comment", "StartupEnabled=true", "MinimizeToTray = FALSE", "junk line", ""]
        );

        if (!parsed.StartupEnabled || parsed.MinimizeToTray || parsed.StartMinimized)
            throw new InvalidOperationException("SelfTest Failed: UserSettings.Parse");

        // Missing Keys Must Keep Their Defaults
        if (!parsed.NotificationsEnabled)
            throw new InvalidOperationException("SelfTest Failed: UserSettings.Parse Defaults");
    }
}
