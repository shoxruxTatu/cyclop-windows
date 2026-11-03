using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Cyclop.App;

/// <summary>
/// The notification-area icon: the two things people reach for in a hurry —
/// opening the panel and hiding its contents with a camera already running —
/// and Quit. Everything else is configuration and lives in the Settings tab.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    readonly NotifyIcon icon;
    readonly AppModel model;
    readonly ToolStripMenuItem privacyItem;
    readonly ToolStripMenuItem privacyAll;
    readonly (PrivacySection Section, ToolStripMenuItem Item)[] sectionItems;

    public event Action? TogglePanel;
    public event Action? AreaShot;
    public event Action? ScreenShot;
    public event Action? Quit;

    public TrayIcon(AppModel model)
    {
        this.model = model;
        var menu = new ContextMenuStrip();
        var version = typeof(TrayIcon).Assembly.GetName().Version;
        menu.Items.Add(new ToolStripMenuItem($"Cyclop {version?.ToString(3)}") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L10n.T("Open Panel"), null, (_, _) => TogglePanel?.Invoke()));
        var area = new ToolStripMenuItem(L10n.T("Screenshot of an Area"), null, (_, _) => AreaShot?.Invoke());
        var screen = new ToolStripMenuItem(L10n.T("Screenshot of the Screen"), null, (_, _) => ScreenShot?.Invoke());
        menu.Items.Add(area);
        menu.Items.Add(screen);
        menu.Opening += (_, _) =>
        {
            area.ShortcutKeyDisplayString = model.Config.HotkeyArea;
            screen.ShortcutKeyDisplayString = model.Config.HotkeyScreen;
        };

        privacyItem = new ToolStripMenuItem(L10n.T("Hide Contents"));
        privacyAll = new ToolStripMenuItem(L10n.T("All"), null, (_, _) =>
        {
            // Anything short of everything means "the rest too"; only a full
            // house turns them all off.
            model.Privacy.SetCoveringAll(!model.Privacy.CoversAll);
            RefreshPrivacy();
        });
        privacyItem.DropDownItems.Add(privacyAll);
        privacyItem.DropDownItems.Add(new ToolStripSeparator());
        sectionItems = Enum.GetValues<PrivacySection>().Select(section =>
        {
            var item = new ToolStripMenuItem(PrivacyMode.Title(section), null, (_, _) =>
            {
                model.Privacy.SetCovering(section, !model.Privacy.Covers(section));
                RefreshPrivacy();
            });
            privacyItem.DropDownItems.Add(item);
            return (section, item);
        }).ToArray();
        menu.Items.Add(privacyItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(L10n.T("Quit"), null, (_, _) => Quit?.Invoke()));
        menu.Opening += (_, _) => RefreshPrivacy();

        icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "Cyclop",
            ContextMenuStrip = menu,
            Visible = model.Config.ShowTrayIcon,
        };
        icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) TogglePanel?.Invoke();
        };
    }

    static Icon LoadIcon()
    {
        try
        {
            var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/cyclop.ico"));
            if (info is not null) return new Icon(info.Stream, SystemInformation.SmallIconSize);
        }
        catch { }
        return SystemIcons.Application;
    }

    /// <summary>A notification from the tray icon.</summary>
    public void Notify(string text, bool warning = true)
    {
        if (!icon.Visible) return;
        icon.ShowBalloonTip(6000, "Cyclop", text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    public bool Visible
    {
        get => icon.Visible;
        set => icon.Visible = value;
    }

    /// <summary>A tick when every section is covered, a partial mark when some are.</summary>
    void RefreshPrivacy()
    {
        var p = model.Privacy;
        privacyItem.CheckState = p.CoversAll ? CheckState.Checked : p.CoversAny ? CheckState.Indeterminate : CheckState.Unchecked;
        privacyAll.Checked = p.CoversAll;
        foreach (var (section, item) in sectionItems) item.Checked = p.Covers(section);
    }

    public void Dispose()
    {
        icon.Visible = false;
        icon.Dispose();
    }
}
