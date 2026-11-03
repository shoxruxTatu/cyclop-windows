using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Cyclop.App;
using Cyclop.UI;

namespace Cyclop.Panes;

/// <summary>
/// Scratch space: a phone number from a call, half a link, a thought for an
/// hour. No folders, no formatting, no search — for that there are editors.
/// </summary>
internal sealed class NotesPane : Pane
{
    readonly NoteStore notes;
    readonly StackPanel cards;
    readonly Field editor;
    readonly Grid editorHost;
    readonly Clickable delete;
    bool loading;

    public NotesPane(AppModel model, IPanelHost host) : base(model, host)
    {
        notes = model.Notes;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(176) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        ColumnDefinitions.Add(new ColumnDefinition());

        var add = Ui.PillButton(L10n.T("New Note"), () =>
        {
            notes.Add();
            FocusField();
        }, Glyph.Add);
        add.HorizontalAlignment = HorizontalAlignment.Stretch;
        cards = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };
        var left = new DockPanel();
        DockPanel.SetDock(add, Dock.Top);
        left.Children.Add(add);
        left.Children.Add(Ui.Scroller(cards));
        Children.Add(left);

        editor = new Field(L10n.T("Jot something down…"), multiline: true, size: 12.5);
        editor.Box.TextChanged += (_, _) =>
        {
            if (loading) return;
            if (notes.SelectedNote is null)
            {
                // Typing into the blank editor starts a note rather than
                // asking for a button press first.
                if (editor.Text.Length == 0) return;
                var text = editor.Text;
                notes.Add();   // reloads the editor with the empty new note…
                notes.Update(notes.Selected!.Value, text);
                RebuildCards();
                LoadEditor();  // …so the typed text is put back, caret at its end
                editor.Box.CaretIndex = text.Length;
                return;
            }
            notes.Update(notes.SelectedNote.Id, editor.Text);
            RefreshCard(notes.SelectedNote);
        };
        delete = Ui.IconButton(Glyph.Delete, () =>
        {
            if (notes.Selected is { } id) notes.Remove(id);
        }, 24, 11, tip: L10n.T("Delete"));
        delete.HorizontalAlignment = HorizontalAlignment.Right;
        delete.VerticalAlignment = VerticalAlignment.Top;
        delete.Margin = new Thickness(0, 4, 4, 0);
        editorHost = new Grid();
        editorHost.Children.Add(editor);
        editorHost.Children.Add(delete);
        Grid.SetColumn(editorHost, 2);
        Children.Add(editorHost);

        notes.Changed += () => { RebuildCards(); LoadEditor(); };
        model.Privacy.Changed += () => { RebuildCards(); LoadEditor(); };
        RebuildCards();
        LoadEditor();
    }

    static string FirstLine(Note note)
    {
        var line = note.Text.Trim().Split('\n').FirstOrDefault()?.Trim() ?? "";
        return line.Length == 0 ? L10n.T("Empty note") : line;
    }

    readonly System.Collections.Generic.Dictionary<Guid, TextBlock> cardTitles = new();

    void RebuildCards()
    {
        HeaderText = notes.Notes.Count > 0 ? notes.Notes.Count.ToString() : null;
        cards.Children.Clear();
        cardTitles.Clear();
        foreach (var note in notes.Notes)
        {
            var title = Ui.Label(FirstLine(note), 11.5, note.Text.Trim().Length == 0 ? Theme.Tertiary : Theme.White, FontWeights.Medium);
            var time = Ui.Label(note.Edited.ToString("t", L10n.Culture), 9.5, Theme.Tertiary);
            var stack = new StackPanel();
            stack.Children.Add(title);
            stack.Children.Add(time);
            cardTitles[note.Id] = title;
            var id = "note." + note.Id;
            bool hidden = Model.Privacy.Hides(PrivacySection.Notes, id);
            var card = new Clickable
            {
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 0, 3),
                CornerRadius = new CornerRadius(7),
                Normal = note.Id == notes.Selected ? Theme.SurfaceHover : Theme.Clear,
                Hover = note.Id == notes.Selected ? Theme.SurfaceHover : Theme.Surface,
                Child = Cover.Wrap(stack, hidden, () => Model.Privacy.Toggle(id)),
            };
            card.Click += () =>
            {
                notes.Select(note.Id);
                FocusField();
            };
            cards.Children.Add(card);
        }
    }

    void RefreshCard(Note note)
    {
        if (cardTitles.TryGetValue(note.Id, out var title))
        {
            title.Text = FirstLine(note);
            title.Foreground = note.Text.Trim().Length == 0 ? Theme.Tertiary : Theme.White;
        }
    }

    void LoadEditor()
    {
        var note = notes.SelectedNote;
        loading = true;
        editor.Text = note?.Text ?? "";
        loading = false;
        delete.Visibility = note is null ? Visibility.Collapsed : Visibility.Visible;
        bool hidden = note is not null && Model.Privacy.Hides(PrivacySection.Notes, "note." + note.Id);
        editor.Visibility = hidden ? Visibility.Hidden : Visibility.Visible;
    }

    public override void FocusField()
    {
        Host.ClaimKeyboard();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            editor.Box.Focus();
            editor.Box.CaretIndex = editor.Box.Text.Length;
        });
    }

    protected override void OnShown()
    {
        RebuildCards();
        LoadEditor();
    }
}
