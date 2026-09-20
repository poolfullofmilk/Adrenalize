using System.Runtime.Versioning;

namespace Adrenalize;

[SupportedOSPlatform("windows")]
internal sealed class TrayManager : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    // Refreshed Each Time The Menu Opens
    private readonly List<ToolStripMenuItem> _toggleItems = [];

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
        AddItem(contextMenu, "Rescan Games", Program.RescanGames);
        contextMenu.Items.Add(new ToolStripSeparator());

        for (var index = 0; index < Program.SettingToggles.Length; index++)
        {
            var toggleIndex = index;
            var item = new ToolStripMenuItem(Program.SettingToggles[index].Label)
            {
                CheckOnClick = true,
            };
            item.Click += (_, _) => Program.ApplySettingToggle(toggleIndex, item.Checked);
            contextMenu.Items.Add(item);
            _toggleItems.Add(item);
        }

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
        for (var index = 0; index < _toggleItems.Count; index++)
            _toggleItems[index].Checked = Program.SettingToggles[index].Read();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
