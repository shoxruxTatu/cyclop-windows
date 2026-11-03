using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.UI;

namespace Cyclop.Panes;

/// <summary>Two amounts, either one typed into; the other follows.</summary>
internal sealed class CurrencyPane : Pane
{
    readonly CurrencyStore store;
    readonly Field sourceAmount, targetAmount;
    readonly Clickable sourceButton, targetButton;
    readonly TextBlock sourceName, targetName, rateLine;
    readonly Grid picker;
    readonly Field pickerSearch;
    readonly StackPanel pickerRows;
    readonly Grid converter;
    bool pickingSource;
    bool writing;

    public CurrencyPane(AppModel model, IPanelHost host) : base(model, host)
    {
        store = model.Currencies;

        sourceAmount = Amount();
        targetAmount = Amount();
        sourceAmount.Box.TextChanged += (_, _) => { if (!writing) store.SetSourceText(sourceAmount.Text); };
        targetAmount.Box.TextChanged += (_, _) => { if (!writing) store.SetTargetText(targetAmount.Text); };
        sourceName = Ui.Label("", 10.5, Theme.Tertiary);
        targetName = Ui.Label("", 10.5, Theme.Tertiary);
        sourceButton = CodeButton(() => OpenPicker(source: true));
        targetButton = CodeButton(() => OpenPicker(source: false));

        var swap = Ui.IconButton(Glyph.Swap, store.Swap, 30, 12, tip: L10n.T("Swap currencies"));
        swap.HorizontalAlignment = HorizontalAlignment.Center;
        swap.Margin = new Thickness(0, 4, 0, 4);

        rateLine = Ui.Label("", 10.5, Theme.Tertiary);
        rateLine.HorizontalAlignment = HorizontalAlignment.Center;
        rateLine.Margin = new Thickness(0, 10, 0, 0);

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MaxWidth = 460 };
        stack.Children.Add(Line(sourceAmount, sourceButton, sourceName));
        stack.Children.Add(swap);
        stack.Children.Add(Line(targetAmount, targetButton, targetName));
        stack.Children.Add(rateLine);
        converter = new Grid { Children = { stack } };

