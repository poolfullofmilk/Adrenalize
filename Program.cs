using System.Diagnostics;
using System.Management;
using System.Reflection;
using System.Runtime.Versioning;
using static Adrenalize.Logger;

namespace Adrenalize;

[SupportedOSPlatform("windows")]
internal static class Program
{
    // Guards Against Overlapping Resets
    private static int s_pendingResetFlag;
    private static TrayManager? s_trayManager;

    // Process Name To Display Name
    private static Dictionary<string, string> s_games = [];

    // Kept Alive For The Lifetime Of The Process
    private static ManagementEventWatcher? s_processWatcher;
    private static NativeMethods.WinEventCallback? s_minimizeCallback;

    private const string SingleInstanceMutexName = "Global\\Adrenalize_SingleInstance";
    private const string ShowConsoleEventName = "Global\\Adrenalize_ShowConsole";

    // Long Enough For Any Game To Finish Loading
    private static readonly TimeSpan s_gameStartDelay = TimeSpan.FromSeconds(30);

    internal static UserSettings Settings { get; private set; } = new();

    // Drives Both The Tray Toggles And The Status Block
    internal static (string Label, Func<bool> Read, Action<bool> Write)[] SettingToggles { get; } =
        [
            (
                "Run On Startup",
                () => Settings.StartupEnabled,
                value =>
                {
                    Settings.StartupEnabled = value;
                    ApplyStartupRegistration();
                }
            ),
            (
                "Minimize To Tray",
                () => Settings.MinimizeToTray,
                value => Settings.MinimizeToTray = value
            ),
            (
                "Start Minimized",
                () => Settings.StartMinimized,
                value => Settings.StartMinimized = value
            ),
            (
                "Notifications",
                () => Settings.NotificationsEnabled,
                value => Settings.NotificationsEnabled = value
            ),
        ];

    #region Entry Point
    private static async Task Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            GameScanner.SelfTest();
            UserSettings.SelfTest();
            Logger.SelfTest();
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

        StartLogFile();

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

        ScanGames();

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
            SetConsoleWindowState(NativeMethods.ShowWindowHide);

        // Ctrl+C Must Not Kill The App
        Console.CancelKeyPress += (_, cancelEventArgs) => cancelEventArgs.Cancel = true;

        DisableConsoleInput();
        StartGameWatcher();

