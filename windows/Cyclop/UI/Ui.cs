using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Cyclop.UI;

/// <summary>
/// Builders for the few kinds of element every pane is made of. The UI is
/// assembled in code rather than XAML on purpose: everything here is checked
/// by the compiler, and the panel is small enough that a markup layer would
/// add more ceremony than it removes.
/// </summary>
internal static class Ui
{
    /// <summary>Menus and pickers that are open right now. The panel stays
    /// open while any is, or the pointer travelling onto a menu item outside
    /// the panel would fold the panel away under it.</summary>
    public static int OpenPopups;

    public static TextBlock Label(string text, double size = 12, Brush? color = null, FontWeight? weight = null)
    {
        return new TextBlock
        {
            Text = text,
            FontFamily = Theme.Font,
            FontSize = size,
            Foreground = color ?? Theme.White,
            FontWeight = weight ?? FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    public static TextBlock Caps(string text) =>
        new()
        {
            Text = text.ToUpper(L10n.Culture),
            FontFamily = Theme.Font,
            FontSize = 9.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Theme.Tertiary,
            VerticalAlignment = VerticalAlignment.Center,
        };

    public static TextBlock Icon(string glyph, double size = 12, Brush? color = null) =>
        new()
        {
            Text = glyph,
            FontFamily = Theme.Icons,
            FontSize = size,
            Foreground = color ?? Theme.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

    /// <summary>A round icon button.</summary>
    public static Clickable IconButton(string glyph, Action click, double size = 26, double iconSize = 12,
        bool prominent = false, string? tip = null)
    {
        var button = new Clickable
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Normal = prominent ? Theme.SurfaceHover : Theme.Clear,
            Hover = prominent ? Theme.Hairline : Theme.Surface,
            Child = Icon(glyph, iconSize),
            ToolTip = tip,
        };
        if (prominent) button.Hover = Theme.SurfaceHover;
        button.Click += click;
        return button;
    }

    /// <summary>A capsule with text, for the few labelled actions.</summary>
    public static Clickable PillButton(string text, Action click, string? glyph = null, bool accent = false)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (glyph is not null)
        {
            var icon = Icon(glyph, 11);
            icon.Margin = new Thickness(0, 0, 6, 0);
            row.Children.Add(icon);
        }
        row.Children.Add(Label(text, 11.5, Theme.White, FontWeights.Medium));
        var button = new Clickable
        {
            Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(12),
            Normal = accent ? Theme.AccentSoft : Theme.Surface,
            Hover = accent ? Theme.Accent : Theme.SurfaceHover,
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.Click += click;
        return button;
    }

    public static Border Card(UIElement child, double radius = 10, Thickness? padding = null) =>
        new()
        {
            Background = Theme.Surface,
            CornerRadius = new CornerRadius(radius),
            Padding = padding ?? new Thickness(4),
            Child = child,
        };

    /// <summary>A scroller without visible bars: the wheel scrolls, nothing
    /// light-grey is drawn across a black panel.</summary>
    public static ScrollViewer Scroller(UIElement content) =>
        new()
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
            PanningMode = PanningMode.VerticalOnly,
        };

    /// <summary>The state shown when a pane has nothing: an icon, a line, an
    /// optional explanation and an optional action.</summary>
    public static StackPanel Empty(string glyph, string title, string? caption = null, UIElement? action = null)
    {
        var stack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(Icon(glyph, 22, Theme.Tertiary));
        var t = Label(title, 12, Theme.Secondary, FontWeights.Medium);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        t.Margin = new Thickness(0, 8, 0, 0);
        stack.Children.Add(t);
        if (caption is not null)
        {
            var c = Label(caption, 10.5, Theme.Tertiary);
            c.TextAlignment = TextAlignment.Center;
            c.TextWrapping = TextWrapping.Wrap;
            c.TextTrimming = TextTrimming.None;
            c.HorizontalAlignment = HorizontalAlignment.Center;
            c.MaxWidth = 420;
            c.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(c);
        }
        if (action is FrameworkElement fe)
        {
            fe.Margin = new Thickness(0, 10, 0, 0);
            stack.Children.Add(fe);
        }
        return stack;
    }

    public static void FadeIn(UIElement element, double seconds = 0.18)
    {
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromSeconds(seconds)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    /// <summary>A context menu that keeps the panel open while it is up.</summary>
    public static ContextMenu Menu(params (string Title, Action Action)[] items)
    {
        var menu = new ContextMenu();
        foreach (var (title, action) in items)
        {
            if (title == "-") { menu.Items.Add(new Separator()); continue; }
            var item = new MenuItem { Header = title };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => OpenPopups++;
        menu.Closed += (_, _) => OpenPopups = Math.Max(0, OpenPopups - 1);
        return menu;
    }

    public static void Copy(string text)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch (System.Runtime.InteropServices.COMException) { System.Threading.Thread.Sleep(30); }
        }
    }
}

/// <summary>
/// The panel's one kind of button: a border that lights up under the pointer
/// and clicks on release. Never focusable — the panel almost never holds the
/// keyboard, and a focus ring on a hover panel is noise.
/// </summary>
internal class Clickable : Border
{
    Brush normal = Theme.Clear;
    Brush hover = Theme.Surface;
    bool pressed;

