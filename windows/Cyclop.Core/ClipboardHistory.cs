namespace Cyclop;

public enum ClipKind { Text, File }

public sealed class ClipItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public ClipKind Kind { get; init; }
    /// <summary>The text for a text copy, the full path for a file.</summary>
    public string Value { get; init; } = "";
    public DateTime Date { get; init; } = DateTime.Now;

    public string Preview => Kind == ClipKind.File ? System.IO.Path.GetFileName(Value) : Value.Trim();
    public bool IsLink => Kind == ClipKind.Text && (Value.StartsWith("http://") || Value.StartsWith("https://"));

    public bool SamePayload(ClipItem other) => Kind == other.Kind && Value == other.Value;
}

/// <summary>The last forty copies, newest first, duplicates folded into the
/// newest occurrence. The Windows clipboard plumbing lives in the app; this is
/// only the list.</summary>
public sealed class ClipboardHistory : Observable
{
    public const int Limit = 40;
    public List<ClipItem> Items { get; } = new();

    public void Record(ClipItem item)
    {
        Items.RemoveAll(i => i.SamePayload(item));
        Items.Insert(0, item);
        if (Items.Count > Limit) Items.RemoveRange(Limit, Items.Count - Limit);
        Notify();
    }

    public void Remove(ClipItem item)
    {
        Items.RemoveAll(i => i.Id == item.Id);
        Notify();
    }

    public void Clear()
    {
        Items.Clear();
        Notify();
    }

    /// <summary>Freshly used entries bubble to the top.</summary>
    public void MoveToTop(ClipItem item)
    {
        int index = Items.FindIndex(i => i.Id == item.Id);
        if (index <= 0) return;
        Items.RemoveAt(index);
        Items.Insert(0, item);
        Notify();
    }
}
