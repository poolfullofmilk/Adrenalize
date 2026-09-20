using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Adrenalize;

[SupportedOSPlatform("windows")]
internal static partial class GameScanner
{
    // Executable Name Tokens That Never Compete
    private static readonly string[] s_executableRejectTokens =
    [
        "helper",
        "service",
        "crash",
        "report",
        "uninstall",
        "setup",
    ];

    // Executable Name Tokens Earning Three Points Each
    private static readonly string[] s_executableBonusTokens = ["win64", "shipping"];

    // Subdirectories Games Commonly Hide Their Executable In
    private static readonly string[] s_executableSubdirectories =
    [
        "",
        @"Binaries",
        @"Binaries\Win64",
        @"bin\win64",
        @"game\bin\win64",
        @"live\ShooterGame\Binaries\Win64",
    ];
    private static readonly string[] s_commonGameRoots = [@"C:\Games", @"D:\Games", @"E:\Games"];

    // Extra Process Names Added By Hand
    private static readonly string s_manualGamesFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Adrenalize",
        "games.txt"
    );

    #region Scan
    internal static Dictionary<string, string> ScanInstalledGameProcessNames()
    {
        var processNameToDisplayName = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase
        );

        // Sources Reporting A Real Game Name
        var namedGames = DiscoverSteamGames()
            .Concat(DiscoverEpicGames())
            .Concat(DiscoverRiotGames())
            .Concat(DiscoverRobloxGames());

        foreach (var (displayName, rootDirectory) in namedGames)
            TryAddGame(processNameToDisplayName, rootDirectory, displayName);

        // Sources Reporting Only A Folder
        var unnamedGames = DiscoverRockstarGameRoots().Concat(DiscoverCommonGamesDirectories());

        foreach (var rootDirectory in unnamedGames)
            TryAddGame(processNameToDisplayName, rootDirectory, displayName: null);

        AddManualGames(processNameToDisplayName);
        return processNameToDisplayName;
    }

    private static void AddManualGames(Dictionary<string, string> map)
    {
        try
        {
            if (!File.Exists(s_manualGamesFilePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s_manualGamesFilePath)!);
                File.WriteAllLines(
                    s_manualGamesFilePath,
                    ["# One Process Name Per Line", "# Example: Cyberpunk2077"]
                );
                return;
            }

            foreach (var line in File.ReadAllLines(s_manualGamesFilePath))
            {
                var entry = Path.GetFileNameWithoutExtension(line.Trim());
                if (entry.Length == 0 || entry.StartsWith('#'))
                    continue;

                var processName = NormalizeProcessKey(entry);
                if (processName.Length == 0)
                    continue;

                var displayName = NormalizeDisplayName(entry);
                map[processName] = displayName.Length > 0 ? displayName : entry;
            }
        }
        catch { }
    }

    private static void TryAddGame(
        Dictionary<string, string> map,
        string rootDirectory,
        string? displayName
    )
    {
        var executablePath = ResolveMainExecutable(rootDirectory);
        if (executablePath is null)
            return;

        var executableName = Path.GetFileNameWithoutExtension(executablePath);
        var processName = NormalizeProcessKey(executableName);
        if (string.IsNullOrWhiteSpace(processName))
            return;

        var normalizedDisplayName = NormalizeDisplayName(
            displayName ?? new DirectoryInfo(rootDirectory).Name
        );
        if (normalizedDisplayName.Length < 2)
            return;

        map[processName] = normalizedDisplayName;
    }

    private static string? ResolveMainExecutable(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            return null;

        var folderName = new DirectoryInfo(rootDirectory).Name;
        var bestExecutablePath = (string?)null;
        var bestScore = int.MinValue;

        foreach (var subdirectory in s_executableSubdirectories)
        {
            var probeDirectory = Path.Combine(rootDirectory, subdirectory);

            foreach (var executablePath in EnumerateFilesSafely(probeDirectory, "*.exe"))
            {
                var executableName = Path.GetFileNameWithoutExtension(executablePath);

                // Vetoed Names Never Compete, So The Runner Up Survives
                if (IsRejectedExecutable(executableName))
                    continue;

                var score = ScoreExecutable(executableName, folderName);
                if (score > bestScore)
                {
                    bestScore = score;
                    bestExecutablePath = executablePath;
                }
            }
        }

        return bestExecutablePath;
    }

    private static bool IsRejectedExecutable(string executableName) =>
        s_executableRejectTokens.Any(token =>
            executableName.Contains(token, StringComparison.OrdinalIgnoreCase)
        );

    private static int ScoreExecutable(string executableName, string folderName)
    {
        var bonusCount = s_executableBonusTokens.Count(token =>
            executableName.Contains(token, StringComparison.OrdinalIgnoreCase)
        );

        var score = bonusCount * 3;

        // A Game Shipping Only A Launcher Still Needs Watching
        if (executableName.Contains("launcher", StringComparison.OrdinalIgnoreCase))
            score -= 5;

        // Reward A Name Matching Its Folder
        if (executableName.Equals(folderName, StringComparison.OrdinalIgnoreCase))
            score += 4;
        else if (folderName.Contains(executableName, StringComparison.OrdinalIgnoreCase))
            score += 2;

        return score;
    }
    #endregion

    #region Steam
    private static IEnumerable<(string DisplayName, string Root)> DiscoverSteamGames()
    {
        using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var primarySteamRoot = steamKey?.GetValue("SteamPath") as string;

        if (string.IsNullOrWhiteSpace(primarySteamRoot) || !Directory.Exists(primarySteamRoot))
            yield break;

        var libraryFoldersPath = Path.Combine(primarySteamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(libraryFoldersPath))
            yield break;

        foreach (var libraryRoot in ParseSteamLibraryFolders(libraryFoldersPath))
        {
            var steamAppsDirectory = Path.Combine(libraryRoot, "steamapps");

            foreach (
                var manifestPath in EnumerateFilesSafely(steamAppsDirectory, "appmanifest_*.acf")
            )
            {
                var (manifestName, installDirectory) = TryParseSteamAppManifest(manifestPath);
                if (installDirectory is null)
                    continue;

                var gameRoot = Path.Combine(steamAppsDirectory, "common", installDirectory);
                if (!Directory.Exists(gameRoot))
                    continue;

                yield return (
                    string.IsNullOrWhiteSpace(manifestName) ? installDirectory : manifestName,
                    gameRoot
                );
            }
        }
    }

    private static IEnumerable<string> ParseSteamLibraryFolders(string libraryFoldersPath)
    {
        string fileText;
        try
        {
            fileText = File.ReadAllText(libraryFoldersPath);
        }
        catch
        {
            yield break;
        }

        // The Primary Library Is Not Listed In The File
        yield return Path.GetDirectoryName(Path.GetDirectoryName(libraryFoldersPath))!;

        foreach (Match match in InstalledPathRegex().Matches(fileText))
        {
            var normalizedPath = match.Groups["p"].Value.Replace(@"\\", @"\");
            if (Directory.Exists(normalizedPath))
                yield return normalizedPath;
        }
    }

    private static (string? Name, string? InstallDirectory) TryParseSteamAppManifest(
        string manifestPath
    )
    {
        try
        {
            var manifestText = File.ReadAllText(manifestPath);
            var installDirectoryMatch = InstalledDirectoryRegex().Match(manifestText);
            var nameMatch = ManifestNameRegex().Match(manifestText);

            return (
                nameMatch.Success ? nameMatch.Groups["n"].Value : null,
                installDirectoryMatch.Success ? installDirectoryMatch.Groups["d"].Value : null
            );
        }
        catch
        {
            return (null, null);
        }
    }
    #endregion

    #region Epic
    private static IEnumerable<(string DisplayName, string Root)> DiscoverEpicGames()
    {
        const string manifestsDirectory = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";

        foreach (var itemFilePath in EnumerateFilesSafely(manifestsDirectory, "*.item"))
        {
            var manifest = TryParseEpicManifest(itemFilePath);
            if (manifest is not null && Directory.Exists(manifest.Value.Root))
                yield return manifest.Value;
        }
    }

    private static (string DisplayName, string Root)? TryParseEpicManifest(string itemFilePath)
    {
        try
        {
            using var jsonDocument = JsonDocument.Parse(File.ReadAllText(itemFilePath));
            var root = jsonDocument.RootElement;

            var hasLocation =
                root.TryGetProperty("InstallLocation", out var locationProperty)
                && locationProperty.ValueKind == JsonValueKind.String;
            if (!hasLocation)
                return null;

            var installLocation = locationProperty.GetString();
            if (string.IsNullOrWhiteSpace(installLocation))
                return null;

            var displayName =
                root.TryGetProperty("DisplayName", out var nameProperty)
                && nameProperty.ValueKind == JsonValueKind.String
                    ? nameProperty.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(displayName))
                displayName = new DirectoryInfo(installLocation).Name;

            return (displayName, installLocation);
        }
        catch
        {
            return null;
        }
    }
    #endregion

    #region Riot
    private static IEnumerable<(string DisplayName, string Root)> DiscoverRiotGames()
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (
            var (path, displayName) in new[]
            {
                (@"C:\Riot Games\VALORANT", "VALORANT"),
                (@"C:\Riot Games\League of Legends", "League of Legends"),
            }
        )
        {
            if (Directory.Exists(path) && seenPaths.Add(path))
                yield return (displayName, path);
        }

        foreach (var root in DiscoverRiotRootsFromInstallsFile(seenPaths))
            yield return (new DirectoryInfo(root).Name, root);
    }

    private static List<string> DiscoverRiotRootsFromInstallsFile(HashSet<string> seenPaths)
    {
        var roots = new List<string>();

        try
        {
            using var jsonDocument = JsonDocument.Parse(
                File.ReadAllText(@"C:\ProgramData\Riot Games\RiotClientInstalls.json")
            );

            foreach (var property in jsonDocument.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    continue;

                // Two Levels Up From The Client Is The Riot Root
                var clientDirectory = Path.GetDirectoryName(
                    property.Value.GetString()?.TrimEnd('\\', '/')
                );
                var riotRoot = Path.GetDirectoryName(clientDirectory);
                if (string.IsNullOrWhiteSpace(riotRoot))
                    continue;

                foreach (var gameDirectory in EnumerateDirectoriesSafely(riotRoot))
                {
                    if (seenPaths.Add(gameDirectory))
                        roots.Add(gameDirectory);
                }
            }
        }
        catch { }

        return roots;
    }
    #endregion

    #region Rockstar
    private static IEnumerable<string> DiscoverRockstarGameRoots()
    {
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var registryRoot in DiscoverRockstarFromRegistry(seenPaths))
            yield return registryRoot;

        foreach (
            var baseDirectory in new[]
            {
                @"C:\Program Files\Rockstar Games",
                @"C:\Program Files (x86)\Rockstar Games",
            }
        )
        {
            foreach (var childDirectory in EnumerateDirectoriesSafely(baseDirectory))
            {
                if (seenPaths.Add(childDirectory))
                    yield return childDirectory;
            }
        }
    }

    private static List<string> DiscoverRockstarFromRegistry(HashSet<string> seenPaths)
    {
        var results = new List<string>();

        try
        {
            using var baseKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Rockstar Games");
            if (baseKey is null)
                return results;

            foreach (var subKeyName in baseKey.GetSubKeyNames())
            {
                try
                {
                    using var subKey = baseKey.OpenSubKey(subKeyName);
                    var installLocation = (
                        (subKey?.GetValue("InstallFolder") as string)
                        ?? (subKey?.GetValue("InstallLocation") as string)
                        ?? string.Empty
                    ).TrimEnd('\\', '/');

                    if (
                        !string.IsNullOrWhiteSpace(installLocation)
                        && Directory.Exists(installLocation)
                        && seenPaths.Add(installLocation)
                    )
                        results.Add(installLocation);
                }
                catch { }
            }
        }
        catch { }

        return results;
    }
    #endregion

    #region Roblox
    private static IEnumerable<(string DisplayName, string Root)> DiscoverRobloxGames()
    {
        var robloxVersionsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Roblox",
            "Versions"
        );

        // Every Version Folder Holds The Same Player
        var versionDirectory = EnumerateDirectoriesSafely(robloxVersionsPath)
            .FirstOrDefault(directory =>
                File.Exists(Path.Combine(directory, "RobloxPlayerBeta.exe"))
            );

        if (versionDirectory is not null)
            yield return ("Roblox", versionDirectory);
    }
    #endregion

    #region Common Directories
    private static IEnumerable<string> DiscoverCommonGamesDirectories() =>
        s_commonGameRoots.SelectMany(EnumerateDirectoriesSafely);
    #endregion

    #region File System
    private static List<string> EnumerateFilesSafely(string directoryPath, string pattern)
    {
        try
        {
            // Enumeration Is Lazy So Building The List Throws Inside The Try
            return [.. Directory.EnumerateFiles(directoryPath, pattern, new EnumerationOptions())];
        }
        catch
        {
            return [];
        }
    }

    private static List<string> EnumerateDirectoriesSafely(string directoryPath)
    {
        try
        {
            return
            [
                .. Directory.EnumerateDirectories(directoryPath, "*", new EnumerationOptions()),
            ];
        }
        catch
        {
            return [];
        }
    }
    #endregion

    #region Names
    private static string NormalizeDisplayName(string value)
    {
        var cleaned = value
            .Replace("\u00AE", "")
            .Replace("\u2122", "")
            .Replace('_', ' ')
            .Replace('-', ' ');

        // Drop Build Tags Then Collapse The Gaps
        cleaned = WhitespaceRegex().Replace(BuildTagRegex().Replace(cleaned, ""), " ").Trim();

        return cleaned.Length < 2 ? string.Empty : ToTitleCaseInvariant(cleaned);
    }

    private static string ToTitleCaseInvariant(string value)
    {
        var words = value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        for (var index = 0; index < words.Length; index++)
            words[index] = char.ToUpperInvariant(words[index][0]) + words[index][1..];

        return string.Join(' ', words);
    }

    internal static string NormalizeProcessKey(string name)
    {
        var cleaned = name.Replace("_", "").Replace("-", "").ToLowerInvariant();

        // Games Shipping Under Many Executable Names
        if (cleaned.StartsWith("acs") || cleaned.Contains("assettocorsa"))
            return "assettocorsa";

        if (cleaned.Contains("valorant"))
            return "valorant";

        return cleaned;
    }
    #endregion

    #region Self Test
    internal static void SelfTest()
    {
        static void Check(string actual, string expected)
        {
            if (actual != expected)
                throw new InvalidOperationException(
                    $"SelfTest Failed: Got \"{actual}\", Expected \"{expected}\""
                );
        }

        Check(
            NormalizeDisplayName("PLAYERUNKNOWN'S_BATTLEGROUNDS"),
            "Playerunknown's Battlegrounds"
        );
        Check(NormalizeDisplayName("Cyberpunk 2077\u00AE"), "Cyberpunk 2077");
        Check(NormalizeDisplayName("Fortnite-Win64-Shipping"), "Fortnite");
        Check(NormalizeDisplayName("A"), string.Empty);

        Check(NormalizeProcessKey("acs"), "assettocorsa");
        Check(NormalizeProcessKey("VALORANT-Win64-Shipping"), "valorant");
        Check(NormalizeProcessKey("RobloxPlayerBeta"), "robloxplayerbeta");

        // The Game Must Outrank Its Launcher
        var gameScore = ScoreExecutable("FortniteClient-Win64-Shipping", "Fortnite");
        var launcherScore = ScoreExecutable("FortniteLauncher", "Fortnite");

        if (gameScore <= launcherScore)
            throw new InvalidOperationException("SelfTest Failed: ScoreExecutable");

        // Vetoed Names Must Be Dropped Before Scoring
        var rejected = new[]
        {
            "RustCrashHandler",
            "Uninstall",
            "EasyAntiCheat_Setup",
            "SomeService",
        };

        if (!rejected.All(IsRejectedExecutable) || IsRejectedExecutable("Rust"))
            throw new InvalidOperationException("SelfTest Failed: IsRejectedExecutable");
    }
    #endregion

    #region Regexes
    [GeneratedRegex("\"name\"\\s*\"(?<n>[^\"]+)\"", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex ManifestNameRegex();

    [GeneratedRegex("\"installdir\"\\s*\"(?<d>[^\"]+)\"", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex InstalledDirectoryRegex();

    [GeneratedRegex("\"path\"\\s*\"(?<p>[^\"]+)\"", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex InstalledPathRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(
        "\\b(Win64|Win32|x64|x86|Shipping|Release|Launcher)\\b",
        RegexOptions.IgnoreCase,
        "en-US"
    )]
    private static partial Regex BuildTagRegex();
    #endregion
}
