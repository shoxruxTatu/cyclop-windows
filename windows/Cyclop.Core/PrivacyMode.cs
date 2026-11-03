namespace Cyclop;

public enum PrivacySection { Clipboard, Snippets, Calendar, Notes }

/// <summary>
/// Hides what the panel holds behind a field of dots, for screens somebody
/// else is watching. Chosen per section, persisted, and nothing here guesses
/// at what is a secret: covering everything in a chosen section is
/// predictable, and predictability is the feature.
/// </summary>
public sealed class PrivacyMode : Observable
{
    readonly ConfigStore config;
    readonly HashSet<string> revealed = new();
    readonly HashSet<PrivacySection> sections;

    public PrivacyMode(ConfigStore config)
    {
        this.config = config;
        sections = config.Privacy
            .Select(s => Enum.GetValues<PrivacySection>().Cast<PrivacySection?>()
                .FirstOrDefault(v => Id(v!.Value) == s))
            .Where(v => v.HasValue).Select(v => v!.Value).ToHashSet();
    }

    public static string Id(PrivacySection section) => section.ToString().ToLowerInvariant();

    public static string Title(PrivacySection section) => L10n.T(section.ToString());

    public bool Covers(PrivacySection section) => sections.Contains(section);
    public bool CoversAll => sections.Count == Enum.GetValues<PrivacySection>().Length;
    public bool CoversAny => sections.Count > 0;

    public void SetCovering(PrivacySection section, bool on)
    {
        if (on) sections.Add(section); else sections.Remove(section);
        Persist();
    }

    public void SetCoveringAll(bool on)
    {
        sections.Clear();
        if (on) foreach (var s in Enum.GetValues<PrivacySection>()) sections.Add(s);
        Persist();
    }

    void Persist()
    {
        config.Privacy = sections.Select(Id).OrderBy(s => s).ToList();
        if (!CoversAny) revealed.Clear();
        Notify();
    }

    /// <summary>True when this particular row has to be covered right now.</summary>
    public bool Hides(PrivacySection section, string id) => Covers(section) && !revealed.Contains(id);

    public void Toggle(string id)
    {
        if (!revealed.Remove(id)) revealed.Add(id);
        Notify();
    }

    /// <summary>Back to covered whenever the panel folds: a row uncovered once
    /// must not still be uncovered the next time, when the camera is.</summary>
    public void CoverEverything()
    {
        if (revealed.Count == 0) return;
        revealed.Clear();
        Notify();
    }
}
