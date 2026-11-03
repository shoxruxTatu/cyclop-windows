using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;

namespace Cyclop.Services;

/// <summary>
/// Now Playing, from the system media sessions — the same source as the media
/// flyout over the volume keys. Anything Windows itself can see shows up:
/// Spotify, the Music app, a video in a browser tab.
///
/// The Mac needs a perl helper to reach the private MediaRemote framework;
/// Windows publishes this as an ordinary API, so here it is a few events.
/// </summary>
internal sealed class MediaService : Observable
{
    readonly Dispatcher dispatcher;
    GlobalSystemMediaTransportControlsSessionManager? manager;
    GlobalSystemMediaTransportControlsSession? session;
    DispatcherTimer? ticker;
    bool started;
    int artworkRequest;
    string? lastSummary;

    public string? Title { get; private set; }
    public string Artist { get; private set; } = "";
    public string Album { get; private set; } = "";
    public BitmapSource? Artwork { get; private set; }
    public bool ArtworkPending { get; private set; }
    public bool IsPlaying { get; private set; }
    public bool CanSkipNext { get; private set; }
    public bool CanSkipPrevious { get; private set; }
    public bool CanSeek { get; private set; }
    public string? SourceName { get; private set; }
    public double Duration { get; private set; }
    TimeSpan basePosition;
    DateTimeOffset baseTime;
    double rate = 1;

    public MediaService(Dispatcher dispatcher) => this.dispatcher = dispatcher;

    /// <summary>A session is a track even without a title: a video in a
    /// player embedded in a page often publishes none, and "nothing is
    /// playing" over a video that is plainly playing reads as broken.</summary>
    public bool HasTrack =>
        session is not null && (!string.IsNullOrWhiteSpace(Title) ||
            status is GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                or GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused);

    GlobalSystemMediaTransportControlsSessionPlaybackStatus? status;

    /// <summary>The title, or the page or app it comes from when there is none.</summary>
    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(Title) ? Title! : !string.IsNullOrEmpty(SourceName) ? SourceName! : L10n.T("Unknown");

    /// <summary>Where the track is now: the last reported position carried
    /// forward by the clock while playing, the way the system flyout does.</summary>
    public double Position
    {
        get
        {
            var position = basePosition;
            if (IsPlaying && baseTime != default) position += TimeSpan.FromTicks((long)((DateTimeOffset.Now - baseTime).Ticks * rate));
            var seconds = position.TotalSeconds;
            return Duration > 0 ? Math.Clamp(seconds, 0, Duration) : Math.Max(0, seconds);
        }
    }

