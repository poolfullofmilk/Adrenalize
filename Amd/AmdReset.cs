using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Adrenalize.Native;
using static Adrenalize.Utilities.Logger;

namespace Adrenalize.Amd;

[SupportedOSPlatform("windows")]
internal static class AmdReset
{
    // Adrenalin Executable Paths
    private static readonly string[] s_adrenalinExecutablePaths =
    [
        @"C:\Program Files\AMD\CNext\CNext\RadeonSoftware.exe",
        @"C:\Program Files\AMD\CNext\CNext\RadeonSettings.exe",
    ];

    // AMD Service And Process Keywords
    private static readonly string[] s_amdKeywords = ["AMD", "Radeon"];

    // AMD Executable Path Markers
    private static readonly string[] s_amdExecutablePathMarkers =
    [
        @"\AMD\",
        @"\Radeon\",
        @"\Advanced Micro Devices\",
        @"\CNext\",
    ];

    #region Reset
    internal static void ExecuteReset()
    {
        Log("Stopping AMD Services", ConsoleColor.DarkYellow);
        var stoppedServiceNames = StopAmdServices();

        Log("Stopping AMD Processes", ConsoleColor.DarkYellow);
        StopAmdProcesses();

        Log("Starting AMD Services", ConsoleColor.DarkGreen);
        StartAmdServices(stoppedServiceNames);

        Log("Starting Adrenalin", ConsoleColor.DarkGreen);
        if (StartAdrenalin())
            HideAdrenalinWindows();
    }

    private static bool ContainsAmdKeyword(string text) =>
        s_amdKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    #endregion

    #region Processes
    private static void StopAmdProcesses()
    {
        var loggedProcessIds = new HashSet<int>();
        var deadlineUtc = DateTime.UtcNow.AddSeconds(10);

        // Keep Sweeping Until Nothing Comes Back
        while (true)
        {
            var anyKilled = false;

            foreach (var processInstance in Process.GetProcesses())
            {
                using (processInstance)
                {
                    if (!IsAmdProcess(processInstance))
                        continue;

                    anyKilled = true;

                    if (loggedProcessIds.Add(processInstance.Id))
                    {
                        LogItem(
                            $"{processInstance.ProcessName} (PID {processInstance.Id})",
                            ConsoleColor.Yellow
                        );
                    }

                    try
                    {
                        processInstance.Kill(entireProcessTree: true);
                        processInstance.WaitForExit(1500);
                    }
                    catch { }
                }
            }

            if (!anyKilled || DateTime.UtcNow >= deadlineUtc)
                return;

            Thread.Sleep(200);
        }
    }

    private static bool IsAmdProcess(Process processInstance)
    {
        // Never Kill Ourselves
        if (processInstance.Id == Environment.ProcessId)
            return false;

        try
        {
            if (ContainsAmdKeyword(processInstance.ProcessName))
                return true;

            // Some AMD Binaries Are Named Differently
            var executablePath = processInstance.MainModule?.FileName;
            return executablePath is not null
                && s_amdExecutablePathMarkers.Any(marker =>
                    executablePath.Contains(marker, StringComparison.OrdinalIgnoreCase)
                );
        }
        catch
        {
            // Protected Processes Deny MainModule
            return false;
        }
    }
    #endregion

    #region Services
    private static List<string> StopAmdServices()
    {
        var stoppedServiceNames = InvokeOnServices(
            (name, displayName, state) =>
                (ContainsAmdKeyword(name) || ContainsAmdKeyword(displayName))
                && state.Equals("Running", StringComparison.OrdinalIgnoreCase),
            "StopService"
        );

        foreach (var serviceName in stoppedServiceNames)
            LogItem(serviceName, ConsoleColor.Yellow);

        WaitForServiceStates(
            stoppedServiceNames,
            ServiceControllerStatus.Stopped,
            TimeSpan.FromSeconds(15)
        );
        return stoppedServiceNames;
    }

    private static void StartAmdServices(List<string> serviceNames)
    {
        if (serviceNames.Count == 0)
            return;

        var startedServiceNames = InvokeOnServices(
            (name, _, state) =>
                serviceNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                && state.Equals("Stopped", StringComparison.OrdinalIgnoreCase),
            "StartService"
        );

        foreach (var serviceName in startedServiceNames)
            LogItem(serviceName, ConsoleColor.Green);

        WaitForServiceStates(
            startedServiceNames,
            ServiceControllerStatus.Running,
            TimeSpan.FromSeconds(10)
        );
    }

    private static List<string> InvokeOnServices(
        Func<string, string, string, bool> filter,
        string methodName
    )
    {
        var matchedServiceNames = new List<string>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DisplayName, State FROM Win32_Service"
            );

