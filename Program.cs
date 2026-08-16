using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using Adrenalize.Amd;
using Adrenalize.Configuration;
using Adrenalize.Game;
using Adrenalize.Native;
using Adrenalize.Startup;
using Adrenalize.Tray;
using static Adrenalize.Utilities.Logger;

namespace Adrenalize;

[SupportedOSPlatform("windows")]
internal static class Program
{
    // Guards Against Overlapping Resets
    private static int s_pendingResetFlag;
    private static TrayManager? s_trayManager;

    // Process Name To Display Name, Also The Running Game Lookup
    private static Dictionary<string, string> s_games = [];

    private const string SingleInstanceMutexName = "Global\\Adrenalize_SingleInstance";
    private const string ShowConsoleEventName = "Global\\Adrenalize_ShowConsole";

    internal static UserSettings Settings { get; private set; } = new();

    #region Entry Point
    private static async Task Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            GameScanner.SelfTest();
            UserSettings.SelfTest();
            SelfTestNativeInterop();
            Console.WriteLine("SelfTest OK");
            return;
        }

        using var singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            SingleInstanceMutexName,
            out var isFirstInstance
        );

        if (!isFirstInstance)
        {
            // Ask The Running Instance To Show Itself
            if (EventWaitHandle.TryOpenExisting(ShowConsoleEventName, out var showConsoleEvent))
            {
                using (showConsoleEvent)
                    showConsoleEvent.Set();
            }

            return;
        }

        using var showConsoleWaitHandle = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ShowConsoleEventName
        );
        _ = Task.Run(() => WatchForShowConsoleSignal(showConsoleWaitHandle));

        Settings = UserSettings.Load();
        ApplyStartupRegistration();

        // Dropping Close Makes The X Hide Instantly
        if (Settings.MinimizeToTray)
            RemoveConsoleCloseButton();

        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        _ = Task.Run(RunApplicationMessagePump);

        // Keep Printed Content From Wrapping Off Screen
        try
        {
            Console.BufferHeight = Math.Max(Console.BufferHeight, 9999);
        }
        catch { }

        PrintConsoleHeader();
        PrintSettingsStatus();
        PrintTrayHint();

        s_games = GameScanner.ScanInstalledGameProcessNames();

        var uniqueDisplayNames = s_games
            .Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        LogList($"Games Found: {uniqueDisplayNames.Count}", uniqueDisplayNames, ConsoleColor.Cyan);

        // Snap The Window To Fit Everything Printed
        try
        {
            var neededHeight = Console.CursorTop + 3;
            Console.WindowHeight = Math.Min(neededHeight, Console.LargestWindowHeight - 1);
            Console.WindowTop = 0;
        }
        catch { }

        if (s_games.Count == 0)
        {
            Log("No Games Found", ConsoleColor.Red);
            Console.WriteLine("Press Any Key");
            Console.ReadKey(true);
            return;
        }

        if (Settings.StartMinimized)
            HideConsoleWindow();

        // Ctrl+C Must Not Kill The App
        Console.CancelKeyPress += (_, cancelEventArgs) => cancelEventArgs.Cancel = true;

        DisableConsoleInput();
        _ = WatchForMinimizeAsync();

        await RunMonitoringLoopAsync().ConfigureAwait(false);
    }

    private static void SelfTestNativeInterop()
    {
        // Callback Marshalling Breaks Silently, So Prove It Fires
        var seenWindows = 0;
        var enumerated = NativeMethods.EnumWindows(
            (windowHandle, _) =>
            {
                if (windowHandle != IntPtr.Zero)
                    seenWindows++;

                return true;
            },
            IntPtr.Zero
        );

        if (!enumerated || seenWindows == 0)
            throw new InvalidOperationException("SelfTest Failed: EnumWindows");

        // Fails Harmlessly When Standard Input Is Redirected
        DisableConsoleInput();
    }
    #endregion

    #region Console Window
    private static void WatchForShowConsoleSignal(EventWaitHandle waitHandle)
    {
        while (true)
        {
            waitHandle.WaitOne();
            ShowConsoleWindow();
            Log("Second Instance Detected, Showing Existing Window", ConsoleColor.DarkGray);
        }
    }

    private static void RunApplicationMessagePump()
    {
        // The Tray Icon Must Live On The Pump Thread
        s_trayManager = new TrayManager();
        Application.Run();
    }

    private static void DisableConsoleInput()
    {
        // The Console Only Reports Status, Typing Must Not Echo
        var inputHandle = NativeMethods.GetStdHandle(NativeMethods.StandardInputHandle);
        if (inputHandle == IntPtr.Zero || inputHandle == -1)
            return;

        if (NativeMethods.GetConsoleMode(inputHandle, out var mode))
        {
            NativeMethods.SetConsoleMode(
                inputHandle,
                mode & ~(NativeMethods.EnableEchoInput | NativeMethods.EnableLineInput)
            );
        }
    }

    private static void RemoveConsoleCloseButton()
    {
        var consoleWindowHandle = NativeMethods.GetConsoleWindow();
        if (consoleWindowHandle == IntPtr.Zero)
            return;

        var systemMenuHandle = NativeMethods.GetSystemMenu(consoleWindowHandle, revert: false);
        if (systemMenuHandle != IntPtr.Zero)
        {
            NativeMethods.DeleteMenu(
                systemMenuHandle,
                NativeMethods.SystemCommandClose,
                NativeMethods.MenuFlagByCommand
            );
        }
    }

    private static async Task WatchForMinimizeAsync()
    {
        while (true)
        {
            await Task.Delay(150).ConfigureAwait(false);

            if (!Settings.MinimizeToTray)
                continue;

            // Redirect Minimize To The Tray
            var consoleWindowHandle = NativeMethods.GetConsoleWindow();
            if (consoleWindowHandle != IntPtr.Zero && NativeMethods.IsIconic(consoleWindowHandle))
                NativeMethods.ShowWindow(consoleWindowHandle, NativeMethods.ShowWindowHide);
        }
    }

    private static void SetConsoleWindowState(int showCommand)
    {
        var consoleWindowHandle = NativeMethods.GetConsoleWindow();
        if (consoleWindowHandle != IntPtr.Zero)
            NativeMethods.ShowWindow(consoleWindowHandle, showCommand);
    }

    internal static void ShowConsoleWindow() =>
        SetConsoleWindowState(NativeMethods.ShowWindowRestore);

    private static void HideConsoleWindow() => SetConsoleWindowState(NativeMethods.ShowWindowHide);
    #endregion

    #region Settings
    internal static void SaveSettings()
    {
        Settings.Save();
        Log("Settings Saved", ConsoleColor.Green);
    }

    internal static void SetStartup(bool value)
    {
        Settings.StartupEnabled = value;
        ApplyStartupRegistration();
        SaveAndLogFlag("Startup", value);
    }

    internal static void SetTray(bool value)
    {
        Settings.MinimizeToTray = value;
        SaveAndLogFlag("MinimizeToTray", value);
    }

    internal static void SetStartMinimized(bool value)
    {
        Settings.StartMinimized = value;
        SaveAndLogFlag("StartMinimized", value);
    }

    internal static void SetNotifications(bool value)
    {
        Settings.NotificationsEnabled = value;
        SaveAndLogFlag("Notifications", value);
    }

    private static void SaveAndLogFlag(string name, bool value)
    {
        Settings.Save();
        Log($"{name} Set To {value}", ConsoleColor.Cyan);
    }

    private static void ApplyStartupRegistration()
    {
        if (Settings.StartupEnabled)
            StartupManager.Enable();
        else
            StartupManager.Disable();
    }

    internal static void ExitApplication()
    {
        s_trayManager?.Dispose();
        Environment.Exit(0);
    }
    #endregion

    #region Console Output
    private static void PrintConsoleHeader()
    {
        Console.Title = "Adrenalize";

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(
            """
                          _                      _ _
                 /\      | |                    | (_)
                /  \   __| |_ __ ___ _ __   __ _| |_   ____   ___
               / /\ \ / _` | '__/ _ \ '_ \ / _` | | | |_  /  / _ \
              / ____ \ (_| | | |  __/ | | | (_| | | |  / /  |  __/
             /_/    \_\__,_|_|  \___|_| |_|\__,_|_|_| /___|  \___|
            """
        );
        Console.ResetColor();
        Console.WriteLine();

        var version = typeof(Program)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine("Automatically Restarts AMD Adrenalin When A Game Launches");
        Console.WriteLine();
        Console.WriteLine($"Version: v{version}");
        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Issues And Features");
        Console.WriteLine("https://github.com/poolfullofmilk/Adrenalize");
        Console.ResetColor();
        Console.WriteLine();
    }

    internal static void PrintSettingsStatus()
    {
        (string Label, bool Value)[] flags =
        [
            ("Startup", Settings.StartupEnabled),
            ("MinimizeToTray", Settings.MinimizeToTray),
            ("StartMinimized", Settings.StartMinimized),
            ("Notifications", Settings.NotificationsEnabled),
        ];

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Current Status");

        foreach (var (label, value) in flags)
            Console.WriteLine($"  {label + ":", -16}{(value ? "TRUE" : "FALSE")}");

        Console.ResetColor();
        Console.WriteLine();
    }

    private static void PrintTrayHint()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Controls");
        Console.WriteLine("  Right Click The Tray Icon For Reset, Status, Settings And Exit");
        Console.WriteLine("  This Window Only Reports Status");
        Console.ResetColor();
        Console.WriteLine();
    }
    #endregion

    #region Monitoring
    private static async Task RunMonitoringLoopAsync()
    {
        Log("Watching for Games", ConsoleColor.Gray);

        var previouslyRunning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (true)
        {
            var currentlyRunning = GetRunningGameProcesses();
            var startedProcessName = currentlyRunning.FirstOrDefault(name =>
                !previouslyRunning.Contains(name)
            );

            if (startedProcessName is not null)
            {
                var displayName = s_games.TryGetValue(startedProcessName, out var niceName)
                    ? niceName
                    : startedProcessName;

                _ = TryTriggerResetAsync(displayName, isManual: false);
            }

            previouslyRunning = currentlyRunning;
            await Task.Delay(AppConfig.s_pollInterval).ConfigureAwait(false);
        }
    }

    private static HashSet<string> GetRunningGameProcesses()
    {
        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var processInstance in Process.GetProcesses())
            {
                using (processInstance)
                {
                    if (s_games.ContainsKey(processInstance.ProcessName))
                        running.Add(processInstance.ProcessName);
                }
            }
        }
        catch { }

        return running;
    }

    internal static void TriggerManualReset() =>
        _ = TryTriggerResetAsync("Manual Reset", isManual: true);

    private static async Task TryTriggerResetAsync(string startedDisplayName, bool isManual)
    {
        if (Interlocked.Exchange(ref s_pendingResetFlag, 1) == 1)
            return;

        try
        {
            Log($"Game Detected: {startedDisplayName}", ConsoleColor.Yellow);

            if (!isManual)
            {
                await Task.Delay(AppConfig.s_gameStartDelay).ConfigureAwait(false);

                // Abort If The Game Closed During The Delay
                if (GetRunningGameProcesses().Count == 0)
                {
                    Log("Game Closed Before Reset", ConsoleColor.DarkYellow);
                    Log("Watching for Games", ConsoleColor.Gray);
                    return;
                }
            }

            AmdReset.ExecuteReset();
            Log("Reset Done", ConsoleColor.Green);
            Log("Watching for Games", ConsoleColor.Gray);

            s_trayManager?.ShowBalloonTip("Adrenalize", $"Reset Done for {startedDisplayName}");
        }
        finally
        {
            Interlocked.Exchange(ref s_pendingResetFlag, 0);
        }
    }
    #endregion
}
