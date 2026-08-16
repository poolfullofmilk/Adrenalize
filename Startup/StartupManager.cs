using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.TaskScheduler;

namespace Adrenalize.Startup;

[SupportedOSPlatform("windows")]
internal static class StartupManager
{
    // Legacy Startup Location, Cleaned Up On Every Call
    private const string StartupRegistryKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    // Name Used In Registry And Task Scheduler
    private const string ApplicationName = "Adrenalize";

    internal static void Enable()
    {
        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(executablePath))
            return;

        RemoveRegistryEntry();

        // Highest Privileges Avoids A UAC Prompt On Logon
        using var taskService = new TaskService();
        var taskDefinition = taskService.NewTask();
        taskDefinition.RegistrationInfo.Description = "Starts Adrenalize on user logon";
        taskDefinition.Principal.RunLevel = TaskRunLevel.Highest;
        taskDefinition.Principal.LogonType = TaskLogonType.InteractiveToken;
        taskDefinition.Settings.DisallowStartIfOnBatteries = false;
        taskDefinition.Settings.StopIfGoingOnBatteries = false;
        taskDefinition.Settings.ExecutionTimeLimit = TimeSpan.Zero;

        taskDefinition.Triggers.Add(
            new LogonTrigger { UserId = Environment.UserDomainName + "\\" + Environment.UserName }
        );
        taskDefinition.Actions.Add(new ExecAction($"\"{executablePath}\""));

        taskService.RootFolder.RegisterTaskDefinition(ApplicationName, taskDefinition);
    }

    internal static void Disable()
    {
        RemoveRegistryEntry();

        using var taskService = new TaskService();
        try
        {
            taskService.RootFolder.DeleteTask(ApplicationName, exceptionOnNotExists: false);
        }
        catch { }
    }

    private static void RemoveRegistryEntry()
    {
        using var registryKey = Registry.CurrentUser.OpenSubKey(StartupRegistryKey, writable: true);
        registryKey?.DeleteValue(ApplicationName, throwOnMissingValue: false);
    }
}
