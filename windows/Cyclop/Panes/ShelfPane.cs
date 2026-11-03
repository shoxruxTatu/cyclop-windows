using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Cyclop.App;
using Cyclop.Interop;
using Cyclop.Services;
using Cyclop.UI;

namespace Cyclop.Panes;

internal sealed class ShelfPane : Pane
{
    readonly ShelfStore shelf;
    readonly WrapPanel cards;
    readonly Border dropZone;
    readonly UIElement empty;
    readonly ScrollViewer scroller;
    readonly DockPanel footer;
    readonly TextBlock selectionLabel;
    readonly Clickable deselect;
    Point? pressPoint;
    ShelfItem? pressItem;
    bool targeted;

    public ShelfPane(AppModel model, IPanelHost host) : base(model, host)
    {
        shelf = model.Shelf;
        cards = new WrapPanel { Orientation = Orientation.Horizontal };
        scroller = Ui.Scroller(cards);

        selectionLabel = Ui.Label("", 10.5, Theme.Tertiary);
        deselect = Ui.PillButton(L10n.T("Deselect"), shelf.ClearSelection);
        var clear = Ui.PillButton(L10n.T("Clear"), shelf.Clear, Glyph.Delete);
        footer = new DockPanel { Margin = new Thickness(0, 6, 0, 0), LastChildFill = false };
        DockPanel.SetDock(clear, Dock.Right);
        DockPanel.SetDock(deselect, Dock.Right);
        deselect.Margin = new Thickness(0, 0, 6, 0);
        footer.Children.Add(selectionLabel);
        footer.Children.Add(clear);
        footer.Children.Add(deselect);

        var layout = new DockPanel();
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(footer);
        layout.Children.Add(scroller);

        empty = Ui.Empty(Glyph.Shelf, L10n.T("Drop files here"),
            L10n.T("They stay where they are — the shelf only holds a reference.\nWin+Shift+S screenshots land here on their own."));

        dropZone = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Theme.Clear,
            Padding = new Thickness(6),
            Child = new Grid { Children = { layout, empty } },
        };
        Children.Add(dropZone);

        shelf.Changed += Rebuild;
        Rebuild();
    }

    /// <summary>A file is being dragged over the panel.</summary>
    public void SetTargeted(bool on)
    {
        targeted = on;
        dropZone.BorderBrush = on ? Theme.Accent : Theme.Clear;
        dropZone.Background = on ? Theme.Surface : Theme.Clear;
    }

    void Rebuild()
    {
        HeaderText = shelf.Items.Count > 0 ? shelf.Items.Count.ToString() : null;
        bool has = shelf.Items.Count > 0;
        empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        footer.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        scroller.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        int selected = shelf.Selection.Count;
        selectionLabel.Text = selected > 0 ? L10n.T("Selected: %d", selected) : L10n.T("Ctrl-click to pick several; drag them out together");
        deselect.Visibility = selected > 0 ? Visibility.Visible : Visibility.Collapsed;

        cards.Children.Clear();
        foreach (var item in shelf.Items) cards.Children.Add(Card(item));
    }

    UIElement Card(ShelfItem item)
    {
        var image = new Image
        {
            Source = item.Icon,
            Width = item.IsPreview ? 72 : 40,
            Height = item.IsPreview ? 52 : 40,
            Stretch = item.IsPreview ? Stretch.UniformToFill : Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var picture = new Border
        {
            Width = 72,
            Height = 52,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Child = image,
        };
        if (item.IsPreview) picture.Clip = new RectangleGeometry(new Rect(0, 0, 72, 52), 6, 6);
        var name = Ui.Label(item.Name, 10, Theme.Secondary);
        name.TextAlignment = TextAlignment.Center;
        name.MaxWidth = 76;
        name.Margin = new Thickness(0, 5, 0, 0);
        name.HorizontalAlignment = HorizontalAlignment.Center;

        var stack = new StackPanel();
        stack.Children.Add(picture);
        stack.Children.Add(name);
        var card = new Clickable
        {
            Width = 86,
            Padding = new Thickness(5, 7, 5, 6),
            Margin = new Thickness(0, 0, 4, 4),
            CornerRadius = new CornerRadius(9),
            Normal = shelf.IsSelected(item) ? Theme.AccentSoft : Theme.Clear,
            Hover = shelf.IsSelected(item) ? Theme.AccentSoft : Theme.Surface,
            Child = stack,
            ToolTip = item.Path,
        };
        card.ContextMenu = Ui.Menu(
            (L10n.T("Copy"), () => shelf.Copy(item)),
            (L10n.T("Open"), () => ShelfStore.Open(item.Path)),
            (L10n.T("Show in Explorer"), () => ShelfStore.Reveal(item.Path)),
            ("-", () => { }),
            (L10n.T("Remove from Shelf"), () => shelf.Remove(item)));

        // Selection on click, drag on movement, open on double click. The
        // modifier keys are read from the hardware: the panel usually does not
        // hold the keyboard, so its own view of Ctrl and Shift is stale.
        bool skipClick = false;
        card.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                skipClick = true;
                ShelfStore.Open(item.Path);
                return;
            }
            pressPoint = e.GetPosition(this);
            pressItem = item;
        };
        card.Click += () =>
        {
            pressItem = null;
            if (skipClick) { skipClick = false; return; }
            bool additive = Native.IsDown(Native.VK_CONTROL) || Native.IsDown(Native.VK_SHIFT);
            shelf.Select(item, additive);
        };
        card.PreviewMouseMove += (_, e) =>
        {
            if (pressItem != item || pressPoint is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            var delta = e.GetPosition(this) - start;
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;
            pressItem = null;
            if (card.IsMouseCaptured) card.ReleaseMouseCapture();
            var data = new DataObject(DataFormats.FileDrop, shelf.DragPaths(item));
            try { DragDrop.DoDragDrop(card, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link); }
            catch { }
            card.Opacity = 1;
        };
        return card;
    }

    protected override void OnShown()
    {
        if (!targeted) SetTargeted(false);
    }
}