        await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
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
        // The Tray Icon And The Window Hook Need The Pump Thread
        s_trayManager = new TrayManager();
        WatchForMinimize();
        Application.Run();
    }

    private static void WatchForMinimize()
    {
        // Redirect Minimize To The Tray
        s_minimizeCallback = (_, _, windowHandle, _, _, _, _) =>
        {
            if (Settings.MinimizeToTray && windowHandle == NativeMethods.GetConsoleWindow())
                NativeMethods.ShowWindow(windowHandle, NativeMethods.ShowWindowHide);
        };

        NativeMethods.SetWinEventHook(
            NativeMethods.EventSystemMinimizeStart,
            NativeMethods.EventSystemMinimizeStart,
            IntPtr.Zero,
            s_minimizeCallback,
            processIdentifier: 0,
            threadIdentifier: 0,
            NativeMethods.WinEventOutOfContext
        );
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

    private static void SetConsoleWindowState(int showCommand)
    {
        var consoleWindowHandle = NativeMethods.GetConsoleWindow();
        if (consoleWindowHandle != IntPtr.Zero)
            NativeMethods.ShowWindow(consoleWindowHandle, showCommand);
    }

    internal static void ShowConsoleWindow() =>
        SetConsoleWindowState(NativeMethods.ShowWindowRestore);
    #endregion

    #region Settings
    internal static void ApplySettingToggle(int index, bool value)
    {
        var (label, _, write) = SettingToggles[index];
        write(value);
        Settings.Save();
        Log($"{label} Set To {(value ? "TRUE" : "FALSE")}", ConsoleColor.Cyan);
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
        Console.WriteLine();
        Console.WriteLine("Log File");
        Console.WriteLine(s_logFilePath);
        Console.ResetColor();
        Console.WriteLine();
    }

    internal static void PrintSettingsStatus()
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Current Status");

        foreach (var (label, read, _) in SettingToggles)
            Console.WriteLine($"  {label + ":", -18}{(read() ? "TRUE" : "FALSE")}");

        Console.ResetColor();
    }

    private static void PrintTrayHint()
    {
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Controls");
        Console.WriteLine("  Right Click The Tray Icon For Reset, Status, Settings And Exit");
        Console.WriteLine("  This Window Only Reports Status");
        Console.ResetColor();
        Console.WriteLine();
    }
    #endregion

    #region Monitoring
    private static void ScanGames()
    {
        s_games = GameScanner.ScanInstalledGameProcessNames();

        var uniqueDisplayNames = s_games
            .Values.Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Log($"Games Found: {uniqueDisplayNames.Count}");

        foreach (var displayName in uniqueDisplayNames)
            LogItem(displayName, ConsoleColor.Cyan);
    }

    internal static void RescanGames()
    {
        ShowConsoleWindow();
        ScanGames();
        Log("Watching For Games", ConsoleColor.Gray);
    }

    private static void StartGameWatcher()
    {
        Log("Watching For Games", ConsoleColor.Gray);

        try
        {
            // WMI Reports Every Process Start, No Polling Needed
            var startQuery = new WqlEventQuery(
                "__InstanceCreationEvent",
                TimeSpan.FromSeconds(1),
                "TargetInstance isa 'Win32_Process'"
            );

            s_processWatcher = new ManagementEventWatcher(startQuery);
            s_processWatcher.EventArrived += OnProcessStarted;
            s_processWatcher.Start();
        }
        catch
        {
            Log("Process Watcher Failed To Start", ConsoleColor.Red);
        }
    }

    private static void OnProcessStarted(object sender, EventArrivedEventArgs eventArguments)
    {
        try
        {
            var startedProcess = (ManagementBaseObject)eventArguments.NewEvent["TargetInstance"];
            var processName = GameScanner.NormalizeProcessKey(
                Path.GetFileNameWithoutExtension(startedProcess["Name"]?.ToString() ?? string.Empty)
            );

            if (s_games.TryGetValue(processName, out var displayName))
                _ = TryTriggerResetAsync(displayName, processName, isManual: false);
        }
        catch { }
    }

    internal static void TriggerManualReset() =>
        _ = TryTriggerResetAsync("Manual Reset", processName: null, isManual: true);

    private static async Task TryTriggerResetAsync(
        string startedDisplayName,
        string? processName,
        bool isManual
    )
    {
        if (Interlocked.Exchange(ref s_pendingResetFlag, 1) == 1)
            return;

        try
        {
            Log($"Game Detected: {startedDisplayName}", ConsoleColor.Yellow);

            if (!isManual)
            {
                await Task.Delay(s_gameStartDelay).ConfigureAwait(false);

                // Abort If The Game Closed During The Delay
                if (!IsGameRunning(processName))
                {
                    Log("Game Closed Before Reset", ConsoleColor.DarkYellow);
                    Log("Watching For Games", ConsoleColor.Gray);
                    return;
                }
            }

            if (AmdReset.ExecuteReset())
            {
                Log("Reset Done", ConsoleColor.Green);
                s_trayManager?.ShowBalloonTip("Adrenalize", $"Reset Done For {startedDisplayName}");
            }
            else
            {
                Log("Reset Incomplete", ConsoleColor.Red);
                s_trayManager?.ShowBalloonTip("Adrenalize", "Reset Incomplete, Check The Console");
            }

            Log("Watching For Games", ConsoleColor.Gray);
        }
        finally
        {
            Interlocked.Exchange(ref s_pendingResetFlag, 0);
        }
    }

    private static bool IsGameRunning(string? processName)
    {
        if (processName is null)
            return true;

        try
        {
            foreach (var processInstance in Process.GetProcesses())
            {
                using (processInstance)
                {
                    if (GameScanner.NormalizeProcessKey(processInstance.ProcessName) == processName)
                        return true;
                }
            }
        }
        catch { }

        return false;
    }
    #endregion
}
