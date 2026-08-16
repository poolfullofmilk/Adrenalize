namespace Adrenalize.Configuration;

internal static class AppConfig
{
    // Adrenalin Executable Paths
    internal static readonly string[] s_adrenalinExecutablePaths =
    [
        @"C:\Program Files\AMD\CNext\CNext\RadeonSoftware.exe",
        @"C:\Program Files\AMD\CNext\CNext\RadeonSettings.exe",
    ];

    // AMD Service And Process Keywords
    internal static readonly string[] s_amdKeywords = ["AMD", "Radeon"];

    // AMD Executable Path Markers
    internal static readonly string[] s_amdExecutablePathMarkers =
    [
        @"\AMD\",
        @"\Radeon\",
        @"\Advanced Micro Devices\",
        @"\CNext\",
    ];

    // Process Scan Frequency
    internal static readonly TimeSpan s_pollInterval = TimeSpan.FromSeconds(2);

    // Delay Before Reset After Game Start
    internal static readonly TimeSpan s_gameStartDelay = TimeSpan.FromSeconds(2);
}
