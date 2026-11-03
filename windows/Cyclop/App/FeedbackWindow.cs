using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Cyclop.Services;
using Cyclop.UI;

namespace Cyclop.App;

/// <summary>
/// "Report a bug or suggest an idea" — an ordinary window rather than a tab:
/// writing a paragraph wants a window that keeps the keyboard and stays put
/// when the pointer wanders off, which is exactly what the panel does not do.
/// </summary>
internal sealed class FeedbackWindow : Window
{
    static FeedbackWindow? open;

    readonly Clickable bugButton, ideaButton;
    readonly Field text, contact;
    readonly ToggleSwitch attachLog;
    readonly TextBlock status;
    readonly Clickable send;
    bool isBug = true;
    bool logTouched;

    /// <summary>One window at a time; asking again brings it forward.</summary>
    public static void Open(bool bug = true)
    {
        if (open is null)
        {
            open = new FeedbackWindow(bug);
            open.Closed += (_, _) => open = null;
            open.Show();
        }
        if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;
        open.Activate();
    }

    FeedbackWindow(bool bug)
    {
        Title = L10n.T("Report a Bug or Suggest an Idea");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Hex("#141416");
        Foreground = Theme.White;
        FontFamily = Theme.Font;
        try { Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/cyclop.ico")); } catch { }

        var heading = Ui.Label(L10n.T("Write to the author"), 18, Theme.White, FontWeights.SemiBold);
        var lead = Ui.Label(L10n.T("Something broken, or an idea for what to add? Every message is read."), 12, Theme.Secondary);
        lead.TextWrapping = TextWrapping.Wrap;
        lead.TextTrimming = TextTrimming.None;
        lead.Margin = new Thickness(0, 4, 0, 16);

        bugButton = Segment(L10n.T("Bug"), () => SetKind(true));
        ideaButton = Segment(L10n.T("Idea"), () => SetKind(false));
        var kinds = new UniformGridRow(bugButton, ideaButton);

        text = new Field(L10n.T("What happened, or what should Cyclop do?"), multiline: true, size: 13);
        text.Box.MinHeight = 140;
        text.Box.MaxHeight = 260;
        text.Box.MaxLength = 5000;
        text.Margin = new Thickness(0, 12, 0, 0);
        text.Box.TextChanged += (_, _) => UpdateSend();

        contact = new Field(L10n.T("Telegram @name or email — if you want an answer"), size: 12.5);
        contact.Box.MaxLength = 200;
        contact.Margin = new Thickness(0, 10, 0, 0);

        attachLog = new ToggleSwitch(true) { VerticalAlignment = VerticalAlignment.Center };
        attachLog.Toggled += _ => logTouched = true;
        var logLabel = Ui.Label(L10n.T("Attach the work log — helps find the bug"), 12, Theme.White);
        logLabel.Margin = new Thickness(10, 0, 0, 0);
        var logRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(attachLog, Dock.Left);
        logRow.Children.Add(attachLog);
        logRow.Children.Add(logLabel);
        var logHint = Ui.Label(L10n.T("The log has the app's version, Windows version and which media apps it saw. No passwords, files or clipboard contents."), 10.5, Theme.Tertiary);
        logHint.TextWrapping = TextWrapping.Wrap;
        logHint.TextTrimming = TextTrimming.None;
        logHint.Margin = new Thickness(40, 4, 0, 0);

        status = Ui.Label("", 12, Theme.Secondary);
        status.TextWrapping = TextWrapping.Wrap;
        status.TextTrimming = TextTrimming.None;
        send = Ui.PillButton(L10n.T("Send"), () => _ = SendAsync(), accent: true);
        send.Padding = new Thickness(22, 8, 22, 8);
        var footer = new DockPanel { Margin = new Thickness(0, 20, 0, 0) };
        DockPanel.SetDock(send, Dock.Right);
        footer.Children.Add(send);
        footer.Children.Add(status);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 22) };
        stack.Children.Add(heading);
        stack.Children.Add(lead);
        stack.Children.Add(kinds);
        stack.Children.Add(text);
        stack.Children.Add(contact);
        stack.Children.Add(logRow);
        stack.Children.Add(logHint);
        stack.Children.Add(footer);
        Content = stack;

        SetKind(bug);
        UpdateSend();
        if (!UpdateService.IsEnabled)
        {
            status.Text = L10n.T("This build has no server to send to. Download Cyclop from the website to write from the app.");
            status.Foreground = Theme.Warning;
        }
        Loaded += (_, _) => text.Box.Focus();
        SourceInitialized += (_, _) => DarkTitleBar();
    }

    Clickable Segment(string title, Action click)
    {
        var label = Ui.Label(title, 13, Theme.White, FontWeights.Medium);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        var b = new Clickable { Height = 34, CornerRadius = new CornerRadius(9), Child = label, Margin = new Thickness(0, 0, 6, 0) };
        b.Click += click;
        return b;
    }

    void SetKind(bool bug)
    {
        isBug = bug;
        foreach (var (button, on) in new[] { (bugButton, bug), (ideaButton, !bug) })
        {
            button.Normal = on ? Theme.AccentSoft : Theme.Surface;
            button.Hover = on ? Theme.AccentSoft : Theme.SurfaceHover;
            button.Refresh();
        }
        // A log helps with a bug and says nothing about an idea; once the
        // sender has decided for themselves, the switch is theirs.
        if (!logTouched) attachLog.IsOn = bug;
    }

    void UpdateSend() => send.IsEnabled = UpdateService.IsEnabled && text.Text.Trim().Length >= 3;

    async Task SendAsync()
    {
        if (UpdateService.Server is not { } server) return;
        send.IsEnabled = false;
        status.Foreground = Theme.Secondary;
        status.Text = L10n.T("Sending…");
        try
        {
            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";
            await FeedbackClient.SendAsync(server, isBug, text.Text.Trim(), contact.Text.Trim(),
                UpdateService.CurrentVersion, Environment.OSVersion.Version.ToString(), arch,
                attachLog.IsOn ? FeedbackClient.LogTail() : null);
            status.Foreground = Theme.Hex("#30D158");
            status.Text = L10n.T("Thank you! The message has been sent.");
            text.Text = "";
            await Task.Delay(1600);
            Close();
        }
        catch (Exception e)
        {
            Support.Log("feedback failed: " + e.Message);
            status.Foreground = Theme.Warning;
            status.Text = L10n.T("Could not send. Check the connection and try again.");
            UpdateSend();
        }
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>A dark title bar over a dark window (Windows 10 2004+).</summary>
    void DarkTitleBar()
    {
        try
        {
            int on = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref on, sizeof(int));
        }
        catch { }
    }

    /// <summary>Two equal buttons side by side.</summary>
    sealed class UniformGridRow : System.Windows.Controls.Primitives.UniformGrid
    {
        public UniformGridRow(params UIElement[] children)
        {
            Columns = children.Length;
            foreach (var c in children) Children.Add(c);
        }
    }
}
