using System.Runtime.Versioning;

namespace Adrenalize.Tray;

[SupportedOSPlatform("windows")]
internal sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    // Toggles Refreshed Each Time The Menu Opens
    private readonly ToolStripMenuItem _startupToggleItem;
    private readonly ToolStripMenuItem _trayToggleItem;
    private readonly ToolStripMenuItem _startMinimizedToggleItem;
    private readonly ToolStripMenuItem _notificationsToggleItem;

    internal TrayManager()
    {
        var contextMenu = new ContextMenuStrip();
        contextMenu.Opening += (_, _) => RefreshToggleStates();

        AddItem(contextMenu, "Open Console", Program.ShowConsoleWindow);
        contextMenu.Items.Add(new ToolStripSeparator());

        AddItem(contextMenu, "Reset", Program.TriggerManualReset);
        AddItem(
            contextMenu,
            "Status",
            () =>
            {
                Program.ShowConsoleWindow();
                Program.PrintSettingsStatus();
            }
        );
        AddItem(
            contextMenu,
            "Save",
            () =>
            {
                Program.ShowConsoleWindow();
                Program.SaveSettings();
            }
        );
        contextMenu.Items.Add(new ToolStripSeparator());

        _startupToggleItem = AddToggle(contextMenu, "Run on Startup", Program.SetStartup);
        _trayToggleItem = AddToggle(contextMenu, "Minimize to Tray", Program.SetTray);
        _startMinimizedToggleItem = AddToggle(
            contextMenu,
            "Start Minimized",
            Program.SetStartMinimized
        );
        _notificationsToggleItem = AddToggle(
            contextMenu,
            "Notifications",
            Program.SetNotifications
        );
        contextMenu.Items.Add(new ToolStripSeparator());

        AddItem(contextMenu, "Exit", Program.ExitApplication);

        _notifyIcon = new NotifyIcon
        {
            Text = "Adrenalize",
            Icon = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Visible = true,
        };

        _notifyIcon.DoubleClick += (_, _) => Program.ShowConsoleWindow();
    }

    private static void AddItem(ContextMenuStrip menu, string text, Action onClick)
    {
        var item = new ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        menu.Items.Add(item);
    }

    private static ToolStripMenuItem AddToggle(
        ContextMenuStrip menu,
        string text,
        Action<bool> onToggle
    )
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = true };
        item.Click += (_, _) => onToggle(item.Checked);
        menu.Items.Add(item);
        return item;
    }

    internal void ShowBalloonTip(string title, string message)
    {
        if (!Program.Settings.NotificationsEnabled)
            return;

        _notifyIcon.BalloonTipTitle = title;
        _notifyIcon.BalloonTipText = message;
        _notifyIcon.BalloonTipIcon = ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(3000);
    }

    private void RefreshToggleStates()
    {
        var settings = Program.Settings;
        _startupToggleItem.Checked = settings.StartupEnabled;
        _trayToggleItem.Checked = settings.MinimizeToTray;
        _startMinimizedToggleItem.Checked = settings.StartMinimized;
        _notificationsToggleItem.Checked = settings.NotificationsEnabled;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