    public event Action? Click;

    public Clickable()
    {
        Background = normal;
        Cursor = Cursors.Hand;
        Focusable = false;
        SnapsToDevicePixels = true;
        IsEnabledChanged += (_, _) => Opacity = IsEnabled ? 1 : 0.35;
    }

    public Brush Normal { get => normal; set { normal = value; if (!IsMouseOver) Background = value; } }
    public Brush Hover { get => hover; set { hover = value; if (IsMouseOver) Background = value; } }
    /// <summary>When set, the button stays lit — the selected tab, the picked card.</summary>
    public bool IsLit { get; set; }

    public void Refresh() => Background = IsLit ? hover : IsMouseOver ? hover : normal;

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        base.OnMouseEnter(e);
        Background = hover;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        Refresh();
        Opacity = IsEnabled ? 1 : 0.35;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        pressed = true;
        Opacity = 0.6;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool wasPressed = pressed;
        pressed = false;
        Opacity = IsEnabled ? 1 : 0.35;
        ReleaseMouseCapture();
        if (!wasPressed) return;
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X <= ActualWidth && p.Y <= ActualHeight)
        {
            e.Handled = true;
            Click?.Invoke();
        }
    }
}

/// <summary>Drawn rather than a system toggle, which renders light-on-light.</summary>
internal sealed class ToggleSwitch : Border
{
    readonly Border knob;
    bool isOn;

    public event Action<bool>? Toggled;

    public ToggleSwitch(bool on)
    {
        Width = 30;
        Height = 17;
        CornerRadius = new CornerRadius(8.5);
        Cursor = Cursors.Hand;
        knob = new Border
        {
            Width = 13,
            Height = 13,
            CornerRadius = new CornerRadius(6.5),
            Background = Theme.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            RenderTransform = new TranslateTransform(),
            Margin = new Thickness(2, 0, 0, 0),
        };
        Child = knob;
        IsOn = on;
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            IsOn = !IsOn;
            Toggled?.Invoke(IsOn);
        };
    }

    public bool IsOn
    {
        get => isOn;
        set
        {
            isOn = value;
            Background = value ? Theme.Accent : Theme.SurfaceHover;
            ((TranslateTransform)knob.RenderTransform).BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(value ? 13 : 0, TimeSpan.FromSeconds(0.15)));
        }
    }
}

/// <summary>
/// A thin slider the panel draws itself: the scrubber under a track, the
/// teleprompter's speed, the open delay. <see cref="Changing"/> follows the
/// drag; <see cref="Committed"/> fires once, on release.
/// </summary>
internal sealed class SliderBar : Grid
{
    readonly Border track, fill, thumb;
    readonly double min, max, step;
    double value;
    bool dragging;

    public event Action<double>? Changing;
    public event Action<double>? Committed;

    public SliderBar(double min, double max, double value, double step = 0)
    {
        this.min = min;
        this.max = max;
        this.step = step;
        Height = 16;
        Background = Theme.Clear;
        Cursor = Cursors.Hand;
        track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Surface, VerticalAlignment = VerticalAlignment.Center };
        fill = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Theme.Secondary, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
        thumb = new Border
        {
            Width = 11, Height = 11, CornerRadius = new CornerRadius(5.5), Background = Theme.White,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0,
        };
        Children.Add(track);
        Children.Add(fill);
        Children.Add(thumb);
        SizeChanged += (_, _) => Layout();
        MouseEnter += (_, _) => { thumb.Opacity = 1; track.Height = fill.Height = 6; };
        MouseLeave += (_, _) => { if (!dragging) { thumb.Opacity = 0; track.Height = fill.Height = 4; } };
        MouseLeftButtonDown += (_, e) =>
        {
            dragging = true;
            CaptureMouse();
            Drag(e.GetPosition(this).X);
            e.Handled = true;
        };
        MouseMove += (_, e) => { if (dragging) Drag(e.GetPosition(this).X); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (!dragging) return;
            dragging = false;
            ReleaseMouseCapture();
            Drag(e.GetPosition(this).X);
            Committed?.Invoke(this.value);
            if (!IsMouseOver) { thumb.Opacity = 0; track.Height = fill.Height = 4; }
            e.Handled = true;
        };
        Value = value;
    }

    public bool IsDragging => dragging;
    /// <summary>Always showing the knob — for sliders that are settings.</summary>
    public bool ShowsThumb { set { if (value) { thumb.Opacity = 1; MouseLeave += (_, _) => thumb.Opacity = 1; } } }

    public double Value
    {
        get => value;
        set
        {
            this.value = Math.Clamp(value, min, max);
            Layout();
        }
    }

    void Drag(double x)
    {
        double width = Math.Max(1, ActualWidth);
        double v = min + Math.Clamp(x / width, 0, 1) * (max - min);
        if (step > 0) v = Math.Round((v - min) / step) * step + min;
        Value = v;
        Changing?.Invoke(this.value);
    }

    void Layout()
    {
        double width = ActualWidth;
        if (width <= 0 || max <= min) return;
        double f = (value - min) / (max - min);
        fill.Width = Math.Max(0, width * f);
        thumb.Margin = new Thickness(Math.Clamp(width * f - 5.5, 0, Math.Max(0, width - 11)), 0, 0, 0);
    }
}

