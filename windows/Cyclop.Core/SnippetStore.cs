using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cyclop;

public enum SnippetKind { Text, Mail, Link, Phone }

public sealed record Snippet(string Label, string Text)
{
    /// <summary>Name and value together: two snippets called the same thing
    /// with different values are both kept. Only a full duplicate collapses.</summary>
    public string Id => Label + "\0" + Text;

    /// <summary>Guessed from the value, so a row is recognisable before it is read.</summary>
    public SnippetKind Kind
    {
        get
        {
            var value = Text.Trim();
            if (value.Contains('@') && !value.Contains(' ')) return SnippetKind.Mail;
            if (value.StartsWith("http://") || value.StartsWith("https://")) return SnippetKind.Link;
            int digits = value.Count(char.IsDigit);
            if (digits >= 7 && value.All(c => char.IsDigit(c) || " +-()".Contains(c))) return SnippetKind.Phone;
            return SnippetKind.Text;
        }
    }
}

/// <summary>
/// A hand-kept list of things worth not retyping, in <c>snippets.json</c> —
/// the same format as on the Mac: <c>[{ "label": "Mail", "text": "…" }]</c>,
/// <c>label</c> optional. Edited from the panel and by hand alike, so the file
/// is re-read before every write, and a file that cannot be read is never
/// written over.
/// </summary>
public sealed class SnippetStore : Observable
{
    public string FilePath { get; }
    public List<Snippet> Items { get; private set; } = new();
    public bool FileBroken { get; private set; }

    public SnippetStore(string? path = null) => FilePath = path ?? Support.File("snippets.json");

    /// <summary>Name and value alike, case- and accent-blind.</summary>
    public IEnumerable<Snippet> Filtered(string query)
    {
        var needle = query.Trim();
        if (needle.Length == 0) return Items;
        return Items.Where(s => Matches(s.Label, needle) || Matches(s.Text, needle));
    }

    static bool Matches(string haystack, string needle) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            haystack, needle, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>Re-read on every visit to the tab: the file is edited from
    /// outside the app.</summary>
    public void Reload()
    {
        if (!System.IO.File.Exists(FilePath))
        {
            Items = new();
            FileBroken = false;
            Notify();
            return;
        }
        try
        {
            Items = Decode(System.IO.File.ReadAllText(FilePath));
            FileBroken = false;
        }
        catch (Exception e)
        {
            // Keep what was on screen and say so — silence here is what turns
            // a stray comma into a lost file.
            FileBroken = true;
            Support.Log("snippets.json is not readable: " + e.Message);
        }
        Notify();
    }

    internal static List<Snippet> Decode(string json)
    {
        var array = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonArray ?? throw new JsonException("not an array");
        var list = new List<Snippet>();
        foreach (var node in array)
        {
            if (node is not JsonObject obj) throw new JsonException("not an object");
            var text = obj["text"]?.GetValue<string>() ?? throw new JsonException("text is missing");
            var label = obj["label"]?.GetValue<string>() ?? "";
            list.Add(new Snippet(label, text));
        }
        return list;
    }

    internal static string Encode(IEnumerable<Snippet> items)
    {
        var array = new JsonArray();
        foreach (var s in items)
        {
            // An unnamed snippet is written without the key, the way the file
            // is documented to be written by hand.
            var obj = new JsonObject();
            if (s.Label.Length > 0) obj["label"] = s.Label;
            obj["text"] = s.Text;
            array.Add(obj);
        }
        return array.ToJsonString(ConfigStore.Json);
    }

    public void Add(string label, string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return;
        var snippet = new Snippet(label.Trim(), value);
        Reload();
        if (FileBroken) return;
        Items.RemoveAll(s => s.Id == snippet.Id);
        Items.Insert(0, snippet);
        Persist();
    }

    public void Remove(Snippet snippet)
    {
        Items.RemoveAll(s => s.Id == snippet.Id);
        Persist();
    }

    /// <summary>The order is the file's order, so moving a row is a real edit.</summary>
    public void Move(Snippet snippet, int index)
    {
        int current = Items.FindIndex(s => s.Id == snippet.Id);
        if (current < 0) return;
        int target = Math.Clamp(index, 0, Items.Count - 1);
        if (target == current) return;
        var item = Items[current];
        Items.RemoveAt(current);
        Items.Insert(target, item);
        Persist();
    }

    /// <summary>Edits in place, keeping the position. An emptied value cancels
    /// the edit rather than deleting the row.</summary>
    public void Update(Snippet snippet, string label, string text)
    {
        var value = text.Trim();
        if (value.Length == 0) return;
        var edited = new Snippet(label.Trim(), value);
        if (edited == snippet) return;
        Reload();
        if (FileBroken) return;
        Items.RemoveAll(s => s.Id == edited.Id && s.Id != snippet.Id);
        // Found after the removal, never before: a copy above this row shifts it.
        int index = Items.FindIndex(s => s.Id == snippet.Id);
        if (index < 0) return;
        Items[index] = edited;
        Persist();
    }

    void Persist()
    {
        if (!FileBroken)
        {
            try { Support.WriteAtomic(FilePath, Encode(Items)); }
            catch (Exception e) { Support.Log("cannot write snippets.json: " + e.Message); }
        }
        Notify();
    }

    /// <summary>Explorer cannot select a file that is not there yet.</summary>
    public void EnsureFile()
    {
        if (!System.IO.File.Exists(FilePath)) Support.WriteAtomic(FilePath, "[]");
    }
}