            foreach (var service in searcher.Get().Cast<ManagementObject>())
            {
                using (service)
                {
                    try
                    {
                        var name = service["Name"]?.ToString() ?? string.Empty;
                        var displayName = service["DisplayName"]?.ToString() ?? string.Empty;
                        var state = service["State"]?.ToString() ?? string.Empty;

                        if (!filter(name, displayName, state))
                            continue;

                        service.InvokeMethod(methodName, null);
                        matchedServiceNames.Add(name);
                    }
                    catch { }
                }
            }
        }
        catch { }

        return matchedServiceNames;
    }

    private static void WaitForServiceStates(
        List<string> serviceNames,
        ServiceControllerStatus targetStatus,
        TimeSpan timeout
    )
    {
        // One Shared Deadline, Not One Per Service
        var deadlineUtc = DateTime.UtcNow.Add(timeout);

        foreach (var serviceName in serviceNames)
        {
            var remaining = deadlineUtc - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return;

            try
            {
                using var serviceController = new ServiceController(serviceName);
                serviceController.WaitForStatus(targetStatus, remaining);
            }
            catch
            {
                // A Timeout Or A Vanished Service Must Not Stop The Reset
            }
        }
    }
    #endregion

    #region Adrenalin
    private static bool StartAdrenalin()
    {
        var executablePath = s_adrenalinExecutablePaths.FirstOrDefault(File.Exists);
        if (executablePath is null)
        {
            Log("Adrenalin Not Found", ConsoleColor.Red);
            return false;
        }

        try
        {
            // Hidden Launch Avoids A Window Flash
            Process.Start(
                new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                }
            );
        }
        catch
        {
            Log("Adrenalin Start Failed", ConsoleColor.Red);
            return false;
        }

        var deadlineUtc = DateTime.UtcNow.AddSeconds(15);

        while (DateTime.UtcNow < deadlineUtc)
        {
            var candidates = GetAdrenalinCandidateProcesses();

            if (candidates.Length > 0)
            {
                foreach (var candidate in candidates)
                {
                    using (candidate)
                    {
                        try
                        {
                            candidate.WaitForInputIdle(5000);
                        }
                        catch { }
                    }
                }

                Log("Adrenalin Started", ConsoleColor.Green);
                return true;
            }

            Thread.Sleep(250);
        }

        Log("Adrenalin Start Timed Out", ConsoleColor.Red);
        return false;
    }

    private static void HideAdrenalinWindows()
    {
        var deadlineUtc = DateTime.UtcNow.AddSeconds(20);
        var hidAnyWindow = false;
        var quietPasses = 0;

        // Adrenalin Ignores The Hidden Start Style And Shows Itself Anyway
        while (DateTime.UtcNow < deadlineUtc)
        {
            var windowHandles = GetAdrenalinWindowHandles();

            foreach (var windowHandle in windowHandles)
            {
                // Hide Kills The Flash, WM_CLOSE Sends Adrenalin To Its Tray
                NativeMethods.ShowWindow(windowHandle, NativeMethods.ShowWindowHide);
                NativeMethods.PostMessage(
                    windowHandle,
                    NativeMethods.WindowMessageClose,
                    IntPtr.Zero,
                    IntPtr.Zero
                );
            }

            if (windowHandles.Count > 0)
            {
                hidAnyWindow = true;
                quietPasses = 0;
            }
            else
            {
                quietPasses++;
            }

            // Stop Once Nothing Reappears For A Second
            if (hidAnyWindow && quietPasses >= 5)
                break;

            Thread.Sleep(200);
        }

        if (hidAnyWindow)
            Log("Adrenalin Hidden", ConsoleColor.Green);
        else
            Log("Adrenalin Window Not Found", ConsoleColor.DarkYellow);
    }

    private static List<IntPtr> GetAdrenalinWindowHandles()
    {
        var adrenalinProcessIds = new HashSet<uint>();

        foreach (var processInstance in GetAdrenalinCandidateProcesses())
        {
            using (processInstance)
                adrenalinProcessIds.Add((uint)processInstance.Id);
        }

        var windowHandles = new List<IntPtr>();
        if (adrenalinProcessIds.Count == 0)
            return windowHandles;

        // MainWindowHandle Picks The Wrong Window, Walk Every Top Level Window Instead
        try
        {
            NativeMethods.EnumWindows(
                (windowHandle, _) =>
                {
                    if (NativeMethods.IsWindowVisible(windowHandle))
                    {
                        NativeMethods.GetWindowThreadProcessId(windowHandle, out var ownerId);
                        if (adrenalinProcessIds.Contains(ownerId))
                            windowHandles.Add(windowHandle);
                    }

                    return true;
                },
                IntPtr.Zero
            );
        }
        catch { }

        return windowHandles;
    }

    private static Process[] GetAdrenalinCandidateProcesses()
    {
        try
        {
            // Newer Drivers Ship RadeonSoftware, Older Ship RadeonSettings
            return
            [
                .. Process.GetProcessesByName("RadeonSoftware"),
                .. Process.GetProcessesByName("RadeonSettings"),
            ];
        }
        catch
        {
            return [];
        }
    }
    #endregion
}
