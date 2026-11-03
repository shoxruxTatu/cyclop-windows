using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Cyclop.Services;

/// <summary>
/// Where clipboard screenshots are kept: <c>Pictures\Cyclop</c>. A screenshot
/// taken to the clipboard (Win+Shift+S) exists only in memory — paste it once
/// and it is gone. The vault writes it to disk so the shelf can hold it.
/// Nothing here is deleted behind the user's back.
/// </summary>
internal static class ScreenshotVault
{
    public static readonly string Folder = CreateFolder();

    static string CreateFolder()
    {
        try
        {
            var pictures = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Cyclop");
            Directory.CreateDirectory(pictures);
            return pictures;
        }
        catch
        {
            return Support.Directory("Screenshots");
        }
    }

    public static string? Save(byte[] png)
    {
        var stamp = DateTime.Now.ToString(L10n.T("yyyy-MM-dd 'at' HH.mm.ss"), System.Globalization.CultureInfo.InvariantCulture);
        var name = $"{L10n.T("Screenshot")} {stamp}";
        var path = Path.Combine(Folder, name + ".png");
        for (int attempt = 2; File.Exists(path); attempt++) path = Path.Combine(Folder, $"{name} ({attempt}).png");
        try
        {
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception e)
        {
            Support.Log("failed to save image: " + e.Message);
            return null;
        }
    }

    public static void Reveal()
    {
        try { Process.Start("explorer.exe", $"\"{Folder}\""); } catch { }
    }

    public static (int Files, long Bytes) Usage()
    {
        try
        {
            var files = new DirectoryInfo(Folder).GetFiles();
            return (files.Length, files.Sum(f => f.Length));
        }
        catch { return (0, 0); }
    }

    /// <summary>To the Recycle Bin, not gone: the menu item is the user's own
    /// hand, and the bin keeps even that reversible.</summary>
    public static void Clear()
    {
        try
        {
            foreach (var file in Directory.GetFiles(Folder))
                FileSystem.DeleteFile(file, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        }
        catch (Exception e) { Support.Log("clearing screenshots failed: " + e.Message); }
    }
}

/// <summary>
/// Watches a folder — by default <c>Pictures\Screenshots</c>, where Win+Print
/// Screen and the Snipping Tool save — and puts each new picture on the shelf.
/// Off until a folder is chosen in Settings.
/// </summary>
internal sealed class ScreenshotFolderWatcher
{
    static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg" };

    readonly ConfigStore config;
    readonly Dispatcher dispatcher;
    FileSystemWatcher? watcher;

    public Action<string>? OnImage;

    public ScreenshotFolderWatcher(ConfigStore config, Dispatcher dispatcher)
    {
        this.config = config;
        this.dispatcher = dispatcher;
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    public bool IsEnabled => config.ScreenshotFolder is not null;

    public void ResumeIfEnabled()
    {
        Stop();
        var folder = config.ScreenshotFolder;
        if (folder is null || !Directory.Exists(folder)) return;
        try
        {
            watcher = new FileSystemWatcher(folder) { IncludeSubdirectories = false, EnableRaisingEvents = false };
            watcher.Created += (_, e) => Arrived(e.FullPath);
            watcher.Renamed += (_, e) => Arrived(e.FullPath);
            watcher.EnableRaisingEvents = true;
        }
        catch (Exception e) { Support.Log("cannot watch screenshots folder: " + e.Message); }
    }

    public void Enable(string folder)
    {
        config.ScreenshotFolder = folder;
        ResumeIfEnabled();
    }

    public void Disable()
    {
        config.ScreenshotFolder = null;
        Stop();
    }

    public void Stop()
    {
        watcher?.Dispose();
        watcher = null;
    }

    /// <summary>The file is announced as soon as it is created, usually before
    /// the writer has finished — wait until it can be opened.</summary>
    void Arrived(string path)
    {
        if (!Extensions.Contains(Path.GetExtension(path))) return;
        Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    using (File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                    await dispatcher.BeginInvoke(() => OnImage?.Invoke(path));
                    return;
                }
                catch (IOException) { await Task.Delay(150); }
                catch { return; }
            }
        });
    }
}