internal sealed class ClipboardPane : Pane
{
    readonly ClipboardHistory history;
    readonly StackPanel rows;
    readonly UIElement empty;
    readonly ScrollViewer scroller;

    public ClipboardPane(AppModel model, IPanelHost host) : base(model, host)
    {
        history = model.Clipboard;
        rows = new StackPanel();
        scroller = Ui.Scroller(rows);
        empty = Ui.Empty(Glyph.Clipboard, L10n.T("Nothing copied yet"),
            L10n.T("The last %d copies show up here. Click one to put it back.", ClipboardHistory.Limit));
        var clear = Ui.PillButton(L10n.T("Clear"), history.Clear, Glyph.Delete);
        clear.HorizontalAlignment = HorizontalAlignment.Right;
        clear.Margin = new Thickness(0, 6, 0, 0);
        var layout = new DockPanel();
        DockPanel.SetDock(clear, Dock.Bottom);
        layout.Children.Add(clear);
        layout.Children.Add(scroller);
        Children.Add(layout);
        Children.Add(empty);
        history.Changed += Rebuild;
        model.Privacy.Changed += Rebuild;
        Rebuild();
        void Rebuild()
        {
            HeaderText = history.Items.Count > 0 ? history.Items.Count.ToString() : null;
            bool has = history.Items.Count > 0;
            layout.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
            if (!IsShown) { rows.Children.Clear(); return; }
            rows.Children.Clear();
            foreach (var item in history.Items) rows.Children.Add(Row(item));
        }
        Shown = Rebuild;
    }

    Action? Shown;

    protected override void OnShown() => Shown?.Invoke();

    UIElement Row(ClipItem item)
    {
        var glyph = item.Kind == ClipKind.File ? Glyph.Document : item.IsLink ? Glyph.Link : Glyph.Text;
        var icon = Ui.Icon(glyph, 11, Theme.Secondary);
        icon.Width = 18;
        var text = Ui.Label(item.Preview.Replace('\n', ' ').Replace('\r', ' '), 11.5, Theme.White);
        text.Margin = new Thickness(6, 0, 6, 0);
        var remove = Ui.IconButton(Glyph.Close, () => history.Remove(item), 20, 9, tip: L10n.T("Delete"));
        remove.Opacity = 0;

        var line = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(remove, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(remove);
        line.Children.Add(text);

        bool hidden = Model.Privacy.Hides(PrivacySection.Clipboard, "clip." + item.Id);
        var row = new Clickable
        {
            Height = 28,
            Padding = new Thickness(6, 0, 4, 0),
            CornerRadius = new CornerRadius(7),
            Hover = Theme.Surface,
            Child = Cover.Wrap(line, hidden, () => Model.Privacy.Toggle("clip." + item.Id)),
            ToolTip = hidden ? null : item.Kind == ClipKind.File ? item.Value : null,
        };
        row.MouseEnter += (_, _) => remove.Opacity = 1;
        row.MouseLeave += (_, _) => remove.Opacity = 0;
        row.Click += () => { if (!hidden) Model.ClipboardWatcher.Copy(item); };
        return row;
    }
}
