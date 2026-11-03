using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.UI;

namespace Cyclop.Panes;

/// <summary>
/// A script scrolling at the top of the screen — right under the webcam on
/// almost every laptop, so the eyes stay near the lens instead of travelling
/// to a window somewhere below it.
/// </summary>
internal sealed class TeleprompterPane : Pane
{
    readonly TeleprompterStore store;
    readonly Grid reader, editorView;
    readonly Field editor;
    readonly Canvas viewport;
    readonly TextBlock text;
    readonly TranslateTransform scroll = new();
    readonly Clickable playPause;
    readonly TextBlock speedLabel;
    readonly SliderBar speed, font;
    bool editing;
    TimeSpan lastFrame;

    public TeleprompterPane(AppModel model, IPanelHost host) : base(model, host)
    {
        store = model.Teleprompter;

        // Editor
        editor = new Field(L10n.T("Paste the script here"), multiline: true, size: 13);
        var ready = Ui.PillButton(L10n.T("Ready"), () => SetEditing(false), accent: true);
        ready.HorizontalAlignment = HorizontalAlignment.Right;
        ready.Margin = new Thickness(0, 8, 0, 0);
        var editorLayout = new DockPanel();
        DockPanel.SetDock(ready, Dock.Bottom);
        editorLayout.Children.Add(ready);
        editorLayout.Children.Add(editor);
        editorView = new Grid { Children = { editorLayout } };
        editor.Box.TextChanged += (_, _) => store.Script = editor.Text;

        // Reader
        text = new TextBlock
        {
            FontFamily = Theme.Font,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.White,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            RenderTransform = scroll,
            LineHeight = double.NaN,
        };
        viewport = new Canvas { ClipToBounds = true, Background = Theme.Clear };
        viewport.Children.Add(text);
        viewport.SizeChanged += (_, _) => Measure();
        // Lines fade out at both edges, so the one being read is the one in the clear.
        viewport.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Colors.Transparent, 0),
                new GradientStop(Colors.Black, 0.14),
                new GradientStop(Colors.Black, 0.86),
                new GradientStop(Colors.Transparent, 1),
            },
        };
        viewport.MouseWheel += (_, e) =>
        {
            store.Offset = Math.Clamp(store.Offset - e.Delta / 3.0, 0, store.MaxOffset);
            scroll.Y = -store.Offset + store.ViewportHeight / 3;
        };

        var rewind = Ui.IconButton(Glyph.Rewind, store.Rewind, 28, 12, tip: L10n.T("Back to the start"));
        playPause = Ui.IconButton(Glyph.Play, store.Toggle, 34, 14, prominent: true);
        var edit = Ui.IconButton(Glyph.Edit, () => SetEditing(true), 28, 11, tip: L10n.T("Edit"));
        speed = new SliderBar(0.3, 3.0, store.Speed, 0.1) { Width = 110 };
        speed.ShowsThumb = true;
        speed.Changing += v => { store.Speed = v; speedLabel!.Text = $"{v:0.0}×"; };
        speedLabel = Ui.Label($"{store.Speed:0.0}×", 10.5, Theme.Secondary, FontWeights.Medium);
        speedLabel.Width = 34;
        font = new SliderBar(18, 64, store.FontSize, 1) { Width = 90 };
        font.ShowsThumb = true;
        font.Changing += v => { store.FontSize = v; ApplyFont(); };

        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        controls.Children.Add(rewind);
        playPause.Margin = new Thickness(8, 0, 14, 0);
        controls.Children.Add(playPause);
        controls.Children.Add(Ui.Icon(Glyph.Timer, 11, Theme.Tertiary));
        speed.Margin = new Thickness(6, 0, 4, 0);
        controls.Children.Add(speed);
        controls.Children.Add(speedLabel);
        var aa = Ui.Label("Aa", 11, Theme.Tertiary, FontWeights.SemiBold);
        aa.Margin = new Thickness(10, 0, 6, 0);
        controls.Children.Add(aa);
        controls.Children.Add(font);
        edit.Margin = new Thickness(14, 0, 0, 0);
        controls.Children.Add(edit);

        var readerLayout = new DockPanel();
        DockPanel.SetDock(controls, Dock.Bottom);
        readerLayout.Children.Add(controls);
        readerLayout.Children.Add(viewport);
        reader = new Grid { Children = { readerLayout } };

        Children.Add(reader);
        Children.Add(editorView);
        store.Changed += Update;
        editor.Text = store.Script;
        SetEditing(store.Script.Length == 0);
    }

    void SetEditing(bool on)
    {
        if (!on && store.Script.Trim().Length == 0) return;
        editing = on;
        editorView.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        reader.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            store.Suspend();
            FocusField();
        }
        else
        {
            store.Rewind();
            text.Text = store.Script;
            ApplyFont();
        }
    }

    void ApplyFont()
    {
        text.FontSize = store.FontSize;
        Measure();
    }

    void Measure()
    {
        double width = Math.Max(0, viewport.ActualWidth - 24);
        text.Width = width;
        Canvas.SetLeft(text, 12);
        text.Measure(new Size(width, double.PositiveInfinity));
        store.ContentHeight = text.DesiredSize.Height;
        store.ViewportHeight = viewport.ActualHeight;
        scroll.Y = -store.Offset + store.ViewportHeight / 3;
    }

    void Update()
    {
        ((TextBlock)playPause.Child).Text = store.IsRunning ? Glyph.Pause : Glyph.Play;
        scroll.Y = -store.Offset + store.ViewportHeight / 3;
        if (store.IsRunning) StartClock(); else StopClock();
    }

    bool clockRunning;

    /// <summary>Every frame, not a step per line: text that moves in jumps is
    /// read in lurches, and the eye loses its line at every one.</summary>
    void StartClock()
    {
        if (clockRunning) return;
        clockRunning = true;
        lastFrame = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
    }

    void StopClock()
    {
        if (!clockRunning) return;
        clockRunning = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (lastFrame != TimeSpan.Zero)
        {
            var dt = (now - lastFrame).TotalSeconds;
            if (dt > 0 && dt < 0.25) store.Tick(dt);
        }
        lastFrame = now;
        scroll.Y = -store.Offset + store.ViewportHeight / 3;
    }

    public override void FocusField()
    {
        if (!editing) return;
        Host.ClaimKeyboard();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => editor.Box.Focus());
    }

    public override bool OnEscape()
    {
        if (store.IsRunning) { store.Pause(); return true; }
        return false;
    }

    protected override void OnShown()
    {
        // A take starts at the top, which is where coming back puts it.
        if (!editing) { store.Rewind(); Measure(); }
    }

    protected override void OnHidden() => store.Suspend();
}

