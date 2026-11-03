using System;
using System.Collections.Generic;
using File = System.IO.File;
using MemoryStream = System.IO.MemoryStream;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.Interop;
using Cyclop.Services;
using Cyclop.UI;

namespace Cyclop.Panel;

/// <summary>
/// The screenshot tool. The screen is captured first and then shown frozen
/// under a dimmed overlay, one window per display, so what is selected is
/// exactly what was on screen at the press — a tooltip or a video frame does
/// not move on while the area is being drawn.
///
/// Drag with the left button: the area goes straight to the clipboard.
/// Drag with the right button: the area opens in the editor.
/// Either way the picture reaches the clipboard, and from there the shelf,
/// the same road a Win+Shift+S snip takes.
/// </summary>
internal sealed class ScreenshotTool
{
    readonly AppModel model;
    readonly PanelController controller;
    readonly List<SelectionWindow> windows = new();
    bool active;

    public bool IsActive => active;

    public ScreenshotTool(AppModel model, PanelController controller)
    {
        this.model = model;
        this.controller = controller;
    }

    public async void StartArea()
    {
        Support.Log($"screenshot: area requested (active={active})");
        if (active || model.KeyboardLock.IsLocked) return;
        active = true;
        try
        {
            await controller.HideForCaptureAsync();
            var monitors = PanelController.Enumerate();
            var shot = Capture(monitors);
            Support.Log($"screenshot: captured {shot.Image.PixelWidth}×{shot.Image.PixelHeight} on {monitors.Count} display(s)");
            var cursor = Native.Cursor();
            foreach (var monitor in monitors)
            {
                var image = Crop(shot.Image, shot.Origin, monitor.Bounds);
                var window = new SelectionWindow(this, monitor, image);
                windows.Add(window);
                window.Present(monitor.Bounds.Contains(cursor));
            }
        }
        catch (Exception e)
        {
            Support.Log("area screenshot failed: " + e);
            Finish();
        }
    }

    public async void TakeScreen()
    {
        Support.Log($"screenshot: screen requested (active={active})");
        if (active || model.KeyboardLock.IsLocked) return;
        active = true;
        try
        {
            await controller.HideForCaptureAsync();
            var cursor = Native.Cursor();
            var monitors = PanelController.Enumerate();
            var monitor = monitors.FirstOrDefault(m => m.Bounds.Contains(cursor)) ?? monitors.First();
            var shot = Capture(new List<Monitor> { monitor });
            ToClipboard(shot.Image);
            Flash(monitor);
        }
        catch (Exception e) { Support.Log("screen screenshot failed: " + e); }
        Finish();
    }

    /// <summary>Another display's overlay started a selection: this one's goes.</summary>
    internal void SelectionStarted(SelectionWindow owner)
    {
        foreach (var w in windows.Where(w => w != owner)) w.ClearSelection();
    }

    internal void Finish()
    {
        foreach (var w in windows.ToList()) w.Close();
        windows.Clear();
        active = false;
        controller.RestoreAfterCapture();
    }

    /// <summary>A file saved from the editor goes on the shelf too.</summary>
    internal void Saved(string path) => model.Shelf.Add(new[] { path });

    // MARK: - Pixels

