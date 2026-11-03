using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.UI;

namespace Cyclop.Panes;

/// <summary>
/// A hand-kept list: click a row to copy it. Deliberately not fed by the
/// clipboard — what belongs here is decided by hand, in the panel or in
/// <c>snippets.json</c>, whichever is closer.
/// </summary>
internal sealed class SnippetsPane : Pane
{
    readonly SnippetStore store;
    readonly Field search;
    readonly StackPanel rows;
    readonly Grid list;
    readonly Grid editor;
    readonly Field nameField, textField;
    readonly TextBlock editorTitle;
    readonly Border brokenNotice;
    readonly TextBlock copiedToast;
    readonly DispatcherTimer toastTimer;
    Snippet? editing;

    public SnippetsPane(AppModel model, IPanelHost host) : base(model, host)
    {
        store = model.Snippets;

        search = new Field(L10n.T("Search"));
        search.Box.TextChanged += (_, _) => Rebuild();
        var add = Ui.IconButton(Glyph.Add, () => BeginEdit(null), 28, 12, tip: L10n.T("Add a snippet"));
        add.Margin = new Thickness(6, 0, 0, 0);
        var top = new DockPanel();
        DockPanel.SetDock(add, Dock.Right);
        top.Children.Add(add);
        top.Children.Add(search);

        brokenNotice = new Border
        {
            Margin = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand,
            Child = Notice(L10n.T("snippets.json is broken — click to open; nothing is overwritten")),
        };
        brokenNotice.MouseLeftButtonUp += (_, _) => Reveal();

        rows = new StackPanel();
        var scroller = Ui.Scroller(rows);
        scroller.Margin = new Thickness(0, 6, 0, 0);

        copiedToast = Ui.Label(L10n.T("Copied"), 10.5, Theme.Secondary, FontWeights.Medium);
        copiedToast.HorizontalAlignment = HorizontalAlignment.Right;
        copiedToast.VerticalAlignment = VerticalAlignment.Bottom;
        copiedToast.Opacity = 0;
        toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        toastTimer.Tick += (_, _) => { toastTimer.Stop(); copiedToast.Opacity = 0; };

        var listLayout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(brokenNotice, Dock.Top);
        listLayout.Children.Add(top);
        listLayout.Children.Add(brokenNotice);
        listLayout.Children.Add(scroller);
        list = new Grid();
        list.Children.Add(listLayout);
        list.Children.Add(copiedToast);

        // The add / edit form.
        editorTitle = Ui.Caps(L10n.T("Add a snippet"));
        nameField = new Field(L10n.T("Name (optional)"));
        textField = new Field(L10n.T("Text"), multiline: true);
        textField.Box.MinHeight = 70;
        nameField.Margin = new Thickness(0, 6, 0, 0);
        textField.Margin = new Thickness(0, 6, 0, 0);
        var save = Ui.PillButton(L10n.T("Save"), Commit, accent: true);
        var cancel = Ui.PillButton(L10n.T("Cancel"), EndEdit);
        cancel.Margin = new Thickness(0, 0, 6, 0);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);
        var form = new StackPanel();
        form.Children.Add(editorTitle);
        form.Children.Add(nameField);
        form.Children.Add(textField);
        form.Children.Add(buttons);
        editor = new Grid { Visibility = Visibility.Collapsed, Children = { form } };
        textField.Box.PreviewKeyDown += (_, e) =>
        {
            // Enter saves; Shift+Enter is a new line, for the multi-line ones.
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { Commit(); e.Handled = true; }
        };
        nameField.Box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { textField.Box.Focus(); e.Handled = true; } };

        Children.Add(list);
        Children.Add(editor);
        store.Changed += Rebuild;
        model.Privacy.Changed += Rebuild;
    }

    static UIElement Notice(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Icon(Glyph.Warning, 10, Theme.Warning));
        var label = Ui.Label(text, 10, Theme.Secondary);
        label.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(label);
        return row;
    }

    void Reveal()
    {
        store.EnsureFile();
        try { Process.Start("explorer.exe", $"/select,\"{store.FilePath}\""); } catch { }
    }

    void Rebuild()
    {
        HeaderText = store.Items.Count > 0 ? store.Items.Count.ToString() : null;
        brokenNotice.Visibility = store.FileBroken ? Visibility.Visible : Visibility.Collapsed;
        rows.Children.Clear();
        var items = store.Filtered(search.Text).ToList();
        if (store.Items.Count == 0)
        {
            var hint = Ui.Label(L10n.T("Nothing here yet — add with +"), 11, Theme.Tertiary);
            hint.HorizontalAlignment = HorizontalAlignment.Center;
            hint.Margin = new Thickness(0, 30, 0, 0);
            rows.Children.Add(hint);
            return;
        }
        foreach (var snippet in items) rows.Children.Add(Row(snippet));
    }

    UIElement Row(Snippet snippet)
    {
        var glyph = snippet.Kind switch
        {
            SnippetKind.Mail => Glyph.Mail,
            SnippetKind.Link => Glyph.Link,
            SnippetKind.Phone => Glyph.Phone,
            _ => Glyph.Text,
        };
        var icon = Ui.Icon(glyph, 11, Theme.Secondary);
        icon.Width = 18;
        var text = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 6, 0) };
        if (snippet.Label.Length > 0)
        {
            text.Children.Add(Ui.Label(snippet.Label, 11.5, Theme.White, FontWeights.SemiBold));
            var value = Ui.Label(snippet.Text.Replace('\n', ' '), 11.5, Theme.Secondary);
            value.Margin = new Thickness(8, 0, 0, 0);
            text.Children.Add(value);
        }
        else text.Children.Add(Ui.Label(snippet.Text.Replace('\n', ' '), 11.5, Theme.White));

        var edit = Ui.IconButton(Glyph.Edit, () => BeginEdit(snippet), 20, 9, tip: L10n.T("Edit"));
        var remove = Ui.IconButton(Glyph.Close, () => store.Remove(snippet), 20, 9, tip: L10n.T("Delete"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Opacity = 0 };
        actions.Children.Add(edit);
        actions.Children.Add(remove);

        var line = new DockPanel();
        DockPanel.SetDock(icon, Dock.Left);
        DockPanel.SetDock(actions, Dock.Right);
        line.Children.Add(icon);
        line.Children.Add(actions);
        line.Children.Add(text);

        var id = "snippet." + snippet.Id;
        bool hidden = Model.Privacy.Hides(PrivacySection.Snippets, id);
        var row = new Clickable
        {
            Height = 28,
            Padding = new Thickness(6, 0, 4, 0),
            CornerRadius = new CornerRadius(7),
            Hover = Theme.Surface,
            Child = Cover.Wrap(line, hidden, () => Model.Privacy.Toggle(id)),
        };
        row.MouseEnter += (_, _) => actions.Opacity = 1;
        row.MouseLeave += (_, _) => actions.Opacity = 0;
        row.Click += () =>
        {
            if (hidden) return;
            // The previous clipboard content stays in the Clipboard tab.
            Ui.Copy(snippet.Text);
            copiedToast.Opacity = 1;
            toastTimer.Stop();
            toastTimer.Start();
        };
        int index = store.Items.FindIndex(s => s.Id == snippet.Id);
        row.ContextMenu = Ui.Menu(
            (L10n.T("Edit"), () => BeginEdit(snippet)),
            (L10n.T("Move Up"), () => store.Move(snippet, index - 1)),
            (L10n.T("Move Down"), () => store.Move(snippet, index + 1)),
            (L10n.T("Move to Top"), () => store.Move(snippet, 0)),
            ("-", () => { }),
            (L10n.T("Delete"), () => store.Remove(snippet)));
        return row;
    }

    void BeginEdit(Snippet? snippet)
    {
        editing = snippet;
        editorTitle.Text = (snippet is null ? L10n.T("Add a snippet") : L10n.T("Edit")).ToUpper(L10n.Culture);
        nameField.Text = snippet?.Label ?? "";
        textField.Text = snippet?.Text ?? "";
        list.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        Host.ClaimKeyboard();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => (snippet is null ? textField.Box : nameField.Box).Focus());
    }

    void Commit()
    {
        if (editing is { } snippet) store.Update(snippet, nameField.Text, textField.Text);
        else store.Add(nameField.Text, textField.Text);
        EndEdit();
    }

    void EndEdit()
    {
        editing = null;
        editor.Visibility = Visibility.Collapsed;
        list.Visibility = Visibility.Visible;
        FocusField();
    }

    public override void FocusField()
    {
        if (editor.Visibility == Visibility.Visible) textField.Box.Focus();
        else search.Box.Focus();
    }

    public override bool OnEscape()
    {
        if (editor.Visibility == Visibility.Visible) { EndEdit(); return true; }
        if (search.Text.Length > 0) { search.Text = ""; return true; }
        return false;
    }

    protected override void OnShown() => Rebuild();
}
