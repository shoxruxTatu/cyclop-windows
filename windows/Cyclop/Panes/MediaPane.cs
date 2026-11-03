using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Cyclop.App;
using Cyclop.Services;
using Cyclop.UI;

namespace Cyclop.Panes;

internal sealed class MediaPane : Pane
{
    readonly MediaService media;
    readonly Grid nowPlaying;
    readonly UIElement empty;
    readonly Border artworkBox;
    readonly Image artwork;
    readonly TextBlock artworkMark;
    readonly TextBlock title, subtitle, elapsed, total;
    readonly Clickable playPause, previous, next;
    readonly SliderBar scrubber;

    public MediaPane(AppModel model, IPanelHost host) : base(model, host)
    {
        media = model.Media;

        artwork = new Image { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(artwork, BitmapScalingMode.HighQuality);
        artworkMark = Ui.Icon(Glyph.Music, 34, Theme.Tertiary);
        artworkBox = new Border
        {
            Width = 118,
            Height = 118,
            CornerRadius = new CornerRadius(14),
            Background = Theme.Surface,
            BorderBrush = Theme.Hairline,
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Child = new Grid { Children = { artworkMark, artwork } },
        };
        artworkBox.Clip = new RectangleGeometry(new Rect(0, 0, 118, 118), 14, 14);
        // The artwork is the way to the player: a click brings forward the
        // window the sound comes from — the browser with the film in it.
        artworkBox.Cursor = System.Windows.Input.Cursors.Hand;
        artworkBox.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (media.RevealSource()) Host.Close();
        };

        title = Ui.Label("", 16, Theme.White, FontWeights.SemiBold);
        subtitle = Ui.Label("", 11.5, Theme.Secondary);
        subtitle.Margin = new Thickness(0, 3, 0, 0);

        previous = Ui.IconButton(Glyph.Previous, media.Previous, 30, 13);
        playPause = Ui.IconButton(Glyph.Play, media.TogglePlayPause, 40, 17, prominent: true);
        next = Ui.IconButton(Glyph.Next, media.Next, 30, 13);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        previous.Margin = new Thickness(0, 0, 20, 0);
        next.Margin = new Thickness(20, 0, 0, 0);
        controls.Children.Add(previous);
        controls.Children.Add(playPause);
        controls.Children.Add(next);

        elapsed = Ui.Label("0:00", 10, Theme.Tertiary, FontWeights.Medium);
        total = Ui.Label("0:00", 10, Theme.Tertiary, FontWeights.Medium);
        elapsed.Width = total.Width = 34;
        total.TextAlignment = TextAlignment.Right;
        scrubber = new SliderBar(0, 1, 0);
        scrubber.Changing += f => elapsed.Text = Time(f * media.Duration);
        scrubber.Committed += f => media.Seek(f * media.Duration);
        var scrubRow = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(elapsed, Dock.Left);
        DockPanel.SetDock(total, Dock.Right);
        scrubber.Margin = new Thickness(8, 0, 8, 0);
        scrubRow.Children.Add(elapsed);
        scrubRow.Children.Add(total);
        scrubRow.Children.Add(scrubber);

        var column = new Grid { Height = 122, Margin = new Thickness(18, 0, 0, 0) };
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(subtitle, 1);
        Grid.SetRow(controls, 3);
        Grid.SetRow(scrubRow, 5);
        column.Children.Add(title);
        column.Children.Add(subtitle);
        column.Children.Add(controls);
        column.Children.Add(scrubRow);

        nowPlaying = new Grid { VerticalAlignment = VerticalAlignment.Center };
        nowPlaying.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nowPlaying.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(column, 1);
        nowPlaying.Children.Add(artworkBox);
        nowPlaying.Children.Add(column);

        empty = Ui.Empty(Glyph.Music, L10n.T("Nothing is playing"));
        Children.Add(nowPlaying);
        Children.Add(empty);

        media.Changed += Update;
        Update();
    }

    static string Time(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return "--:--";
        int total = (int)Math.Round(seconds);
        return $"{total / 60}:{total % 60:00}";
    }

    void Update()
    {
        HeaderText = media.SourceName;
        artworkBox.ToolTip = string.IsNullOrEmpty(media.SourceName) ? null : L10n.T("Open %@", media.SourceName);
        bool has = media.HasTrack;
        nowPlaying.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        empty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        if (!has || !IsShown && !Model.IsPanelActive) return;

        title.Text = media.DisplayTitle;
        // The system often repeats the title as the album.
        var album = media.Album.Length > 0 && media.Album != media.Title ? media.Album : "";
        subtitle.Text = string.Join(" — ", new[] { media.Artist, album }.Where(s => s.Length > 0));
        // Untitled and from a browser: the source name is already the title,
        // so the line below says which app it is instead of repeating it.
        if (subtitle.Text.Length == 0 && media.DisplayTitle != media.SourceName) subtitle.Text = media.SourceName ?? "";

        if (!ReferenceEquals(artwork.Source, media.Artwork))
        {
            artwork.Source = media.Artwork;
            if (media.Artwork is not null)
            {
                // A square cover fills the box; a 16:9 video frame is fitted,
                // or the edges that say which video it is would be cropped.
                bool square = Math.Abs(media.Artwork.Width / Math.Max(1, media.Artwork.Height) - 1) < 0.02;
                artwork.Stretch = square ? Stretch.UniformToFill : Stretch.Uniform;
                Ui.FadeIn(artwork, 0.28);
            }
        }
        artworkMark.Visibility = media.Artwork is null && !media.ArtworkPending ? Visibility.Visible : Visibility.Collapsed;

        ((TextBlock)playPause.Child).Text = media.IsPlaying ? Glyph.Pause : Glyph.Play;
        // Dimmed, not hidden, when the player does not offer it: dim says
        // "not here", a live-looking button that does nothing says "broken".
        previous.IsEnabled = media.CanSkipPrevious;
        next.IsEnabled = media.CanSkipNext;
        scrubber.IsEnabled = media.CanSeek && media.Duration > 0;

        if (!scrubber.IsDragging)
        {
            scrubber.Value = media.Duration > 0 ? media.Position / media.Duration : 0;
            elapsed.Text = Time(media.Position);
        }
        total.Text = Time(media.Duration);
    }

    protected override void OnShown() => Update();
}
