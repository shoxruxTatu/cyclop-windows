using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Cyclop.Services;

/// <summary>
/// Keeps Cyclop current. Twenty seconds after launch and every hour it
/// asks the update server; a newer build is downloaded, checked against the
/// release's SHA-256 and swapped in place of the running exe — Windows lets a
/// running exe be renamed, so the old file steps aside, the new one takes its
/// name, starts, and the old process quits. Next launch removes the old file.
///
/// The server address is baked in at release time (the <c>UpdateServer</c>
/// build property). A build without one — a local build — never asks anyone.
/// </summary>
internal sealed class UpdateService : Observable
{
    /// <summary>Hourly: a release should reach people the same day, and one
    /// small request an hour is nothing for the server or the PC.</summary>
    static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    static readonly TimeSpan Retry = TimeSpan.FromMinutes(5);
    System.Threading.Timer? retry;

    readonly ConfigStore config;
    readonly AppState state;
    readonly Func<bool> canRestart;
    readonly Action quit;
    Repeating? timer;
    System.Threading.Timer? firstCheck;

    public UpdateInfo? Available { get; private set; }
    public bool Busy { get; private set; }
    public string? Status { get; private set; }

    public UpdateService(ConfigStore config, AppState state, Func<bool> canRestart, Action quit)
    {
        this.config = config;
        this.state = state;
        this.canRestart = canRestart;
        this.quit = quit;
    }

    public static string CurrentVersion => typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string? Server =>
        typeof(UpdateService).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "UpdateServer")?.Value is { Length: > 0 } url && url.StartsWith("https://")
            ? url : null;

    public static bool IsEnabled => Server is not null;

    static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64";

    public void Start()
    {
        if (!IsEnabled) return;
        var ctx = System.Threading.SynchronizationContext.Current;
        firstCheck = new System.Threading.Timer(_ => ctx?.Post(_ => _ = CheckAsync(manual: false), null),
            null, TimeSpan.FromSeconds(20), System.Threading.Timeout.InfiniteTimeSpan);
        timer = new Repeating(Interval, () => _ = CheckAsync(manual: false));
    }

    public void Stop()
    {
        firstCheck?.Dispose();
        retry?.Dispose();
        timer?.Dispose();
    }

    public void CheckNow() => _ = CheckAsync(manual: true);

    /// <summary>Installs what a check found, from the Settings button.</summary>
    public void InstallNow()
    {
        if (Available is { } info) _ = InstallAsync(info, force: true);
    }

    async Task CheckAsync(bool manual)
    {
        if (Busy || Server is not { } server) return;
        Busy = true;
        Status = L10n.T("Checking…");
        Notify();
        try
        {
            var id = config.AnonymousStats ? state.EnsureInstallId() : null;
            var info = await Updates.CheckAsync(server, CurrentVersion, Arch, Environment.OSVersion.Version.ToString(), id);
            Available = info is not null && Updates.IsNewer(info.Version, CurrentVersion) ? info : null;
            Status = Available is null ? L10n.T("Cyclop is up to date") : L10n.T("Version %@ is available", Available.Version);
            Support.Log($"update check: current {CurrentVersion}, server says {info?.Version ?? "nothing"}");
        }
        catch (Exception e)
        {
            Status = manual ? L10n.T("Could not reach the update server") : null;
            Support.Log("update check failed: " + e.Message);
        }
        Busy = false;
        Notify();
        if (Available is { } found && (config.AutoUpdate || manual)) await InstallAsync(found, force: manual);
    }

    async Task InstallAsync(UpdateInfo info, bool force)
    {
        if (Busy) return;
        // Not mid-sentence, mid-screenshot or mid-take: the restart waits for
        // the next check rather than pull the panel away under the user.
        if (!force && !canRestart())
        {
            // Busy right now — the panel is open, a screenshot is being taken.
            // Asked again in a few minutes, not at the next hourly check.
            var ctx = System.Threading.SynchronizationContext.Current;
            retry?.Dispose();
            retry = new System.Threading.Timer(_ => ctx?.Post(_ => _ = InstallAsync(info, force: false), null),
                null, Retry, System.Threading.Timeout.InfiniteTimeSpan);
            return;
        }
        Busy = true;
        Status = L10n.T("Downloading %@…", info.Version);
        Notify();
        try
        {
            var file = await Updates.DownloadAsync(info, Support.Directory("Updates"));
            Apply(file, info.Version);
        }
        catch (Exception e)
        {
            Status = L10n.T("Update failed: %@", e.Message);
            Support.Log("update failed: " + e);
        }
        Busy = false;
        Notify();
    }

    /// <summary>The swap. On failure the old exe is put back where it was.</summary>
    void Apply(string downloaded, string version)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("no exe path");
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Copy(downloaded, exe, overwrite: true);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
        try { File.Delete(downloaded); } catch { }
        Support.Log($"update: {CurrentVersion} → {version}, restarting");
        Process.Start(new ProcessStartInfo(exe, $"--updated {CurrentVersion}") { UseShellExecute = false });
        quit();
    }

    /// <summary>On the first launch after an update: the previous exe goes.
    /// It may still be quitting, hence the patience.</summary>
    public static void CleanUpAfterUpdate()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        var old = exe + ".old";
        if (!File.Exists(old)) return;
        Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20 && File.Exists(old); attempt++)
            {
                try { File.Delete(old); } catch { await Task.Delay(500); }
            }
        });
    }
}
