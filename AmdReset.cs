using System.Diagnostics;
using System.Management;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Win32.TaskScheduler;
using static Adrenalize.Logger;

namespace Adrenalize;

[SupportedOSPlatform("windows")]
internal static class AmdReset
{
    // AMD's Own Command Line, Used To Start And Hide Adrenalin
    private const string AdrenalinCommandPath = @"C:\Program Files\AMD\CNext\CNext\cncmd.exe";

    // Temporary Task Used To Drop Elevation
    private const string LaunchTaskName = "AdrenalizeLaunch";

    // Services Adrenalin Needs, Everything Else Stays Untouched
    private static readonly string[] s_requiredServiceNames =
    [
        "AMD External Events Utility",
        "AMD Crash Defender Service",
    ];

    // AMD Process Keywords
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
    internal static bool ExecuteReset()
    {
        Log("Stopping AMD Services", ConsoleColor.DarkYellow);
        ControlServices(
            (name, _, state) =>
                s_requiredServiceNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                && state.Equals("Running", StringComparison.OrdinalIgnoreCase),
            "StopService",
            ServiceControllerStatus.Stopped,
            TimeSpan.FromSeconds(15),
            ConsoleColor.Yellow
        );

        Log("Stopping AMD Processes", ConsoleColor.DarkYellow);
        StopAmdProcesses();

        // Start Every Required Service, Not Just The Ones This Reset Stopped
        Log("Starting AMD Services", ConsoleColor.DarkGreen);
        ControlServices(
            (name, _, state) =>
                s_requiredServiceNames.Contains(name, StringComparer.OrdinalIgnoreCase)
                && !state.Equals("Running", StringComparison.OrdinalIgnoreCase),
            "StartService",
            ServiceControllerStatus.Running,
            TimeSpan.FromSeconds(25),
            ConsoleColor.Green
        );

        Log("Starting Adrenalin", ConsoleColor.DarkGreen);
        if (!StartAdrenalin())
            return false;

        HideAdrenalin();
        return VerifyReset();
    }

    private static bool VerifyReset()
    {
        var deadServiceNames = s_requiredServiceNames
            .Where(name => !IsServiceRunning(name))
            .ToList();

        foreach (var serviceName in deadServiceNames)
            LogItem($"{serviceName} Not Running", ConsoleColor.Red);

        if (GetAdrenalinProcessIds().Count == 0)
        {
            Log("Adrenalin Not Running", ConsoleColor.Red);
            return false;
        }

        return deadServiceNames.Count == 0;
    }

    // AMD64 Is An Architecture Name, Not An AMD Product
    private static bool ContainsAmdKeyword(string text) =>
        !text.Contains("AMD64", StringComparison.OrdinalIgnoreCase)
        && s_amdKeywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    #endregion