    public async void Start()
    {
        if (started) return;
        started = true;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (!started) return;
            Support.Log("media sessions: manager ready");
            manager.CurrentSessionChanged += (_, _) => dispatcher.BeginInvoke(PickSession);
            manager.SessionsChanged += (_, _) => dispatcher.BeginInvoke(PickSession);
            PickSession();
        }
        catch (Exception e)
        {
            Support.Log("media sessions unavailable: " + e.Message);
        }
    }

    public void Stop()
    {
        started = false;
        Attach(null);
        manager = null;
        SetActive(false);
    }

    /// <summary>The position ticker runs only while the panel is open.</summary>
    public void SetActive(bool active)
    {
        ticker?.Stop();
        ticker = null;
        if (!active) return;
        ticker = new DispatcherTimer(TimeSpan.FromSeconds(0.25), DispatcherPriority.Background, (_, _) => { if (IsPlaying) Notify(); }, dispatcher);
        ticker.Start();
    }

    /// <summary>The system's current session; failing that, whichever is
    /// playing — the current one is sometimes a paused tab while music plays
    /// in another app.</summary>
    void PickSession()
    {
        if (manager is null) return;
        GlobalSystemMediaTransportControlsSession? pick = null;
        try
        {
            pick = manager.GetCurrentSession();
            var sessions = manager.GetSessions();
            if (pick?.GetPlaybackInfo()?.PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            {
                var playing = sessions.FirstOrDefault(s =>
                    s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing);
                if (playing is not null) pick = playing;
            }
            pick ??= sessions.FirstOrDefault();
            var summary = string.Join(", ", sessions.Select(s => $"{s.SourceAppUserModelId}={s.GetPlaybackInfo()?.PlaybackStatus}"));
            if (summary != lastSummary)
            {
                lastSummary = summary;
                Support.Log($"media sessions: [{summary}] current={pick?.SourceAppUserModelId ?? "none"}");
            }
        }
        catch (Exception e) { Support.Log("media sessions: " + e.Message); }
        if (!ReferenceEquals(pick, session)) Attach(pick);
    }

    void Attach(GlobalSystemMediaTransportControlsSession? next)
    {
        if (session is not null)
        {
            session.MediaPropertiesChanged -= OnProperties;
            session.PlaybackInfoChanged -= OnPlayback;
            session.TimelinePropertiesChanged -= OnTimeline;
        }
        session = next;
        if (session is null)
        {
            Title = null;
            Artist = Album = "";
            Artwork = null;
            ArtworkPending = false;
            IsPlaying = false;
            status = null;
            SourceName = null;
            Duration = 0;
            basePosition = TimeSpan.Zero;
            Notify();
            return;
        }
        session.MediaPropertiesChanged += OnProperties;
        session.PlaybackInfoChanged += OnPlayback;
        session.TimelinePropertiesChanged += OnTimeline;
        SourceName = FriendlyName(session.SourceAppUserModelId);
        ReadPlayback();
        ReadTimeline();
        _ = ReadPropertiesAsync();
    }

    void OnProperties(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e) =>
        dispatcher.BeginInvoke(() => { if (ReferenceEquals(s, session)) _ = ReadPropertiesAsync(); });

    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e) =>
        dispatcher.BeginInvoke(() => { if (ReferenceEquals(s, session)) { ReadPlayback(); PickSession(); } });

    void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e) =>
        dispatcher.BeginInvoke(() => { if (ReferenceEquals(s, session)) ReadTimeline(); });

    void ReadPlayback()
    {
        try
        {
            var info = session?.GetPlaybackInfo();
            if (info is null) return;
            bool wasPlaying = IsPlaying;
            if (wasPlaying) { basePosition = TimeSpan.FromSeconds(Position); baseTime = DateTimeOffset.Now; }
            status = info.PlaybackStatus;
            IsPlaying = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (!wasPlaying && IsPlaying) baseTime = DateTimeOffset.Now;
            CanSkipNext = info.Controls.IsNextEnabled;
            CanSkipPrevious = info.Controls.IsPreviousEnabled;
            CanSeek = info.Controls.IsPlaybackPositionEnabled;
            rate = info.PlaybackRate is { } r && r > 0 ? r : 1;
            Notify();
        }
        catch { }
    }

    void ReadTimeline()
    {
        try
        {
            var t = session?.GetTimelineProperties();
            if (t is null) return;
            Duration = Math.Max(0, (t.EndTime - t.StartTime).TotalSeconds);
            basePosition = t.Position - t.StartTime;
            baseTime = t.LastUpdatedTime == default ? DateTimeOffset.Now : t.LastUpdatedTime;
            Notify();
        }
        catch { }
    }

    async Task ReadPropertiesAsync()
    {
        var current = session;
        if (current is null) return;
        int request = ++artworkRequest;
        try
        {
            var props = await current.TryGetMediaPropertiesAsync();
            if (request != artworkRequest || props is null) return;
            Support.Log($"media properties: \"{props.Title}\" — \"{props.Artist}\" from {current.SourceAppUserModelId}");
            bool sameTrack = props.Title == Title && props.Artist == Artist;
            Title = props.Title;
            Artist = props.Artist ?? "";
            Album = props.AlbumTitle ?? "";
            if (!sameTrack) { Artwork = null; ArtworkPending = props.Thumbnail is not null; }
            Notify();

            if (props.Thumbnail is null) { ArtworkPending = false; Notify(); return; }
            using var stream = await props.Thumbnail.OpenReadAsync();
            using var input = stream.AsStreamForRead();
            var memory = new MemoryStream();
            await input.CopyToAsync(memory);
            memory.Position = 0;
            if (request != artworkRequest) return;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = memory;
            image.DecodePixelWidth = 240;
            image.EndInit();
            image.Freeze();
            Artwork = image;
        }
        catch
        {
            // A source with no readable cover keeps the plain mark.
        }
        ArtworkPending = false;
        Notify();
    }

    /// <summary>
    /// Play or pause, asked for explicitly rather than as a toggle — a player
    /// embedded in a web page often refuses the toggle while accepting the
    /// two plain commands. When the session still does not move, the media
    /// key is pressed instead: every browser and player answers that one,
    /// because it is the same key as on the keyboard.
    /// </summary>
    public async void TogglePlayPause()
    {
        var current = session;
        bool wasPlaying = IsPlaying;
        bool ok = false;
        try
        {
            if (current is not null)
            {
                var controls = current.GetPlaybackInfo()?.Controls;
                if (wasPlaying && controls?.IsPauseEnabled == true) ok = await current.TryPauseAsync();
                else if (!wasPlaying && controls?.IsPlayEnabled == true) ok = await current.TryPlayAsync();
                if (!ok) ok = await current.TryTogglePlayPauseAsync();
            }
        }
        catch (Exception e) { Support.Log("media: play/pause failed: " + e.Message); }
        Support.Log($"media: play/pause on {current?.SourceAppUserModelId ?? "none"} → {ok}");
        await FallBackIfUnchanged(ok, () => IsPlaying != wasPlaying, MediaKey.PlayPause);
    }

    public async void Next() => await Skip(forward: true);

    public async void Previous() => await Skip(forward: false);

    async Task Skip(bool forward)
    {
        var current = session;
        var title = Title;
        bool ok = false;
        try
        {
            if (current is not null) ok = forward ? await current.TrySkipNextAsync() : await current.TrySkipPreviousAsync();
        }
        catch (Exception e) { Support.Log("media: skip failed: " + e.Message); }
        Support.Log($"media: {(forward ? "next" : "previous")} → {ok}");
        await FallBackIfUnchanged(ok, () => Title != title, forward ? MediaKey.Next : MediaKey.Previous);
    }

    /// <summary>A refused command, or an accepted one that changed nothing
    /// within half a second, gets the media key. An accepted command is given
    /// the time to land first, so it is never sent twice — a second play/pause
    /// would undo the first.</summary>
    async Task FallBackIfUnchanged(bool accepted, Func<bool> changed, byte key)
    {
        if (accepted)
        {
            await Task.Delay(600);
            ReadPlayback();
            if (changed()) return;
        }
        else if (session is null) { MediaKey.Press(key); return; }
        if (changed()) return;
        Support.Log("media: no change, pressing the media key");
        MediaKey.Press(key);
    }

    /// <summary>Brings the window the sound comes from to the front.</summary>
    public bool RevealSource()
    {
        var id = session?.SourceAppUserModelId;
        if (string.IsNullOrEmpty(id)) return false;
        Support.Log($"media: reveal {id}");
        return SourceWindow.Reveal(id, Title);
    }

    public async void Seek(double seconds)
    {
        if (session is null) return;
        basePosition = TimeSpan.FromSeconds(seconds);
        baseTime = DateTimeOffset.Now;
        Notify();
        try
        {
            var start = session.GetTimelineProperties().StartTime;
            await session.TryChangePlaybackPositionAsync((start + TimeSpan.FromSeconds(seconds)).Ticks);
        }
        catch { }
    }

    /// <summary>"Spotify.exe" → "Spotify", Chrome's and Edge's ids to their
    /// names. Store apps report a package id; its first part reads well enough.</summary>
    static string FriendlyName(string id)
    {
        if (string.IsNullOrEmpty(id)) return "";
        var lower = id.ToLowerInvariant();
        if (lower.Contains("chrome")) return "Chrome";
        if (lower.Contains("msedge")) return "Edge";
        if (lower.Contains("firefox") || lower == "308046b0af4a39cb") return "Firefox";
        if (lower.Contains("spotify")) return "Spotify";
        if (lower.Contains("zunemusic") || lower.Contains("media.player")) return L10n.T("Media Player");
        if (lower.Contains("yandex")) return "Yandex";
        if (lower.Contains("opera")) return "Opera";
        if (lower.Contains("vlc")) return "VLC";
        var name = id;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : id;
    }
}

/// <summary>The hardware media keys, pressed in software.</summary>
internal static class MediaKey
{
    public const byte Next = 0xB0;
    public const byte Previous = 0xB1;
    public const byte PlayPause = 0xB3;

    const uint ExtendedKey = 0x1, KeyUp = 0x2;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    public static void Press(byte key)
    {
        keybd_event(key, 0, ExtendedKey, UIntPtr.Zero);
        keybd_event(key, 0, ExtendedKey | KeyUp, UIntPtr.Zero);
    }
}
