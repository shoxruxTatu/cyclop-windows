using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cyclop.Interop;

namespace Cyclop.Services;

internal sealed class ShelfItem
{
    public Guid Id { get; } = Guid.NewGuid();
    public string Path { get; init; } = "";
    public ImageSource? Icon { get; set; }
    public bool IsPreview { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// Drop zone contents. Files are referenced, never copied — the shelf is a
/// holding area, so moving the original away removes it from the shelf.
/// </summary>
internal sealed class ShelfStore : Observable
{
    const int Limit = 60;
    static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tif", ".tiff", ".ico", ".webp", ".heic",
    };

    readonly AppState state;
    readonly Dispatcher dispatcher;

    public List<ShelfItem> Items { get; } = new();
    public HashSet<Guid> Selection { get; } = new();

    public ShelfStore(AppState state, Dispatcher dispatcher)
    {
        this.state = state;
        this.dispatcher = dispatcher;
    }

    public void Load()
    {
        Selection.Clear();
        Items.Clear();
        foreach (var path in state.Shelf) Items.Add(new ShelfItem { Path = path });
        Notify();
    }

    /// <summary>Called when the shelf comes into view: missing files leave,
    /// icons and previews arrive.</summary>
    public void RefreshFromDisk()
    {
        int removed = Items.RemoveAll(i => !File.Exists(i.Path) && !Directory.Exists(i.Path));
        if (removed > 0)
        {
            Selection.RemoveWhere(id => Items.All(i => i.Id != id));
            Persist();
        }
        foreach (var item in Items.Where(i => i.Icon is null)) LoadIcon(item);
        Notify();
    }

    public void Add(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            var item = new ShelfItem { Path = path };
            Items.Insert(0, item);
            LoadIcon(item);
        }
        if (Items.Count > Limit) Items.RemoveRange(Limit, Items.Count - Limit);
        Persist();
        Notify();
    }

    public void Remove(ShelfItem item)
    {
        Items.RemoveAll(i => i.Id == item.Id);
        Selection.Remove(item.Id);
        Persist();
        Notify();
    }

    public void Clear()
    {
        Items.Clear();
        Selection.Clear();
        Persist();
        Notify();
    }

    void Persist()
    {
        state.Shelf = Items.Select(i => i.Path).ToList();
        state.Save();
    }

    void LoadIcon(ShelfItem item)
    {
        item.Icon = ShellIcon(item.Path);
        if (!ImageExtensions.Contains(System.IO.Path.GetExtension(item.Path))) return;
        var path = item.Path;
        Task.Run(() =>
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                image.UriSource = new Uri(path);
                image.DecodePixelHeight = 160;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result is not { } preview) return;
            dispatcher.BeginInvoke(() =>
            {
                item.Icon = preview;
                item.IsPreview = true;
                Notify();
            });
        });
    }

    /// <summary>The icon Explorer would show for this file.</summary>
    public static ImageSource? ShellIcon(string path)
    {
        var info = new Native.SHFILEINFO();
        bool exists = File.Exists(path) || Directory.Exists(path);
        var flags = Native.SHGFI_ICON | Native.SHGFI_LARGEICON | (exists ? 0 : Native.SHGFI_USEFILEATTRIBUTES);
        var result = Native.SHGetFileInfo(path, Native.FILE_ATTRIBUTE_NORMAL, ref info,
            (uint)System.Runtime.InteropServices.Marshal.SizeOf(info), flags);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally { Native.DestroyIcon(info.hIcon); }
    }

    // MARK: - Selection

    /// <summary>Plain click replaces the selection; Ctrl or Shift adds to it,
    /// as in Explorer. The clipboard then mirrors whatever is selected — the
    /// shelf exists to hand a file onward, and picking it is the gesture.</summary>
    public void Select(ShelfItem item, bool additive)
    {
        if (additive)
        {
            if (!Selection.Remove(item.Id)) Selection.Add(item.Id);
        }
        else if (Selection.Count == 1 && Selection.Contains(item.Id)) Selection.Clear();
        else
        {
            Selection.Clear();
            Selection.Add(item.Id);
        }
        CopySelection();
        Notify();
    }

    public void ClearSelection()
    {
        Selection.Clear();
        Notify();
    }

    public bool IsSelected(ShelfItem item) => Selection.Contains(item.Id);

    /// <summary>Whole selection when the grabbed card is in it, else that card.</summary>
    public string[] DragPaths(ShelfItem item) =>
        Selection.Contains(item.Id)
            ? Items.Where(i => Selection.Contains(i.Id)).Select(i => i.Path).ToArray()
            : new[] { item.Path };

    public void CopySelection() => Copy(Items.Where(i => Selection.Contains(i.Id)).Select(i => i.Path).ToArray());

    public void Copy(ShelfItem item) => Copy(new[] { item.Path });

    /// <summary>The files, their paths as text, and — for a single picture —
    /// the picture itself, so pasting works in Explorer and in an editor.
    /// A picture rides along only for one card: a reader that finds an image
    /// takes it and looks no further, turning four screenshots into one.</summary>
    static void Copy(string[] paths)
    {
        if (paths.Length == 0) return;
        var data = new DataObject();
        var list = new StringCollection();
        list.AddRange(paths);
        data.SetFileDropList(list);
        data.SetText(string.Join(Environment.NewLine, paths));
        data.SetData(ClipboardWatcher.InternalFormat, "1");
        if (paths.Length == 1 && ImageExtensions.Contains(System.IO.Path.GetExtension(paths[0])))
        {
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(paths[0]);
                image.EndInit();
                image.Freeze();
                data.SetImage(image);
            }
            catch { }
        }
        ClipboardWatcher.SetData(data);
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
    }

    public static void Reveal(string path)
    {
        try { Process.Start("explorer.exe", $"/select,\"{path}\""); } catch { }
    }
}
