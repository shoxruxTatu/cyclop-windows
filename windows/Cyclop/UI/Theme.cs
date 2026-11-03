using System;
using System.Windows;
using System.Windows.Media;

namespace Cyclop.UI;

/// <summary>The palette and the measurements, in one place — the same values
/// as the macOS version's <c>Theme</c>, so the two panels read as one design.</summary>
internal static class Theme
{
    public static readonly Brush Black = Freeze(new SolidColorBrush(Colors.Black));
    public static readonly Brush White = Freeze(new SolidColorBrush(Colors.White));
    public static readonly Brush Secondary = WhiteAlpha(0.55);
    public static readonly Brush Tertiary = WhiteAlpha(0.32);
    public static readonly Brush Surface = WhiteAlpha(0.08);
    public static readonly Brush SurfaceHover = WhiteAlpha(0.14);
    public static readonly Brush Hairline = WhiteAlpha(0.10);
    public static readonly Brush Clear = Freeze(new SolidColorBrush(Colors.Transparent));
    public static readonly Brush Warning = Freeze(new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xD6, 0x0A)));
    public static readonly Brush Accent = Freeze(new SolidColorBrush(AccentColor()));
    public static readonly Brush AccentSoft = Freeze(new SolidColorBrush(Color.FromArgb(0x66, AccentColor().R, AccentColor().G, AccentColor().B)));

    /// <summary>Calendar colours, one per subscribed feed, in order.</summary>
    public static readonly Brush[] FeedColors =
    {
        Hex("#0A84FF"), Hex("#30D158"), Hex("#FF9F0A"), Hex("#BF5AF2"), Hex("#FF453A"), Hex("#64D2FF"),
    };

    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    /// <summary>Fluent Icons on Windows 11, MDL2 Assets on Windows 10 — the
    /// codepoints below exist in both.</summary>
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // Measurements, in device-independent pixels.
    public const double BodyWidth = 680;
    public const double HeaderHeight = 30;
    public const double StandardContentHeight = 208;
    public const double TallContentHeight = 400;
    public const double BottomPadding = 14;
    public const double OpenShoulder = 12;
    public const double OpenBottomRadius = 22;
    public const double StripWidth = 190;
    public const double StripHeight = 6;
    public const double StripRadius = 4;
    public const double ShadowPad = 32;
    public const double RailWidth = 30;
    public const double RailSpacing = 4;

    public static double BodyHeight(bool tall) =>
        HeaderHeight + (tall ? TallContentHeight : StandardContentHeight) + BottomPadding;

    static Brush WhiteAlpha(double alpha) =>
        Freeze(new SolidColorBrush(Color.FromArgb((byte)Math.Round(alpha * 255), 255, 255, 255)));

    public static Brush Hex(string hex) =>
        Freeze(new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)));

    static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>The accent the user picked in Windows settings.</summary>
    static Color AccentColor()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.AccentLight1);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }
        catch
        {
            return Color.FromRgb(0x0A, 0x84, 0xFF);
        }
    }
}

/// <summary>Segoe MDL2 / Fluent codepoints.</summary>
internal static class Glyph
{
    public const string Music = "";
    public const string Shelf = "";
    public const string Clipboard = "";
    public const string Pin = "";
    public const string Calendar = "";
    public const string Globe = "";
    public const string Money = "";
    public const string Note = "";
    public const string Teleprompter = "";
    public const string Wrench = "";
    public const string Settings = "";

    public const string Play = "";
    public const string Pause = "";
    public const string Previous = "";
    public const string Next = "";
    public const string Add = "";
    public const string Close = "";
    public const string Delete = "";
    public const string Edit = "";
    public const string Copy = "";
    public const string Link = "";
    public const string Mail = "";
    public const string Phone = "";
    public const string Text = "";
    public const string Document = "";
    public const string Folder = "";
    public const string Search = "";
    public const string Swap = "";
    public const string Refresh = "";
    public const string Eye = "";
    public const string Keyboard = "";
    public const string Warning = "";
    public const string Open = "";
    public const string Power = "";
    public const string Photo = "";
    public const string Monitor = "";
    public const string Timer = "";
    public const string Rewind = "";
    public const string FontSize = "";
    public const string Video = "";
    public const string Up = "";
    public const string Down = "";
    public const string Person = "\uE77B";
    public const string Crop = "\uE7A8";
    public const string Camera = "\uE722";
    public const string Heart = "\uEB51";

    public static string For(Tab tab) => tab switch
    {
        Tab.Media => Music,
        Tab.Shelf => Shelf,
        Tab.Clipboard => Clipboard,
        Tab.Snippets => Pin,
        Tab.Calendar => Calendar,
        Tab.Translate => Globe,
        Tab.Currency => Money,
        Tab.Notes => Note,
        Tab.Teleprompter => Teleprompter,
        Tab.Utilities => Wrench,
        _ => Settings,
    };
}
