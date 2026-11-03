using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.Interop;
using Cyclop.Panes;
using Cyclop.UI;

namespace Cyclop.Panel;

/// <summary>A display as the panel needs it: physical-pixel bounds and scale.</summary>
internal sealed record Monitor(IntPtr Handle, string Device, Native.RECT Bounds, double Scale, bool IsPrimary)
{
    public int CenterX => (Bounds.Left + Bounds.Right) / 2;
}

/// <summary>
/// The panel: one borderless, transparent, topmost window that never takes
/// focus on its own. It is always the size of the tallest panel and never
/// changes size; only the drawing inside animates, so the unfold is smooth
/// and no geometry is recomputed mid-animation. Transparent pixels are
/// transparent to clicks too — the window is layered — so the space around
/// the visible panel belongs to whatever is underneath.
/// </summary>
internal sealed class PanelWindow : Window, IPanelHost
{
    static readonly double WindowWidth = Theme.BodyWidth + 2 * Theme.OpenShoulder + 2 * Theme.ShadowPad;
    static readonly double WindowHeight = Theme.BodyHeight(tall: true) + Theme.ShadowPad;

    readonly AppModel model;
    readonly Action closeRequest;
    readonly Path shape;
    readonly DropShadowEffect shadow;
    readonly Grid body;
    readonly Grid paneHost;
    readonly TextBlock headerTitle, headerTrailing;
    readonly StackPanel leftRail, rightRail;
    readonly Grid railsLeftHost, railsRightHost;
    readonly Dictionary<Tab, Pane> panes = new();
    readonly ShelfPane shelfPane;
    IntPtr hwnd;
    IntPtr previousForeground;
    bool keyboardClaimed;

    double width = Theme.StripWidth, height = Theme.StripHeight;
    double targetWidth = Theme.StripWidth, targetHeight = Theme.StripHeight;
    bool animating;
    TimeSpan lastFrame;
    Action? onCollapsed;

    public bool IsOpen { get; private set; }
    public bool IsDragOver { get; private set; }

