using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cyclop.Interop;

namespace Cyclop.Services;

/// <summary>
/// Feeds <see cref="ClipboardHistory"/> from the Windows clipboard. Where the
/// Mac polls a change counter, Windows says so outright: a hidden
/// message-only window registered with <c>AddClipboardFormatListener</c>
/// receives <c>WM_CLIPBOARDUPDATE</c> on every change and costs nothing in
/// between.
/// </summary>
internal sealed class ClipboardWatcher
{
    /// <summary>Marks Cyclop's own writes, so copying a screenshot back out of
    /// the shelf does not save it to disk all over again.</summary>
    public const string InternalFormat = "Cyclop.Internal";

    readonly ClipboardHistory history;
    readonly Dispatcher dispatcher;
    HwndSource? source;

    /// <summary>Formats an app sets to keep a copy out of history tools —
    /// what password managers use. Presence is what counts.</summary>
    static readonly uint ExcludeFormat = Native.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    static readonly uint IgnoreFormat = Native.RegisterClipboardFormat("Clipboard Viewer Ignore");
    static readonly uint HistoryFormat = Native.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    static readonly uint OwnFormat = Native.RegisterClipboardFormat(InternalFormat);

    public Func<bool> WantsImages = () => true;
    public Action<byte[]>? OnImage;

    public ClipboardWatcher(ClipboardHistory history, Dispatcher dispatcher)
    {
        this.history = history;
        this.dispatcher = dispatcher;
    }

    public void Start()
    {
        if (source is not null) return;
        var parameters = new HwndSourceParameters("CyclopClipboard")
        {
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
            Width = 0,
            Height = 0,
            WindowStyle = 0,
        };
        source = new HwndSource(parameters);
        source.AddHook(Hook);
        Native.AddClipboardFormatListener(source.Handle);
    }

    public void Stop()
    {
        if (source is null) return;
        Native.RemoveClipboardFormatListener(source.Handle);
        source.RemoveHook(Hook);
        source.Dispose();
        source = null;
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            // Read a moment later, off the notification: the app that copied
            // may still be holding the clipboard open.
            dispatcher.BeginInvoke(DispatcherPriority.Background, Read);
        }
        return IntPtr.Zero;
    }

    void Read()
    {
        if (Native.IsClipboardFormatAvailable(ExcludeFormat) || Native.IsClipboardFormatAvailable(IgnoreFormat)) return;
        if (Native.IsClipboardFormatAvailable(OwnFormat)) return;

        IDataObject? data = null;
        for (int attempt = 0; attempt < 6 && data is null; attempt++)
        {
            try { data = Clipboard.GetDataObject(); }
            catch (COMException) { System.Threading.Thread.Sleep(40); }
        }
        if (data is null) return;

        try
        {
            if (Native.IsClipboardFormatAvailable(HistoryFormat) &&
                data.GetData("CanIncludeInClipboardHistory") is MemoryStream flag &&
                flag.Length >= 4 && BitConverter.ToInt32(flag.ToArray(), 0) == 0)
                return;

            // A copied file arrives as a path, not as image data, so files win first.
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files)
            {
                history.Record(new ClipItem { Kind = ClipKind.File, Value = files[0] });
                return;
            }

            // Win+Shift+S, Print Screen, "Copy image" — an image that would
            // otherwise vanish after one paste.
            if (WantsImages() && data.GetDataPresent(DataFormats.Bitmap) && Clipboard.GetImage() is { } image)
            {
                OnImage?.Invoke(EncodePng(image));
                return;
            }

            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text &&
                !string.IsNullOrWhiteSpace(text))
            {
                history.Record(new ClipItem { Kind = ClipKind.Text, Value = text });
            }
        }
        catch (Exception e)
        {
            Support.Log("clipboard read failed: " + e.Message);
        }
    }

    static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    /// <summary>Puts an entry back on the clipboard without re-recording it.</summary>
    public void Copy(ClipItem item)
    {
        var data = new DataObject();
        data.SetData(InternalFormat, "1");
        if (item.Kind == ClipKind.File)
        {
            data.SetFileDropList(new System.Collections.Specialized.StringCollection { item.Value });
            data.SetText(item.Value);
        }
        else data.SetText(item.Value);
        SetData(data);
        history.MoveToTop(item);
    }

    public static void SetData(DataObject data)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetDataObject(data, true); return; }
            catch (COMException) { System.Threading.Thread.Sleep(40); }
        }
    }
}