    #region Processes
    private static void StopAmdProcesses()
    {
        var loggedProcessIds = new HashSet<int>();
        var skippedProcessIds = new HashSet<int>();
        var foreignServiceProcessIds = GetForeignServiceProcessIds();
        var deadlineUtc = DateTime.UtcNow.AddSeconds(10);

        // Keep Sweeping Until Nothing Comes Back
        while (true)
        {
            var anyKilled = false;

            foreach (var processInstance in Process.GetProcesses())
            {
                using (processInstance)
                {
                    // Already Judged Not AMD On An Earlier Sweep
                    if (skippedProcessIds.Contains(processInstance.Id))
                        continue;

                    if (!IsAmdProcess(processInstance, foreignServiceProcessIds))
                    {
                        skippedProcessIds.Add(processInstance.Id);
                        continue;
                    }

                    anyKilled = true;

                    if (loggedProcessIds.Add(processInstance.Id))
                        LogItem(DescribeProcess(processInstance), ConsoleColor.Yellow);

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

    private static string DescribeProcess(Process processInstance)
    {
        try
        {
            // The File Description Is What Task Manager Shows
            var description = processInstance.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(description))
                return $"{description} (PID {processInstance.Id})";
        }
        catch { }

        return $"{processInstance.ProcessName} (PID {processInstance.Id})";
    }

    private static bool IsAmdProcess(Process processInstance, HashSet<int> foreignServiceProcessIds)
    {
        // Never Kill Ourselves Or A Service Nobody Asked Us To Touch
        if (
            processInstance.Id == Environment.ProcessId
            || foreignServiceProcessIds.Contains(processInstance.Id)
        )
            return false;

        try
        {
            var mainModule = processInstance.MainModule;

            if (mainModule is not null)
            {
                // The Publisher Is The Only Consistent Signal
                var company = mainModule.FileVersionInfo.CompanyName ?? string.Empty;
                if (company.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                    return true;

                if (
                    s_amdExecutablePathMarkers.Any(marker =>
                        mainModule.FileName.Contains(marker, StringComparison.OrdinalIgnoreCase)
                    )
                )
                    return true;
            }
        }
        catch
        {
            // Protected Processes Deny MainModule
        }

        return ContainsAmdKeyword(processInstance.ProcessName);
    }

    private static HashSet<int> GetForeignServiceProcessIds()
    {
        var processIds = new HashSet<int>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, ProcessId FROM Win32_Service WHERE State = 'Running'"
            );

            foreach (var service in searcher.Get().Cast<ManagementObject>())
            {
                using (service)
                {
                    var name = service["Name"]?.ToString() ?? string.Empty;
                    if (s_requiredServiceNames.Contains(name, StringComparer.OrdinalIgnoreCase))
                        continue;

                    var processId = Convert.ToInt32(service["ProcessId"] ?? 0);
                    if (processId > 0)
                        processIds.Add(processId);
                }
            }
        }
        catch { }

        return processIds;
    }
    #endregion

    #region Services
    private static List<string> ControlServices(
        Func<string, string, string, bool> filter,
        string methodName,
        ServiceControllerStatus targetStatus,
        TimeSpan timeout,
        ConsoleColor color
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

                        var resultCode = Convert.ToUInt32(
                            service.InvokeMethod(methodName, null) ?? 0u
                        );

                        var friendlyName = displayName.Length > 0 ? displayName : name;

                        if (resultCode != 0)
                        {
                            LogItem(
                                $"{friendlyName} Failed With Code {resultCode}",
                                ConsoleColor.Red
                            );
                            continue;
                        }

                        LogItem(friendlyName, color);
                        matchedServiceNames.Add(name);
                    }
                    catch { }
                }
            }
        }
        catch { }

        WaitForServiceStates(matchedServiceNames, targetStatus, timeout);
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

    private static bool IsServiceRunning(string serviceName)
    {
        try
        {
            using var serviceController = new ServiceController(serviceName);
            return serviceController.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }
    #endregion

    #region Adrenalin
    private static bool StartAdrenalin()
    {
        if (!File.Exists(AdrenalinCommandPath))
        {
            Log("Adrenalin Not Found", ConsoleColor.Red);
            return false;
        }

        // The Logon Command Starts Adrenalin Without Ever Drawing A Window
        if (!RunAsSignedInUser(AdrenalinCommandPath, "startwithdelay"))
        {
            Log("Adrenalin Start Failed", ConsoleColor.Red);
            return false;
        }

        var deadlineUtc = DateTime.UtcNow.AddSeconds(60);

        while (DateTime.UtcNow < deadlineUtc)
        {
            if (GetAdrenalinProcessIds().Count > 0)
            {
                Log("Adrenalin Started", ConsoleColor.Green);
                return true;
            }

            Thread.Sleep(250);
        }

        Log("Adrenalin Start Timed Out", ConsoleColor.Red);
        return false;
    }

    private static bool RunAsSignedInUser(string executablePath, string arguments)
    {
        try
        {
            // Adrenalin Quits During Startup When It Inherits Elevation
            using var taskService = new TaskService();
            var taskDefinition = taskService.NewTask();
            taskDefinition.RegistrationInfo.Description = "Starts Adrenalin without elevation";
            taskDefinition.Principal.RunLevel = TaskRunLevel.LUA;
            taskDefinition.Principal.LogonType = TaskLogonType.InteractiveToken;
            taskDefinition.Principal.UserId =
                Environment.UserDomainName + "\\" + Environment.UserName;
            taskDefinition.Settings.DisallowStartIfOnBatteries = false;
            taskDefinition.Settings.StopIfGoingOnBatteries = false;
            taskDefinition.Settings.AllowHardTerminate = false;
            taskDefinition.Settings.ExecutionTimeLimit = TimeSpan.Zero;
            taskDefinition.Actions.Add(new ExecAction($"\"{executablePath}\"", arguments));

            taskService.RootFolder.RegisterTaskDefinition(LaunchTaskName, taskDefinition).Run();

            // Let Task Scheduler Spawn Before The Task Is Removed
            Thread.Sleep(1000);
            taskService.RootFolder.DeleteTask(LaunchTaskName, exceptionOnNotExists: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void HideAdrenalin()
    {
        var deadlineUtc = DateTime.UtcNow.AddSeconds(15);
        var adrenalinProcessIds = GetAdrenalinProcessIds();
        var hidAnyWindow = false;
        var passes = 0;
        var quietPasses = 0;

        // Insurance Only, The Logon Command Should Never Draw A Window
        while (DateTime.UtcNow < deadlineUtc)
        {
            // Adrenalin Relaunches Itself, Refresh The Owners Every Second
            if (passes++ % 10 == 0)
                adrenalinProcessIds = GetAdrenalinProcessIds();

            var windowHandles = GetAdrenalinWindowHandles(adrenalinProcessIds);

            if (windowHandles.Count > 0)
            {
                // Hide First So Nothing Flashes, Then Tell Adrenalin Itself
                foreach (var windowHandle in windowHandles)
                    NativeMethods.ShowWindow(windowHandle, NativeMethods.ShowWindowHide);

                if (File.Exists(AdrenalinCommandPath))
                    RunAsSignedInUser(AdrenalinCommandPath, "hide");

                hidAnyWindow = true;
                quietPasses = 0;
                continue;
            }

            // Stop Once Nothing Reappears For Five Seconds
            if (hidAnyWindow && ++quietPasses >= 50)
                break;

            Thread.Sleep(100);
        }

        if (hidAnyWindow)
            Log("Adrenalin Window Hidden", ConsoleColor.DarkYellow);
        else
            Log("Adrenalin Running In The Background", ConsoleColor.Green);
    }

    private static List<IntPtr> GetAdrenalinWindowHandles(HashSet<uint> adrenalinProcessIds)
    {
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

    private static HashSet<uint> GetAdrenalinProcessIds()
    {
        var processIds = new HashSet<uint>();

        try
        {
            foreach (var processInstance in Process.GetProcessesByName("RadeonSoftware"))
            {
                using (processInstance)
                    processIds.Add((uint)processInstance.Id);
            }
        }
        catch { }

        return processIds;
    }
    #endregion
}