    public PanelWindow(AppModel model, Action closeRequest, Func<bool> trayVisible, Action<bool> setTrayVisible, Action quit)
    {
        this.model = model;
        this.closeRequest = closeRequest;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Width = WindowWidth;
        Height = WindowHeight;
        Title = "Cyclop";
        WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        shadow = new DropShadowEffect { BlurRadius = 28, ShadowDepth = 8, Direction = 270, Opacity = 0, Color = Colors.Black };
        shape = new Path { Fill = Theme.Black, Effect = shadow, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };

        // Header — no controls in it: this strip sits over the top edge, where
        // a click is most often a near-miss on something else.
        headerTitle = Ui.Caps("");
        headerTitle.Margin = new Thickness(16, 0, 0, 0);
        headerTrailing = Ui.Label("", 10, Theme.Tertiary, FontWeights.Medium);
        headerTrailing.Margin = new Thickness(0, 0, 16, 0);
        headerTrailing.HorizontalAlignment = HorizontalAlignment.Right;
        headerTrailing.MaxWidth = 260;
        var header = new Grid { Height = Theme.HeaderHeight };
        header.Children.Add(headerTitle);
        header.Children.Add(headerTrailing);

        leftRail = new StackPanel();
        rightRail = new StackPanel();
        railsLeftHost = new Grid { Height = Theme.StandardContentHeight, VerticalAlignment = VerticalAlignment.Top, Children = { leftRail } };
        railsRightHost = new Grid { Height = Theme.StandardContentHeight, VerticalAlignment = VerticalAlignment.Top, Children = { rightRail } };
        leftRail.VerticalAlignment = rightRail.VerticalAlignment = VerticalAlignment.Center;

        paneHost = new Grid();
        var content = new Grid { Margin = new Thickness(14, 0, 14, Theme.BottomPadding) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.RailWidth) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        content.ColumnDefinitions.Add(new ColumnDefinition());
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Theme.RailWidth) });
        Grid.SetColumn(paneHost, 2);
        Grid.SetColumn(railsRightHost, 4);
        content.Children.Add(railsLeftHost);
        content.Children.Add(paneHost);
        content.Children.Add(railsRightHost);

        body = new Grid
        {
            Width = Theme.BodyWidth,
            Height = Theme.BodyHeight(model.Tab.WantsTallBody()),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.Transparent,
            Opacity = 0,
            AllowDrop = true,
        };
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(Theme.HeaderHeight) });
        body.RowDefinitions.Add(new RowDefinition());
        Grid.SetRow(content, 1);
        body.Children.Add(header);
        body.Children.Add(content);

        Content = new Grid { Children = { shape, body } };

        // Panes, built once and kept.
        shelfPane = new ShelfPane(model, this);
        panes[Tab.Media] = new MediaPane(model, this);
        panes[Tab.Shelf] = shelfPane;
        panes[Tab.Clipboard] = new ClipboardPane(model, this);
        panes[Tab.Snippets] = new SnippetsPane(model, this);
        panes[Tab.Calendar] = new CalendarPane(model, this);
        panes[Tab.Translate] = new TranslatePane(model, this);
        panes[Tab.Currency] = new CurrencyPane(model, this);
        panes[Tab.Notes] = new NotesPane(model, this);
        panes[Tab.Teleprompter] = new TeleprompterPane(model, this);
        panes[Tab.Utilities] = new UtilitiesPane(model, this);
        panes[Tab.Settings] = new SettingsPane(model, this, trayVisible, setTrayVisible, quit);
        foreach (var (tab, pane) in panes)
        {
            pane.Visibility = Visibility.Collapsed;
            pane.HeaderChanged += () => { if (model.Tab == tab) headerTrailing.Text = pane.HeaderText ?? ""; };
            paneHost.Children.Add(pane);
        }

        model.TabChanged += OnTabChanged;
        model.RailChanged += BuildRails;
        BuildRails();
        ShowTab(model.Tab, animate: false);

        body.DragEnter += OnDragEnter;
        body.DragOver += (_, e) => { e.Effects = Accepts(e) ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; };
        body.DragLeave += (_, _) => { IsDragOver = false; shelfPane.SetTargeted(false); };
        body.Drop += OnDrop;
        PreviewMouseDown += OnPreviewMouseDown;
        PreviewKeyDown += OnPreviewKeyDown;
        Deactivated += (_, _) => KeyboardLost();
        Redraw();
    }

    // MARK: - Window plumbing

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        hwnd = new WindowInteropHelper(this).Handle;
        // Tool window: no taskbar button, no Alt+Tab entry. No-activate:
        // hovering and clicking leave the window underneath in front.
        Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
    }

    public void EnsureHandle() => hwnd = new WindowInteropHelper(this).EnsureHandle();

    /// <summary>Top-centre of the display, in that display's pixels. Set twice:
    /// moving onto a display of another scale makes WPF resize the window to
    /// its own idea of the rect, and the second call puts it where it belongs.</summary>
    void PlaceOn(Monitor monitor)
    {
        int w = (int)Math.Round(WindowWidth * monitor.Scale);
        int h = (int)Math.Round(WindowHeight * monitor.Scale);
        int x = monitor.CenterX - w / 2;
        int y = monitor.Bounds.Top;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
    }

    /// <summary>The visible panel in screen pixels, shoulders included, with a
    /// few pixels of slack — what the pointer has to stay inside of.</summary>
    public Native.RECT HitRect()
    {
        Native.GetWindowRect(hwnd, out var window);
        double scale = window.Width / WindowWidth;
        // Open, the target size counts, not the frame being drawn: the
        // interactive area grows first, so the pointer that asked for the
        // panel never falls outside it while it is still unfolding.
        double w = IsOpen ? targetWidth : width;
        double h = IsOpen ? targetHeight : height;
        double shoulder = IsOpen ? Theme.OpenShoulder : Shoulder();
        double outer = w + 2 * shoulder;
        double left = (WindowWidth - outer) / 2;
        int slack = (int)Math.Round(4 * scale);
        return new Native.RECT
        {
            Left = window.Left + (int)Math.Floor(left * scale) - slack,
            Top = window.Top,
            Right = window.Left + (int)Math.Ceiling((left + outer) * scale) + slack,
            Bottom = window.Top + (int)Math.Ceiling(h * scale) + slack,
        };
    }

    // MARK: - Open / close

    public void Open(Monitor monitor)
    {
        onCollapsed = null;
        // Moved while still hidden, so it never flashes where Windows would
        // have put a new window; placed again once shown, at its final scale.
        PlaceOn(monitor);
        if (!IsVisible) Show();
        PlaceOn(monitor);
        IsOpen = true;
        targetWidth = Theme.BodyWidth;
        targetHeight = Theme.BodyHeight(model.Tab.WantsTallBody());
        body.Height = targetHeight;
        panes[model.Tab].Show();
        headerTrailing.Text = panes[model.Tab].HeaderText ?? "";
        StartAnimation();
        if (model.Tab.NeedsKeyboard() && Native.IsDown(Native.VK_LBUTTON) == false)
        {
            // Arriving and typing is one move: the typing tab takes the keyboard.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { if (IsOpen) panes[model.Tab].FocusField(); });
        }
    }

    public void Collapse(Action? collapsed)
    {
        if (!IsOpen) return;
        IsOpen = false;
        IsDragOver = false;
        ReleaseKeyboard(restoreFocus: true);
        targetWidth = Theme.StripWidth;
        targetHeight = Theme.StripHeight;
        onCollapsed = () =>
        {
            Hide();
            collapsed?.Invoke();
        };
        foreach (var pane in panes.Values) pane.OnPanelClosed();
        StartAnimation();
    }

    void StartAnimation()
    {
        if (animating) return;
        animating = true;
        lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    /// <summary>A critically damped approach rather than a fixed-length
    /// tween: a change of target mid-flight — hover out while unfolding —
    /// simply bends the motion instead of restarting it.</summary>
    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        double dt = lastFrame == TimeSpan.Zero ? 1.0 / 60 : Math.Clamp((now - lastFrame).TotalSeconds, 0, 0.05);
        lastFrame = now;
        double k = 1 - Math.Exp(-dt * (IsOpen ? 19 : 24));
        width += (targetWidth - width) * k;
        height += (targetHeight - height) * k;
        bool done = Math.Abs(targetWidth - width) < 0.4 && Math.Abs(targetHeight - height) < 0.4;
        if (done)
        {
            width = targetWidth;
            height = targetHeight;
        }
        Redraw();
        if (!done) return;
        CompositionTarget.Rendering -= OnFrame;
        animating = false;
        if (!IsOpen)
        {
            var collapsed = onCollapsed;
            onCollapsed = null;
            collapsed?.Invoke();
        }
    }

    /// <summary>0 folded, 1 fully open — what the shoulders, the corner radius,
    /// the shadow and the content fade are read from.</summary>
    double Progress()
    {
        double open = Theme.BodyHeight(false);
        return Math.Clamp((height - Theme.StripHeight) / (open - Theme.StripHeight), 0, 1);
    }

    double Shoulder() => Theme.OpenShoulder * Progress();

    void Redraw()
    {
        double p = Progress();
        double shoulder = Shoulder();
        double radius = Theme.StripRadius + (Theme.OpenBottomRadius - Theme.StripRadius) * p;
        shape.Data = NotchShape.Make(width + 2 * shoulder, height, shoulder, radius);
        shadow.Opacity = 0.55 * p;
        body.Clip = NotchShape.Make(width + 2 * shoulder, height, shoulder, radius, x0: (Theme.BodyWidth - width) / 2 - shoulder);
        body.Opacity = IsOpen ? Math.Clamp((p - 0.55) / 0.45, 0, 1) : Math.Clamp((p - 0.7) / 0.3, 0, 1);
        body.IsHitTestVisible = IsOpen;
    }

    // MARK: - Tabs

    void BuildRails()
    {
        railButtons.Clear();
        FillRail(leftRail, model.LeftRail.ToList());
        FillRail(rightRail, model.RightRail.ToList());
    }

    readonly Dictionary<Tab, Clickable> railButtons = new();

    /// <summary>Hovering switches tabs, but only after the pointer has stopped:
    /// one crossing the rail on its way elsewhere is gone in a few dozen
    /// milliseconds, one that came to choose stays put.</summary>
    void FillRail(StackPanel rail, List<Tab> tabs)
    {
        rail.Children.Clear();
        if (tabs.Count == 0) return;
        double iconHeight = Math.Min(32, (Theme.StandardContentHeight - Theme.RailSpacing * (tabs.Count - 1)) / tabs.Count);
        foreach (var tab in tabs)
        {
            var icon = Ui.Icon(Glyph.For(tab), 12, model.Tab == tab ? Theme.White : Theme.Tertiary);
            var button = new Clickable
            {
                Width = Theme.RailWidth,
                Height = iconHeight,
                CornerRadius = new CornerRadius(7),
                Normal = Theme.Clear,
                Hover = Theme.SurfaceHover,
                Child = icon,
                ToolTip = tab.Title(),
                Margin = new Thickness(0, 0, 0, Theme.RailSpacing),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = new ScaleTransform(1, 1),
            };
            button.IsLit = model.Tab == tab;
            button.Refresh();
            var dwell = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            dwell.Tick += (_, _) =>
            {
                dwell.Stop();
                if (button.IsMouseOver && IsOpen) Select(tab);
            };
            button.MouseEnter += (_, _) =>
            {
                var scale = (ScaleTransform)button.RenderTransform;
                scale.ScaleX = scale.ScaleY = 1.12;
                if (model.Tab != tab) dwell.Start();
            };
            button.MouseLeave += (_, _) =>
            {
                var scale = (ScaleTransform)button.RenderTransform;
                scale.ScaleX = scale.ScaleY = 1;
                dwell.Stop();
            };
            button.Click += () => Select(tab);
            railButtons[tab] = button;
            rail.Children.Add(button);
        }
    }

    public void Select(Tab tab)
    {
        if (!model.IsVisible(tab)) return;
        model.Tab = tab;
    }

    void OnTabChanged(Tab old, Tab now)
    {
        if (panes.TryGetValue(old, out var leaving)) leaving.Hide();
        ShowTab(now, animate: IsOpen);
        foreach (var (tab, button) in railButtons)
        {
            button.IsLit = tab == now;
            button.Refresh();
            if (button.Child is TextBlock icon) icon.Foreground = tab == now ? Theme.White : Theme.Tertiary;
        }
        if (IsOpen)
        {
            targetHeight = Theme.BodyHeight(now.WantsTallBody());
            if (targetHeight > body.Height) body.Height = targetHeight;
            else Dispatcher.BeginInvoke(DispatcherPriority.Background, () => { if (!animating) body.Height = targetHeight; });
            StartAnimation();
            // The typing tab takes the keyboard on arrival; leaving it gives
            // the keyboard straight back to the window underneath.
            if (now.NeedsKeyboard()) panes[now].FocusField();
            else ReleaseKeyboard(restoreFocus: true);
        }
        else body.Height = Theme.BodyHeight(now.WantsTallBody());
    }

    void ShowTab(Tab tab, bool animate)
    {
        headerTitle.Text = tab.Title().ToUpper(L10n.Culture);
        var pane = panes[tab];
        headerTrailing.Text = pane.HeaderText ?? "";
        pane.Show();
        if (animate) Ui.FadeIn(pane, 0.2);
    }

    /// <summary>For panes: folds the panel (not <see cref="Window.Close"/>).</summary>
    void IPanelHost.Close() => closeRequest();

    // MARK: - Keyboard

    /// <summary>Hands the panel the keyboard: the window may activate, and is
    /// brought to the front. The window that had it is remembered, and gets
    /// it back when the panel folds or the tab stops typing.</summary>
    public void ClaimKeyboard()
    {
        if (!IsOpen || hwnd == IntPtr.Zero) return;
        if (!keyboardClaimed)
        {
            var foreground = Native.GetForegroundWindow();
            previousForeground = foreground == hwnd ? IntPtr.Zero : foreground;
            Native.AddExStyle(hwnd, 0, remove: Native.WS_EX_NOACTIVATE);
            keyboardClaimed = true;
            model.IsTyping = true;
        }
        Activate();
        Native.SetForegroundWindow(hwnd);
    }

    void ReleaseKeyboard(bool restoreFocus)
    {
        if (!keyboardClaimed) return;
        keyboardClaimed = false;
        model.IsTyping = false;
        Keyboard.ClearFocus();
        Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE);
        if (restoreFocus && Native.GetForegroundWindow() == hwnd &&
            previousForeground != IntPtr.Zero && Native.IsWindow(previousForeground))
            Native.SetForegroundWindow(previousForeground);
        previousForeground = IntPtr.Zero;
    }

    /// <summary>A click into another app took the keyboard: the claim is
    /// dropped, what was typed stays, and the panel is free to fold.</summary>
    void KeyboardLost()
    {
        if (!keyboardClaimed) return;
        keyboardClaimed = false;
        model.IsTyping = false;
        Native.AddExStyle(hwnd, Native.WS_EX_NOACTIVATE);
        previousForeground = IntPtr.Zero;
    }

    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsOpen) return;
        bool onField = FindAncestor<TextBox>(e.OriginalSource as DependencyObject) is not null;
        if (onField) ClaimKeyboard();
        else if (model.ClickTakesKeyboard && e.ChangedButton == MouseButton.Left && !keyboardClaimed &&
                 FindAncestor<Clickable>(e.OriginalSource as DependencyObject) is null)
            panes[model.Tab].FocusField();
    }

    void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (panes[model.Tab].OnEscape()) return;
        closeRequest();
    }

    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // MARK: - Drop

    bool Accepts(DragEventArgs e) => model.IsVisible(Tab.Shelf) && e.Data.GetDataPresent(DataFormats.FileDrop);

    void OnDragEnter(object sender, DragEventArgs e)
    {
        if (!Accepts(e)) { e.Effects = DragDropEffects.None; e.Handled = true; return; }
        IsDragOver = true;
        Select(Tab.Shelf);
        shelfPane.SetTargeted(true);
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        IsDragOver = false;
        shelfPane.SetTargeted(false);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) model.Accept(paths);
        e.Handled = true;
    }
}