/// <summary>The one-shot tools: screenshots and the keyboard lock.</summary>
internal sealed class UtilitiesPane : Pane
{
    readonly TextBlock areaCaption, screenCaption, lockTitle, lockCaption;

    public UtilitiesPane(AppModel model, IPanelHost host) : base(model, host)
    {
        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        grid.Children.Add(Card(Glyph.Crop, L10n.T("Screenshot of an Area"), out areaCaption, () => model.Screenshots?.StartArea()));
        grid.Children.Add(Card(Glyph.Monitor, L10n.T("Screenshot of the Screen"), out screenCaption, () => model.Screenshots?.TakeScreen()));
        grid.Children.Add(Card(Glyph.Keyboard, L10n.T("Lock Keyboard"), out lockCaption, () =>
        {
            // The panel folds out of the way; the cover goes up on every screen.
            host.Close();
            Dispatcher.BeginInvoke(DispatcherPriority.Background, model.KeyboardLock.Lock);
        }));
        lockTitle = (TextBlock)((StackPanel)((Clickable)grid.Children[2]).Child).Children[1];
        Children.Add(grid);
        model.KeyboardLock.Changed += Update;
        model.Config.Changed += Update;
        Update();
    }

    static Clickable Card(string glyph, string title, out TextBlock caption, Action click)
    {
        var heading = Ui.Label(title, 12.5, Theme.White, FontWeights.Medium);
        heading.HorizontalAlignment = HorizontalAlignment.Center;
        heading.Margin = new Thickness(0, 8, 0, 0);
        caption = Ui.Label("", 10, Theme.Tertiary);
        caption.TextAlignment = TextAlignment.Center;
        caption.TextWrapping = TextWrapping.Wrap;
        caption.TextTrimming = TextTrimming.None;
        caption.HorizontalAlignment = HorizontalAlignment.Center;
        caption.Margin = new Thickness(8, 4, 8, 0);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(Ui.Icon(glyph, 24, Theme.White));
        stack.Children.Add(heading);
        stack.Children.Add(caption);
        var card = new Clickable
        {
            CornerRadius = new CornerRadius(14),
            BorderBrush = Theme.Hairline,
            BorderThickness = new Thickness(1),
            Normal = Theme.Surface,
            Hover = Theme.SurfaceHover,
            Margin = new Thickness(4, 0, 4, 0),
            Child = stack,
        };
        card.Click += click;
        return card;
    }

    static string Shortcut(string hotkey) =>
        string.IsNullOrWhiteSpace(hotkey) ? "" : "\n" + hotkey;

    void Update()
    {
        areaCaption.Text = L10n.T("Left button copies the area, right button opens the editor") + Shortcut(Model.Config.HotkeyArea);
        screenCaption.Text = L10n.T("The whole display, straight to the clipboard and the shelf") + Shortcut(Model.Config.HotkeyScreen);
        var failure = Model.KeyboardLock.Failure;
        lockTitle.Text = failure ?? L10n.T("Lock Keyboard");
        lockCaption.Text = failure is not null
            ? L10n.T("Windows refused the input hook. Press again in a moment.")
            : L10n.T("Hold Esc or − for %d s to unlock; it unlocks by itself after a minute. Ctrl+Alt+Del always gets through.",
                (int)Services.KeyboardLock.HoldToRelease.TotalSeconds);
    }
}
