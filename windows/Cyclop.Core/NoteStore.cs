using System.Text.Json;

namespace Cyclop;

public sealed class Note
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Text { get; set; } = "";
    public DateTime Edited { get; set; } = DateTime.Now;
}

/// <summary>
/// Scratch notes: somewhere to put a thought down for an hour. Deliberately not
/// a notes app — created empty and instantly, deleted in one click, and the
/// blank ones sweep themselves out when the tab is left.
/// </summary>
public sealed class NoteStore : Observable
{
    readonly string path;
    readonly DebouncedWrite saves = new();

    public List<Note> Notes { get; private set; } = new();
    public Guid? Selected { get; set; }

    public NoteStore(string? path = null)
    {
        this.path = path ?? Support.File("notes.json");
        Load();
    }

    public Note? SelectedNote => Notes.FirstOrDefault(n => n.Id == Selected);

    /// <summary>Newest on top, and the order never changes afterwards.</summary>
    public Note Add()
    {
        var note = new Note();
        Notes.Insert(0, note);
        Selected = note.Id;
        ScheduleSave();
        Notify();
        return note;
    }

    /// <summary>Typing does not raise <see cref="Observable.Changed"/>: the
    /// editor already shows the text, and rebuilding the list per keystroke
    /// would take the caret with it. The pane refreshes the one card itself.</summary>
    public void Update(Guid id, string text)
    {
        var note = Notes.FirstOrDefault(n => n.Id == id);
        if (note is null || note.Text == text) return;
        note.Text = text;
        note.Edited = DateTime.Now;
        ScheduleSave();
    }

    public void Remove(Guid id)
    {
        Notes.RemoveAll(n => n.Id == id);
        if (Selected == id) Selected = Notes.FirstOrDefault()?.Id;
        ScheduleSave();
        Notify();
    }

    public void Select(Guid id)
    {
        Selected = id;
        Notify();
    }

    /// <summary>Leaving the tab: blank notes go, they cost one click to recreate.</summary>
    public void Leave()
    {
        int removed = Notes.RemoveAll(n => string.IsNullOrWhiteSpace(n.Text));
        if (Selected is { } id && Notes.All(n => n.Id != id)) Selected = Notes.FirstOrDefault()?.Id;
        if (removed > 0) Notify();
        ScheduleSave();
        Flush();
    }

    void Load()
    {
        try
        {
            if (!System.IO.File.Exists(path)) return;
            Notes = JsonSerializer.Deserialize<List<Note>>(System.IO.File.ReadAllText(path), ConfigStore.Json) ?? new();
            Selected = Notes.FirstOrDefault()?.Id;
        }
        catch (Exception e) { Support.Log("notes.json is not readable: " + e.Message); }
    }

    void ScheduleSave() => saves.Schedule(Persist);

    public void Flush() => saves.Flush();

    void Persist()
    {
        try { Support.WriteAtomic(path, JsonSerializer.Serialize(Notes, ConfigStore.Json)); }
        catch (Exception e) { Support.Log("cannot write notes.json: " + e.Message); }
    }
}