        // The picker covers the pane rather than opening a popup: a popup is a
        // window of its own, and the pointer travelling onto it would read as
        // leaving the panel.
        pickerSearch = new Field(L10n.T("Search currency"));
        pickerSearch.Box.TextChanged += (_, _) => FillPicker();
        pickerSearch.Box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                var first = store.Filtered(pickerSearch.Text).FirstOrDefault();
                if (first is not null) Choose(first.Code);
                e.Handled = true;
            }
        };
        pickerRows = new StackPanel();
        var pickerLayout = new DockPanel();
        DockPanel.SetDock(pickerSearch, Dock.Top);
        pickerLayout.Children.Add(pickerSearch);
        var scroller = Ui.Scroller(pickerRows);
        scroller.Margin = new Thickness(0, 6, 0, 0);
        pickerLayout.Children.Add(scroller);
        picker = new Grid { Visibility = Visibility.Collapsed, Background = Theme.Black, Children = { pickerLayout } };

        Children.Add(converter);
        Children.Add(picker);
        store.Changed += Update;
        Update();
    }

    static Field Amount()
    {
        var field = new Field("0", size: 20);
        field.Box.Padding = new Thickness(8, 4, 8, 4);
        field.Height = 40;
        return field;
    }

    static Clickable CodeButton(Action click)
    {
        var button = new Clickable
        {
            Width = 86,
            Height = 40,
            Margin = new Thickness(8, 0, 0, 0),
            CornerRadius = new CornerRadius(8),
            Normal = Theme.Surface,
            Hover = Theme.SurfaceHover,
            Child = Ui.Label("", 14, Theme.White, FontWeights.SemiBold),
        };
        ((TextBlock)button.Child).HorizontalAlignment = HorizontalAlignment.Center;
        button.Click += click;
        return button;
    }

    static UIElement Line(Field amount, Clickable code, TextBlock name)
    {
        var row = new DockPanel();
        DockPanel.SetDock(code, Dock.Right);
        row.Children.Add(code);
        row.Children.Add(amount);
        name.Margin = new Thickness(4, 2, 0, 0);
        var stack = new StackPanel();
        stack.Children.Add(row);
        stack.Children.Add(name);
        return stack;
    }

    void Update()
    {
        HeaderText = store.RateDate;
        ((TextBlock)sourceButton.Child).Text = store.SourceCode.ToUpperInvariant();
        ((TextBlock)targetButton.Child).Text = store.TargetCode.ToUpperInvariant();
        sourceName.Text = store.Name(store.SourceCode);
        targetName.Text = store.Name(store.TargetCode);
        writing = true;
        // Only the side not being typed into is rewritten — fighting the caret
        // on the other one is how a typed "1." turns into "1".
        if (!sourceAmount.Box.IsKeyboardFocused) sourceAmount.Text = store.SourceText;
        if (!targetAmount.Box.IsKeyboardFocused) targetAmount.Text = store.TargetText;
        writing = false;
        if (store.Failure is { } failure && store.Rate is null)
        {
            rateLine.Text = failure;
            rateLine.Foreground = Theme.Warning;
        }
        else if (store.Rate is { } rate)
        {
            rateLine.Text = $"1 {store.SourceCode.ToUpperInvariant()} = {CurrencyStore.Format(rate)} {store.TargetCode.ToUpperInvariant()}";
            rateLine.Foreground = Theme.Tertiary;
        }
        else
        {
            rateLine.Text = store.IsLoading ? L10n.T("Loading…") : "";
            rateLine.Foreground = Theme.Tertiary;
        }
        if (picker.Visibility == Visibility.Visible) FillPicker();
    }

    void OpenPicker(bool source)
    {
        pickingSource = source;
        pickerSearch.Text = "";
        FillPicker();
        converter.Visibility = Visibility.Hidden;
        picker.Visibility = Visibility.Visible;
        Host.ClaimKeyboard();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => pickerSearch.Box.Focus());
    }

    void ClosePicker()
    {
        picker.Visibility = Visibility.Collapsed;
        converter.Visibility = Visibility.Visible;
        FocusField();
    }

    void Choose(string code)
    {
        if (pickingSource)
        {
            if (code == store.TargetCode) store.Swap(); else store.SourceCode = code;
        }
        else
        {
            if (code == store.SourceCode) store.Swap(); else store.TargetCode = code;
        }
        ClosePicker();
    }

    void FillPicker()
    {
        pickerRows.Children.Clear();
        var current = pickingSource ? store.SourceCode : store.TargetCode;
        var matches = store.Filtered(pickerSearch.Text).Take(120).ToList();
        if (matches.Count == 0)
        {
            var none = Ui.Label(store.Currencies.Count == 0 ? L10n.T("Loading…") : L10n.T("No currencies match"), 11, Theme.Tertiary);
            none.Margin = new Thickness(6, 8, 0, 0);
            pickerRows.Children.Add(none);
            return;
        }
        foreach (var currency in matches)
        {
            var code = Ui.Label(currency.DisplayCode, 11.5, Theme.White, FontWeights.SemiBold);
            code.Width = 56;
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            line.Children.Add(code);
            line.Children.Add(Ui.Label(currency.Name, 11.5, Theme.Secondary));
            var row = new Clickable
            {
                Height = 26,
                Padding = new Thickness(8, 0, 8, 0),
                CornerRadius = new CornerRadius(6),
                Normal = currency.Code == current ? Theme.SurfaceHover : Theme.Clear,
                Hover = Theme.Surface,
                Child = line,
            };
            var picked = currency.Code;
            row.Click += () => Choose(picked);
            pickerRows.Children.Add(row);
        }
    }

    public override void FocusField()
    {
        Host.ClaimKeyboard();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (picker.Visibility == Visibility.Visible) { pickerSearch.Box.Focus(); return; }
            sourceAmount.Box.Focus();
            sourceAmount.Box.SelectAll();
        });
    }

    public override bool OnEscape()
    {
        if (picker.Visibility != Visibility.Visible) return false;
        ClosePicker();
        return true;
    }

    public override void OnPanelClosed()
    {
        if (picker.Visibility == Visibility.Visible)
        {
            picker.Visibility = Visibility.Collapsed;
            converter.Visibility = Visibility.Visible;
        }
    }
}