    /// <summary>The union of the displays, in physical pixels — the process
    /// is per-monitor aware, so nothing here is scaled.</summary>
    static (BitmapSource Image, System.Drawing.Point Origin) Capture(List<Monitor> monitors)
    {
        int left = monitors.Min(m => m.Bounds.Left), top = monitors.Min(m => m.Bounds.Top);
        int right = monitors.Max(m => m.Bounds.Right), bottom = monitors.Max(m => m.Bounds.Bottom);
        using var bitmap = new System.Drawing.Bitmap(right - left, bottom - top, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
            g.CopyFromScreen(left, top, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
        var handle = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(handle, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return (source, new System.Drawing.Point(left, top));
        }
        finally { DeleteObject(handle); }
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);

    static BitmapSource Crop(BitmapSource image, System.Drawing.Point origin, Native.RECT bounds)
    {
        var rect = new Int32Rect(bounds.Left - origin.X, bounds.Top - origin.Y, bounds.Width, bounds.Height);
        var crop = new CroppedBitmap(image, rect);
        crop.Freeze();
        return crop;
    }

    /// <summary>Bitmap for every app, PNG for the ones that keep transparency
    /// and colour exact (Telegram, Office, browsers).</summary>
    internal static void ToClipboard(BitmapSource image)
    {
        Support.Log($"screenshot: {image.PixelWidth}×{image.PixelHeight} to the clipboard");
        var data = new DataObject();
        data.SetImage(image);
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);
        png.Position = 0;
        data.SetData("PNG", png);
        ClipboardWatcher.SetData(data);
    }

    /// <summary>A brief white flash over the display: the shot was taken.</summary>
    static void Flash(Monitor monitor)
    {
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.White,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Opacity = 0.35,
        };
        var hwnd = new WindowInteropHelper(window).EnsureHandle();
        Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT);
        var b = monitor.Bounds;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_NOACTIVATE);
        window.Show();
        var fade = new DoubleAnimation(0.35, 0, TimeSpan.FromSeconds(0.3));
        fade.Completed += (_, _) => window.Close();
        window.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}

/// <summary>One display's overlay: the frozen screen, the selection, and —
/// after a right-button selection — the editor.</summary>
internal sealed class SelectionWindow : Window
{
    enum Tool { Pen, Line, Arrow, Rectangle, Marker, Text }

    static readonly Color[] Palette =
    {
        Color.FromRgb(0xFF, 0x3B, 0x30), Color.FromRgb(0xFF, 0x95, 0x00), Color.FromRgb(0xFF, 0xD6, 0x0A),
        Color.FromRgb(0x34, 0xC7, 0x59), Color.FromRgb(0x0A, 0x84, 0xFF), Color.FromRgb(0xAF, 0x52, 0xDE),
        Colors.White, Colors.Black,
    };

    readonly ScreenshotTool tool;
    readonly Monitor monitor;
    readonly BitmapSource image;
    readonly IntPtr hwnd;
    readonly Grid root;
    readonly Path dim;
    readonly Canvas guides;
    readonly Rectangle guideAcrossShade, guideAcross, guideDownShade, guideDown;
    readonly Rectangle frame;
    readonly Border sizeLabel;
    readonly TextBlock sizeText, hint;
    readonly Canvas ink;
    readonly StackPanel tools, actions, palette;
    readonly Border toolsBox, actionsBox, paletteBox;
    readonly Border colorSwatch;
    readonly Dictionary<Tool, Clickable> toolButtons = new();

    Point? anchor;
    MouseButton selectingWith;
    Rect selection = Rect.Empty;
    bool editing;
    Tool current = Tool.Pen;
    Color color = Palette[0];
    Point drawStart;
    UIElement? drawing;
    TextBox? typing;

