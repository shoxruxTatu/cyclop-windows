namespace Cyclop;

/// <summary>
/// The script, and where reading it has got to. Kept as plain text in
/// <c>teleprompter.txt</c> — a script is written elsewhere and pasted here, and
/// a file any editor opens is worth more than a format only this app reads.
/// </summary>
public sealed class TeleprompterStore : Observable
{
    /// <summary>Pixels per second the text creeps upward at 1×. Slow: a
    /// comfortable reading pace is far slower than anyone guesses.</summary>
    public const double BaseRate = 22;

    readonly string path;
    readonly ConfigStore config;
    readonly DebouncedWrite saves = new();
    string script = "";
    double speed = 1, fontSize = 30;

    public TeleprompterStore(ConfigStore config, string? path = null)
    {
        this.config = config;
        this.path = path ?? Support.File("teleprompter.txt");
        try { if (System.IO.File.Exists(this.path)) script = System.IO.File.ReadAllText(this.path); } catch { }
        speed = Math.Clamp(config.TeleprompterSpeed, 0.3, 3.0);
        fontSize = Math.Clamp(config.TeleprompterFontSize, 18, 64);
    }

    public string FilePath => path;

    public string Script
    {
        get => script;
        set
        {
            if (script == value) return;
            script = value;
            // Rewriting mid-take would leave the scroll on a line that is gone.
            Offset = 0;
            saves.Schedule(Persist);
            Notify();
        }
    }

    public double Speed
    {
        get => speed;
        set
        {
            value = Math.Clamp(value, 0.3, 3.0);
            if (Math.Abs(speed - value) < 0.0001) return;
            speed = value;
            config.TeleprompterSpeed = value;
            Notify();
        }
    }

    public double FontSize
    {
        get => fontSize;
        set
        {
            value = Math.Clamp(value, 18, 64);
            if (Math.Abs(fontSize - value) < 0.0001) return;
            fontSize = value;
            config.TeleprompterFontSize = value;
            Notify();
        }
    }

    /// <summary>How far the text has scrolled. A new session starts at the top.</summary>
    public double Offset { get; set; }
    public bool IsRunning { get; private set; }
    public double ContentHeight { get; set; }
    public double ViewportHeight { get; set; }

    /// <summary>The last line stops in the middle of the window, where every
    /// line before it was read.</summary>
    public double MaxOffset => Math.Max(0, ContentHeight - ViewportHeight / 2);

    public void Toggle()
    {
        if (IsRunning) Pause(); else Start();
    }

    public void Start()
    {
        if (string.IsNullOrEmpty(script) || IsRunning) return;
        if (Offset >= MaxOffset) Offset = 0;
        IsRunning = true;
        Notify();
    }

    public void Pause()
    {
        if (!IsRunning) return;
        IsRunning = false;
        Notify();
    }

    public void Rewind()
    {
        IsRunning = false;
        Offset = 0;
        Notify();
    }

    /// <summary>Stops the clock and leaves the position alone — for the panel
    /// closing or the tab changing.</summary>
    public void Suspend() => Pause();

    /// <summary>Advances by the time since the last frame.</summary>
    public void Tick(double seconds)
    {
        if (!IsRunning) return;
        Offset = Math.Min(MaxOffset, Offset + BaseRate * speed * seconds);
        if (Offset >= MaxOffset) Pause();
    }

    public void Flush() => saves.Flush();

    void Persist()
    {
        try { Support.WriteAtomic(path, script); }
        catch (Exception e) { Support.Log("cannot write teleprompter.txt: " + e.Message); }
    }

    public void EnsureFile()
    {
        if (!System.IO.File.Exists(path)) Persist();
    }
}