/// <summary>A text field on the panel's dark surface, with a placeholder.</summary>
internal sealed class Field : Grid
{
    public TextBox Box { get; }
    readonly TextBlock placeholder;

    public Field(string placeholderText, bool multiline = false, double size = 12)
    {
        Box = new TextBox
        {
            FontFamily = Theme.Font,
            FontSize = size,
            Foreground = Theme.White,
            CaretBrush = Theme.White,
            SelectionBrush = Theme.Accent,
            Background = Theme.Surface,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6, 5, 6, 5),
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        placeholder = new TextBlock
        {
            Text = placeholderText,
            FontFamily = Theme.Font,
            FontSize = size,
            Foreground = Theme.Tertiary,
            IsHitTestVisible = false,
            Margin = new Thickness(9, 5, 6, 5),
            VerticalAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center,
        };
        Children.Add(new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true, Child = Box, Background = Theme.Surface });
        Children.Add(placeholder);
        Box.TextChanged += (_, _) => placeholder.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public string Text
    {
        get => Box.Text;
        set { if (Box.Text != value) Box.Text = value; }
    }
}

/// <summary>
/// The privacy cover: a field of dots over a row, with the eye that uncovers
/// that one row until the panel folds.
/// </summary>
internal static class Cover
{
    static readonly Brush Dots = MakeDots();

    static Brush MakeDots()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(Theme.Clear, null, new RectangleGeometry(new Rect(0, 0, 7, 7))));
        group.Children.Add(new GeometryDrawing(Theme.Tertiary, null, new EllipseGeometry(new Point(2, 2), 1.1, 1.1)));
        group.Children.Add(new GeometryDrawing(Theme.Surface, null, new EllipseGeometry(new Point(5.5, 5.5), 1.0, 1.0)));
        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 7, 7),
            ViewportUnits = BrushMappingMode.Absolute,
        };
        brush.Freeze();
        return brush;
    }

    /// <summary>Wraps <paramref name="content"/>: covered, the dots show
    /// instead of it and a click on the eye uncovers.</summary>
    public static UIElement Wrap(UIElement content, bool hidden, Action toggle, double radius = 7)
    {
        if (!hidden) return content;
        var grid = new Grid();
        content.Visibility = Visibility.Hidden;
        grid.Children.Add(content);
        var dots = new Border { Background = Dots, CornerRadius = new CornerRadius(radius) };
        grid.Children.Add(dots);
        var eye = Ui.IconButton(Glyph.Eye, toggle, 22, 11, tip: L10n.T("Show"));
        eye.HorizontalAlignment = HorizontalAlignment.Right;
        eye.VerticalAlignment = VerticalAlignment.Center;
        eye.Margin = new Thickness(0, 0, 4, 0);
        grid.Children.Add(eye);
        return grid;
    }
}

/// <summary>The panel outline: flat top flush with the screen edge, concave
/// shoulders where it meets it, rounded bottom.</summary>
internal static class NotchShape
{
    /// <summary><paramref name="x0"/> shifts the outline right, for clipping
    /// content laid out wider than the shape currently is.</summary>
    public static Geometry Make(double width, double height, double shoulder, double bottomRadius, double x0 = 0)
    {
        double w = Math.Max(width, 2 * shoulder + 1);
        double h = Math.Max(height, 1);
        double r = Math.Min(bottomRadius, Math.Min(h - shoulder, (w - 2 * shoulder) / 2));
        r = Math.Max(0, r);
        var figure = new PathFigure { StartPoint = new Point(0, 0), IsClosed = true, IsFilled = true };
        // Left shoulder curves from the screen edge into the body.
        figure.Segments.Add(new QuadraticBezierSegment(new Point(shoulder, 0), new Point(shoulder, shoulder), true));
        figure.Segments.Add(new LineSegment(new Point(shoulder, h - r), true));
        figure.Segments.Add(new ArcSegment(new Point(shoulder + r, h), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new LineSegment(new Point(w - shoulder - r, h), true));
        figure.Segments.Add(new ArcSegment(new Point(w - shoulder, h - r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true));
        figure.Segments.Add(new LineSegment(new Point(w - shoulder, shoulder), true));
        figure.Segments.Add(new QuadraticBezierSegment(new Point(w - shoulder, 0), new Point(w, 0), true));
        var geometry = new PathGeometry(new[] { figure });
        if (x0 != 0) geometry.Transform = new TranslateTransform(x0, 0);
        geometry.Freeze();
        return geometry;
    }
}