    public SelectionWindow(ScreenshotTool tool, Monitor monitor, BitmapSource image)
    {
        this.tool = tool;
        this.monitor = monitor;
        this.image = image;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;

        var picture = new Image { Source = image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(picture, BitmapScalingMode.NearestNeighbor);
        dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), IsHitTestVisible = false };
        // Lines across the display through the pointer: the system cross is a
        // few thin pixels that get lost on a dimmed high-resolution screen.
        // White on a dark edge, so they show over light and dark alike.
        var shade = new SolidColorBrush(Color.FromArgb(0x90, 0, 0, 0));
        var line = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
        guideAcrossShade = new Rectangle { Fill = shade, Height = 3 };
        guideAcross = new Rectangle { Fill = line, Height = 1 };
        guideDownShade = new Rectangle { Fill = shade, Width = 3 };
        guideDown = new Rectangle { Fill = line, Width = 1 };
        guides = new Canvas
        {
            IsHitTestVisible = false, Visibility = Visibility.Collapsed,
            Children = { guideAcrossShade, guideDownShade, guideAcross, guideDown },
        };
        frame = new Rectangle
        {
            Stroke = Brushes.White, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        sizeText = Ui.Label("", 11, Theme.White, FontWeights.Medium);
        sizeLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
            CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            Child = sizeText, Visibility = Visibility.Collapsed, IsHitTestVisible = false,
        };
        hint = Ui.Label(L10n.T("Left button — copy the area · right button — edit it · Esc — cancel"), 12, Theme.White, FontWeights.Medium);
        var hintBox = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)), CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 6, 12, 6), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 40, 0, 0), Child = hint, IsHitTestVisible = false,
        };
        ink = new Canvas
        {
            Background = Brushes.Transparent, ClipToBounds = true, Visibility = Visibility.Collapsed,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };

        tools = new StackPanel();
        actions = new StackPanel { Orientation = Orientation.Horizontal };
        palette = new StackPanel();
        colorSwatch = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1) };
        toolsBox = Toolbar(tools);
        actionsBox = Toolbar(actions);
        paletteBox = Toolbar(palette);
        paletteBox.Visibility = Visibility.Collapsed;
        BuildToolbars();

        root = new Grid { Children = { picture, dim, guides, frame, ink, sizeLabel, hintBox, toolsBox, actionsBox, paletteBox } };
        Content = root;

        hwnd = new WindowInteropHelper(this).EnsureHandle();
        Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW);
        var b = monitor.Bounds;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, Native.SWP_NOACTIVATE);

        root.MouseDown += OnDown;
        root.MouseMove += OnMove;
        root.MouseUp += OnUp;
        root.MouseLeave += (_, _) => { if (anchor is null) guides.Visibility = Visibility.Collapsed; };
        PreviewKeyDown += OnKey;
        Loaded += (_, _) =>
        {
            UpdateDim();
            if (root.IsMouseOver) PlaceGuides(Mouse.GetPosition(root));
        };
    }

    public void Present(bool focus)
    {
        Show();
        var b = monitor.Bounds;
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, b.Left, b.Top, b.Width, b.Height, 0);
        if (focus)
        {
            Activate();
            Native.SetForegroundWindow(hwnd);
        }
    }

    /// <summary>Image pixels per layout unit, measured rather than asked: a
    /// window moved onto a display of another scale while hidden can still
    /// believe the old one, but the frozen image always fills it exactly.</summary>
    double Scale => root.ActualWidth > 0 ? image.PixelWidth / root.ActualWidth : VisualTreeHelper.GetDpi(this).DpiScaleX;

    static Border Toolbar(System.Windows.Controls.Panel content) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1E, 0x1E, 0x1E)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(3),
        Child = content,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Visibility = Visibility.Collapsed,
        Cursor = Cursors.Arrow,
    };

    Clickable ToolbarButton(UIElement child, Action click, string tip)
    {
        var button = new Clickable
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(6),
            Normal = Theme.Clear, Hover = Theme.SurfaceHover, Child = child, ToolTip = tip,
        };
        button.Click += click;
        return button;
    }

    void BuildToolbars()
    {
        void AddTool(Tool t, UIElement icon, string tip)
        {
            var b = ToolbarButton(icon, () => Select(t), tip);
            toolButtons[t] = b;
            tools.Children.Add(b);
        }
        AddTool(Tool.Pen, Ui.Icon(Glyph.Edit, 13), L10n.T("Pen"));
        var line = Ui.Icon("", 13);
        line.RenderTransform = new RotateTransform(-45, 6.5, 8);
        AddTool(Tool.Line, line, L10n.T("Line"));
        var arrow = Ui.Icon("", 13);
        arrow.RenderTransform = new RotateTransform(-45, 6.5, 8);
        AddTool(Tool.Arrow, arrow, L10n.T("Arrow"));
        AddTool(Tool.Rectangle, Ui.Icon("", 13), L10n.T("Rectangle"));
        AddTool(Tool.Marker, Ui.Icon("", 13), L10n.T("Marker"));
        AddTool(Tool.Text, Ui.Label("T", 15, Theme.White, FontWeights.SemiBold), L10n.T("Text"));
        ((TextBlock)toolButtons[Tool.Text].Child).HorizontalAlignment = HorizontalAlignment.Center;
        colorSwatch.Background = new SolidColorBrush(color);
        tools.Children.Add(ToolbarButton(colorSwatch, () =>
            paletteBox.Visibility = paletteBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible, L10n.T("Colour")));
        tools.Children.Add(ToolbarButton(Ui.Icon("", 13), Undo, L10n.T("Undo") + " (Ctrl+Z)"));

        foreach (var c in Palette)
        {
            var swatch = new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) };
            var picked = c;
            palette.Children.Add(ToolbarButton(swatch, () =>
            {
                color = picked;
                colorSwatch.Background = new SolidColorBrush(picked);
                paletteBox.Visibility = Visibility.Collapsed;
                if (typing is not null) typing.Foreground = new SolidColorBrush(picked);
            }, ""));
        }

        actions.Children.Add(ToolbarButton(Ui.Icon(Glyph.Copy, 13), CopyAndClose, L10n.T("Copy") + " (Ctrl+C)"));
        actions.Children.Add(ToolbarButton(Ui.Icon("", 13), Save, L10n.T("Save") + " (Ctrl+S)"));
        actions.Children.Add(ToolbarButton(Ui.Icon(Glyph.Close, 13), () => tool.Finish(), L10n.T("Close") + " (Esc)"));
        Select(Tool.Pen);
    }

    void Select(Tool t)
    {
        CommitText();
        current = t;
        foreach (var (kind, button) in toolButtons)
        {
            button.Normal = kind == t ? Theme.Accent : Theme.Clear;
            button.Hover = kind == t ? Theme.Accent : Theme.SurfaceHover;
            button.Refresh();
        }
        ink.Cursor = t == Tool.Text ? Cursors.IBeam : Cursors.Cross;
    }

    // MARK: - Selecting

    public void ClearSelection()
    {
        if (editing) return;
        selection = Rect.Empty;
        anchor = null;
        UpdateDim();
    }

    void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (editing)
        {
            if (e.ChangedButton == MouseButton.Left && ink.IsMouseOver) BeginStroke(e);
            return;
        }
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Right)) return;
        tool.SelectionStarted(this);
        anchor = e.GetPosition(root);
        selectingWith = e.ChangedButton;
        selection = new Rect(anchor.Value, anchor.Value);
        root.CaptureMouse();
        UpdateDim();
        e.Handled = true;
    }

    void OnMove(object sender, MouseEventArgs e)
    {
        if (editing)
        {
            if (drawing is not null) ContinueStroke(e.GetPosition(ink));
            return;
        }
        var point = e.GetPosition(root);
        PlaceGuides(point);
        if (anchor is not { } start) return;
        selection = new Rect(start, point);
        UpdateDim();
    }

    void PlaceGuides(Point p)
    {
        double width = root.ActualWidth, height = root.ActualHeight;
        guideAcrossShade.Width = guideAcross.Width = width;
        guideDownShade.Height = guideDown.Height = height;
        Canvas.SetTop(guideAcrossShade, p.Y - 1);
        Canvas.SetTop(guideAcross, p.Y);
        Canvas.SetLeft(guideDownShade, p.X - 1);
        Canvas.SetLeft(guideDown, p.X);
        guides.Visibility = Visibility.Visible;
    }

    void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (editing)
        {
            if (drawing is not null) { drawing = null; ink.ReleaseMouseCapture(); }
            return;
        }
        if (anchor is null || e.ChangedButton != selectingWith) return;
        anchor = null;
        root.ReleaseMouseCapture();
        var px = PixelRect();
        // A click without a drag selects nothing; the overlay stays.
        if (px.Width < 4 || px.Height < 4)
        {
            selection = Rect.Empty;
            UpdateDim();
            return;
        }
        if (selectingWith == MouseButton.Left)
        {
            ScreenshotTool.ToClipboard(Cropped());
            tool.Finish();
        }
        else BeginEditing();
        e.Handled = true;
    }

    void UpdateDim()
    {
        var full = new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        if (selection.IsEmpty || selection.Width < 1 || selection.Height < 1)
        {
            dim.Data = full;
            frame.Visibility = sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        dim.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(selection));
        frame.Visibility = Visibility.Visible;
        frame.Margin = new Thickness(selection.X, selection.Y, 0, 0);
        frame.Width = selection.Width;
        frame.Height = selection.Height;
        var px = PixelRect();
        sizeText.Text = $"{px.Width}×{px.Height}";
        sizeLabel.Visibility = Visibility.Visible;
        double labelTop = selection.Y >= 26 ? selection.Y - 24 : selection.Y + 4;
        sizeLabel.Margin = new Thickness(selection.X, labelTop, 0, 0);
    }

    /// <summary>The selection in the captured image's pixels.</summary>
    Int32Rect PixelRect()
    {
        double s = Scale;
        int x = (int)Math.Round(selection.X * s), y = (int)Math.Round(selection.Y * s);
        int w = (int)Math.Round(selection.Width * s), h = (int)Math.Round(selection.Height * s);
        x = Math.Clamp(x, 0, image.PixelWidth);
        y = Math.Clamp(y, 0, image.PixelHeight);
        w = Math.Clamp(w, 0, image.PixelWidth - x);
        h = Math.Clamp(h, 0, image.PixelHeight - y);
        return new Int32Rect(x, y, w, h);
    }

    BitmapSource Cropped()
    {
        var crop = new CroppedBitmap(image, PixelRect());
        crop.Freeze();
        return crop;
    }

    // MARK: - Editing

    void BeginEditing()
    {
        editing = true;
        guides.Visibility = Visibility.Collapsed;
        Cursor = Cursors.Arrow;
        ink.Visibility = Visibility.Visible;
        ink.Margin = new Thickness(selection.X, selection.Y, 0, 0);
        ink.Width = selection.Width;
        ink.Height = selection.Height;
        ink.Cursor = Cursors.Cross;
        PlaceToolbars();
        Activate();
        Native.SetForegroundWindow(hwnd);
    }

    /// <summary>Tools to the right of the area, actions under it — inside the
    /// area instead wherever the display edge leaves no room.</summary>
    void PlaceToolbars()
    {
        toolsBox.Visibility = actionsBox.Visibility = Visibility.Visible;
        toolsBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        actionsBox.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var t = toolsBox.DesiredSize;
        var a = actionsBox.DesiredSize;
        double width = root.ActualWidth, height = root.ActualHeight;

        double tx = selection.Right + 6;
        if (tx + t.Width > width) tx = selection.X - t.Width - 6;
        if (tx < 0) tx = selection.Right - t.Width - 6;
        double ty = Math.Clamp(selection.Y, 0, Math.Max(0, height - t.Height));
        toolsBox.Margin = new Thickness(tx, ty, 0, 0);
        paletteBox.Margin = new Thickness(tx < selection.X ? tx - 42 : tx + t.Width + 4, ty, 0, 0);

        double ax = Math.Clamp(selection.Right - a.Width, 0, Math.Max(0, width - a.Width));
        double ay = selection.Bottom + 6;
        if (ay + a.Height > height) ay = selection.Y - a.Height - 6;
        if (ay < 0) ay = selection.Bottom - a.Height - 6;
        actionsBox.Margin = new Thickness(ax, ay, 0, 0);
    }

    Brush Stroke(bool marker = false) =>
        new SolidColorBrush(marker ? Color.FromArgb(0x70, color.R, color.G, color.B) : color);

    void BeginStroke(MouseButtonEventArgs e)
    {
        paletteBox.Visibility = Visibility.Collapsed;
        var p = e.GetPosition(ink);
        if (current == Tool.Text)
        {
            CommitText();
            StartText(p);
            e.Handled = true;
            return;
        }
        CommitText();
        drawStart = p;
        drawing = current switch
        {
            Tool.Pen => new Polyline { Stroke = Stroke(), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = { p } },
            Tool.Marker => new Polyline { Stroke = Stroke(marker: true), StrokeThickness = 16, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = { p } },
            Tool.Line => new Line { Stroke = Stroke(), StrokeThickness = 3, X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
            Tool.Arrow => new Path { Stroke = Stroke(), StrokeThickness = 3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round },
            _ => new Rectangle { Stroke = Stroke(), StrokeThickness = 3 },
        };
        ink.Children.Add(drawing);
        ink.CaptureMouse();
        ContinueStroke(p);
        e.Handled = true;
    }

    void ContinueStroke(Point p)
    {
        switch (drawing)
        {
            case Polyline line: line.Points.Add(p); break;
            case Line line: line.X2 = p.X; line.Y2 = p.Y; break;
            case Path path: path.Data = Arrow(drawStart, p); break;
            case Rectangle rect:
                var r = new Rect(drawStart, p);
                Canvas.SetLeft(rect, r.X);
                Canvas.SetTop(rect, r.Y);
                rect.Width = r.Width;
                rect.Height = r.Height;
                break;
        }
    }

    static Geometry Arrow(Point from, Point to)
    {
        var group = new GeometryGroup();
        group.Children.Add(new LineGeometry(from, to));
        var v = to - from;
        if (v.Length > 1)
        {
            v.Normalize();
            double head = 14;
            var back = to - v * head;
            var side = new Vector(-v.Y, v.X) * (head * 0.55);
            group.Children.Add(new LineGeometry(to, back + side));
            group.Children.Add(new LineGeometry(to, back - side));
        }
        return group;
    }

    void StartText(Point p)
    {
        typing = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(color),
            CaretBrush = new SolidColorBrush(color),
            FontFamily = Theme.Font,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            AcceptsReturn = true,
            MinWidth = 40,
        };
        Canvas.SetLeft(typing, p.X);
        Canvas.SetTop(typing, p.Y - 12);
        ink.Children.Add(typing);
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => typing?.Focus());
    }

    /// <summary>The field becomes plain text: what is rendered is what was typed.</summary>
    void CommitText()
    {
        if (typing is null) return;
        var box = typing;
        typing = null;
        int index = ink.Children.IndexOf(box);
        ink.Children.Remove(box);
        if (string.IsNullOrWhiteSpace(box.Text) || index < 0) return;
        var text = new TextBlock
        {
            Text = box.Text,
            Foreground = box.Foreground,
            FontFamily = box.FontFamily,
            FontSize = box.FontSize,
            FontWeight = box.FontWeight,
            Padding = new Thickness(2, 1, 2, 1),
        };
        Canvas.SetLeft(text, Canvas.GetLeft(box));
        Canvas.SetTop(text, Canvas.GetTop(box));
        ink.Children.Insert(index, text);
    }

    void Undo()
    {
        if (typing is not null)
        {
            ink.Children.Remove(typing);
            typing = null;
            return;
        }
        if (ink.Children.Count > 0) ink.Children.RemoveAt(ink.Children.Count - 1);
    }

    /// <summary>The area with the drawing on it, at the display's own pixels.</summary>
    BitmapSource Render()
    {
        CommitText();
        var crop = Cropped();
        if (ink.Children.Count == 0) return crop;
        double s = Scale;
        var size = new Size(selection.Width, selection.Height);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(crop, new Rect(size));
            var brush = new VisualBrush(ink)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(size),
            };
            dc.DrawRectangle(brush, null, new Rect(size));
        }
        var bitmap = new RenderTargetBitmap(crop.PixelWidth, crop.PixelHeight, 96 * s, 96 * s, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    void CopyAndClose()
    {
        ScreenshotTool.ToClipboard(Render());
        tool.Finish();
    }

    void Save()
    {
        var result = Render();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "PNG|*.png|JPEG|*.jpg",
            FileName = $"{L10n.T("Screenshot")} {DateTime.Now.ToString(L10n.T("yyyy-MM-dd 'at' HH.mm.ss"), System.Globalization.CultureInfo.InvariantCulture)}",
            InitialDirectory = ScreenshotVault.Folder,
            DefaultExt = ".png",
        };
        Topmost = false;
        bool ok = dialog.ShowDialog(this) == true;
        Topmost = true;
        if (!ok) return;
        try
        {
            BitmapEncoder encoder = dialog.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                ? new JpegBitmapEncoder { QualityLevel = 92 }
                : new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(result));
            using (var file = File.Create(dialog.FileName)) encoder.Save(file);
            tool.Saved(dialog.FileName);
            tool.Finish();
        }
        catch (Exception e)
        {
            Support.Log("saving screenshot failed: " + e.Message);
            MessageBox.Show(this, e.Message, "Cyclop", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (typing is not null) { CommitText(); e.Handled = true; return; }
            tool.Finish();
            e.Handled = true;
            return;
        }
        if (!editing || typing is not null) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (ctrl && e.Key == Key.C) { CopyAndClose(); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { Save(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (e.Key == Key.Enter) { CopyAndClose(); e.Handled = true; }
    }
}
