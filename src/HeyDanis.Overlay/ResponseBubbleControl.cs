using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace HeyDanis.Overlay;

/// <summary>
/// The speech bubble next to the mouse that shows heyDanis' answer as text, so the
/// user can read it even with the sound off (or when the speakers are wrong).
/// Lives on the click-through overlay, never takes focus, fades out on its own.
/// </summary>
public sealed class ResponseBubbleControl
{
    private const double MaxBubbleWidth = 380;
    private const double OffsetFromCursor = 24;

    private readonly Border _bubble;
    private readonly TextBlock _text;
    private readonly DispatcherTimer _hideTimer;

    public ResponseBubbleControl()
    {
        _text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            MaxWidth = MaxBubbleWidth,
        };

        _bubble = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1C, 0x1C, 0x1E)),
            BorderBrush = new SolidColorBrush(BuddyCursorControl.AccentColor),
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 10, 14, 10),
            Child = _text,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45, Color = Colors.Black },
        };

        _hideTimer = new DispatcherTimer();
        _hideTimer.Tick += (_, _) => FadeOut();
    }

    /// <summary>The element to add to the overlay canvas.</summary>
    public UIElement Visual => _bubble;

    public bool IsVisible => _bubble.Visibility == Visibility.Visible;

    /// <summary>
    /// Shows <paramref name="text"/> near <paramref name="anchorInOverlayDips"/>, flipping to the
    /// other side when the bubble would leave the monitor.
    /// </summary>
    public void Show(string text, Point anchorInOverlayDips, Size overlaySizeDips)
    {
        _text.Text = text;
        _bubble.BeginAnimation(UIElement.OpacityProperty, null);
        _bubble.Opacity = 1;
        _bubble.Visibility = Visibility.Visible;
        _bubble.Measure(new Size(MaxBubbleWidth + 40, double.PositiveInfinity));
        var bubbleSize = _bubble.DesiredSize;

        var left = anchorInOverlayDips.X + OffsetFromCursor;
        if (left + bubbleSize.Width > overlaySizeDips.Width - 8)
            left = anchorInOverlayDips.X - OffsetFromCursor - bubbleSize.Width;
        var top = anchorInOverlayDips.Y + OffsetFromCursor;
        if (top + bubbleSize.Height > overlaySizeDips.Height - 8)
            top = anchorInOverlayDips.Y - OffsetFromCursor - bubbleSize.Height;

        Canvas.SetLeft(_bubble, Math.Max(8, left));
        Canvas.SetTop(_bubble, Math.Max(8, top));

        _hideTimer.Stop();
        _hideTimer.Interval = ReadingTimeFor(text);
        _hideTimer.Start();
    }

    /// <summary>Roughly how long a person needs to read the text, plus a buffer.</summary>
    internal static TimeSpan ReadingTimeFor(string text)
    {
        var seconds = 4.0 + text.Length * 0.06;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 30));
    }

    public void Hide()
    {
        _hideTimer.Stop();
        _bubble.BeginAnimation(UIElement.OpacityProperty, null);
        _bubble.Visibility = Visibility.Collapsed;
    }

    private void FadeOut()
    {
        _hideTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(400));
        fade.Completed += (_, _) =>
        {
            if (_bubble.Opacity <= 0.01)
                _bubble.Visibility = Visibility.Collapsed;
        };
        _bubble.BeginAnimation(UIElement.OpacityProperty, fade);
    }
}
