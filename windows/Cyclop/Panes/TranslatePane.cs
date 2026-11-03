using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.UI;

namespace Cyclop.Panes;

/// <summary>English ↔ Russian in two columns: type on the left, read on the
/// right. The direction follows the script of what was typed.</summary>
internal sealed class TranslatePane : Pane
{
    readonly Translator translator;
    readonly Field input;
    readonly TextBox output;
    readonly TextBlock sourceTitle, targetTitle, status;
    readonly Clickable retry, copy, clear;

    public TranslatePane(AppModel model, IPanelHost host) : base(model, host)
    {
        translator = model.Translator;
        ColumnDefinitions.Add(new ColumnDefinition());
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        ColumnDefinitions.Add(new ColumnDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        sourceTitle = Ui.Caps("");
        targetTitle = Ui.Caps("");
        clear = Ui.IconButton(Glyph.Close, () => { translator.Reset(); input!.Text = ""; FocusField(); }, 20, 9, tip: L10n.T("Clear"));
        copy = Ui.IconButton(Glyph.Copy, () => Ui.Copy(translator.Output), 20, 10, tip: L10n.T("Copy"));
        Grid.SetColumn(targetTitle, 2);

        var sourceHead = new DockPanel { Height = 22 };
        DockPanel.SetDock(clear, Dock.Right);
        sourceHead.Children.Add(clear);
        sourceHead.Children.Add(sourceTitle);
        var targetHead = new DockPanel { Height = 22 };
        DockPanel.SetDock(copy, Dock.Right);
        targetHead.Children.Add(copy);
        targetHead.Children.Add(targetTitle);
        Grid.SetColumn(targetHead, 2);
        Children.Add(sourceHead);
        Children.Add(targetHead);

        input = new Field(L10n.T("Type or paste text"), multiline: true, size: 13);
        input.Box.TextChanged += (_, _) => translator.SetInput(input.Text);
        Grid.SetRow(input, 1);
        Children.Add(input);

        output = new TextBox
        {
            IsReadOnly = true,
            FontFamily = Theme.Font,
            FontSize = 13,
            Foreground = Theme.White,
            Background = Theme.Clear,
            BorderThickness = new Thickness(0),
            TextWrapping = TextWrapping.Wrap,
            Padding = new Thickness(6, 5, 6, 5),
            SelectionBrush = Theme.Accent,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        };
        var outputCard = new Border { CornerRadius = new CornerRadius(7), BorderBrush = Theme.Hairline, BorderThickness = new Thickness(1), Child = output };
        Grid.SetRow(outputCard, 1);
        Grid.SetColumn(outputCard, 2);
        Children.Add(outputCard);

        status = Ui.Label("", 10, Theme.Tertiary);
        retry = Ui.PillButton(L10n.T("Retry"), translator.Retry, Glyph.Refresh);
        retry.Margin = new Thickness(8, 0, 0, 0);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        footer.Children.Add(status);
        footer.Children.Add(retry);
        Grid.SetRow(footer, 2);
        Grid.SetColumnSpan(footer, 3);
        Children.Add(footer);

        translator.Changed += Update;
        Update();
    }

    void Update()
    {
        var route = translator.CurrentRoute;
        sourceTitle.Text = Translator.Name(route.Source).ToUpper(L10n.Culture);
        targetTitle.Text = Translator.Name(route.Target).ToUpper(L10n.Culture);
        if (output.Text != translator.Output) output.Text = translator.Output;
        copy.Visibility = translator.Output.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        clear.Visibility = translator.Input.Length > 0 ? Visibility.Visible : Visibility.Hidden;
        retry.Visibility = translator.Failure is null ? Visibility.Collapsed : Visibility.Visible;
        if (translator.Failure is { } failure)
        {
            status.Text = failure;
            status.Foreground = Theme.Warning;
        }
        else
        {
            // Said where it is relevant: what is typed here leaves the PC.
            status.Text = translator.IsBusy ? L10n.T("Translating…") : L10n.T("Online, via Google Translate — the text is sent to translate it");
            status.Foreground = Theme.Tertiary;
        }
    }

    public override void FocusField()
    {
        Host.ClaimKeyboard();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            input.Box.Focus();
            input.Box.CaretIndex = input.Box.Text.Length;
        });
    }

    public override bool OnEscape()
    {
        if (input.Text.Length == 0) return false;
        translator.Reset();
        input.Text = "";
        return true;
    }
}
