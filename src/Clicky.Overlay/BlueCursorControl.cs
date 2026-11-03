using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace Clicky.Overlay;

/// <summary>
/// A blue glowing triangle cursor with speech bubble that follows the OS cursor
/// and can fly to a target point.
/// Mirrors <c>BlueCursorView</c> from the Mac reference (OverlayWindow.swift).
///
/// The triangle is 16×16 DIPs, colored #3380FF with a glow (DropShadowEffect).
/// FlyTo animates along a quadratic Bézier arc with smoothstep easing,
/// scale pulsing, and rotation toward the direction of travel.
/// After arrival, a speech bubble pops in and the cursor lingers ~3 s before fading out.
/// </summary>
public sealed class BlueCursorControl : IDisposable
{
    // ── Constants matching Mac reference ──────────────────────────────

    /// <summary>Triangle size in DIPs (matches Mac 16×16 pt).</summary>
    internal const double TriangleSize = 16.0;
    internal const double FollowScale = 0.85;

    /// <summary>Blue accent color #3380FF.</summary>
    internal static readonly Color AccentColor = Color.FromRgb(0x33, 0x80, 0xFF);

    /// <summary>Static glow blur radius.</summary>
    private const double BaseGlowRadius = 8.0;

    /// <summary>Offset from cursor position when following (right, down).</summary>
    private const double FollowOffsetX = 35.0;
    private const double FollowOffsetY = 25.0;

    /// <summary>Rest rotation angle in degrees (cursor-like orientation).</summary>
    private const double RestAngle = -35.0;
    private const double PointingOffsetX = 8.0;
    private const double PointingOffsetY = 12.0;
    private const double EdgePadding = 20.0;

    /// <summary>Duration the cursor lingers at the target before fading.</summary>
    private const double LingerSeconds = 3.0;

    /// <summary>Duration of the fade-out animation.</summary>
    private const double FadeOutSeconds = 0.5;

    /// <summary>Random pointer phrases, matching Mac reference.</summary>
    private static readonly string[] PointerPhrases =
    {
        "right here!", "this one!", "over here!",
        "click this!", "here it is!", "found it!"
    };

    private static readonly Random s_random = new();

    // ── Visual elements ──────────────────────────────────────────────

    private readonly Canvas _canvas;
    private readonly Polygon _triangle;
    private readonly DropShadowEffect _triangleGlow;
    private readonly Border _bubbleBorder;
    private readonly TextBlock _bubbleText;
    private readonly DropShadowEffect _bubbleGlow;

    // Transforms applied to the triangle
    private readonly TranslateTransform _translateTransform;
    private readonly RotateTransform _rotateTransform;
    private readonly ScaleTransform _scaleTransform;
    private readonly TransformGroup _transformGroup;

    // Bubble transforms
    private readonly TranslateTransform _bubbleTranslate;
    private readonly ScaleTransform _bubbleScale;
    private readonly TransformGroup _bubbleTransformGroup;

    // ── Animation state ──────────────────────────────────────────────

    private DispatcherTimer? _flightTimer;
    private DispatcherTimer? _lingerTimer;
    private DispatcherTimer? _followTimer;
    private DispatcherTimer? _pointingCompletionFallbackTimer;
    private DateTime _flightStartTime;
    private double _flightDurationSeconds;
    private System.Drawing.Rectangle _followOverlayBounds;
    private double _followDpiScaleX = 1.0;
    private double _followDpiScaleY = 1.0;

    // Bézier control points (in overlay-local coordinates)
    private Point _bezierP0; // start
    private Point _bezierP1; // control (arc apex)
    private Point _bezierP2; // end (target)

    private bool _isVisible;
    private bool _isPointing;
    private bool _followingRequested;
    private bool _followingSuspended;
    private bool _disposed;
    private double _currentLingerSeconds = LingerSeconds;
    private double _currentFadeOutSeconds = FadeOutSeconds;

    public BlueCursorControl()
    {
        // Build the triangle shape (equilateral, pointing up)
        var height = TriangleSize * Math.Sqrt(3.0) / 2.0;
        var midX = TriangleSize / 2.0;
        var midY = TriangleSize / 2.0;

        _triangle = new Polygon
        {
            Points = new PointCollection
            {
                new Point(midX, midY - height / 1.5),           // top
                new Point(midX - TriangleSize / 2.0, midY + height / 3.0), // bottom-left
                new Point(midX + TriangleSize / 2.0, midY + height / 3.0), // bottom-right
            },
            Fill = new SolidColorBrush(AccentColor),
            StrokeThickness = 0
        };

        _triangleGlow = new DropShadowEffect
        {
            Color = AccentColor,
            BlurRadius = BaseGlowRadius,
            ShadowDepth = 0,
            Opacity = 1.0
        };
        _triangle.Effect = _triangleGlow;

        // Transforms: translate → rotate → scale (all around triangle center)
        _translateTransform = new TranslateTransform(0, 0);
        _rotateTransform = new RotateTransform(RestAngle, midX, midY);
        _scaleTransform = new ScaleTransform(1.0, 1.0, midX, midY);
        _transformGroup = new TransformGroup();
        _transformGroup.Children.Add(_scaleTransform);
        _transformGroup.Children.Add(_rotateTransform);
        _transformGroup.Children.Add(_translateTransform);
        _triangle.RenderTransform = _transformGroup;

        // Speech bubble
        _bubbleText = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.Medium,
            Text = ""
        };

        _bubbleGlow = new DropShadowEffect
        {
            Color = AccentColor,
            BlurRadius = 6,
            ShadowDepth = 0,
            Opacity = 0.5
        };

        _bubbleBorder = new Border
        {
            Background = new SolidColorBrush(AccentColor),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Child = _bubbleText,
            Effect = _bubbleGlow,
            Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0, 0.5)
        };

        _bubbleTranslate = new TranslateTransform(0, 0);
        _bubbleScale = new ScaleTransform(1, 1);
        _bubbleTransformGroup = new TransformGroup();
        _bubbleTransformGroup.Children.Add(_bubbleScale);
        _bubbleTransformGroup.Children.Add(_bubbleTranslate);
        _bubbleBorder.RenderTransform = _bubbleTransformGroup;

        // Canvas to host both
        _canvas = new Canvas
        {
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        _canvas.Children.Add(_triangle);
        _canvas.Children.Add(_bubbleBorder);
    }

    /// <summary>The root UIElement to add to the overlay window.</summary>
    public UIElement Visual => _canvas;

    /// <summary>Whether the cursor is currently visible (flying or lingering).</summary>
    public bool IsVisible => _isVisible;

    /// <summary>Raised after a point animation finishes and the control is ready to follow again.</summary>
    public event EventHandler? PointingCompleted;

    /// <summary>Starts the idle cursor-following mode for this overlay.</summary>
    public void StartFollowing(
        System.Drawing.Rectangle overlayBounds,
        double dpiScaleX,
        double dpiScaleY)
    {
        _followOverlayBounds = overlayBounds;
        _followDpiScaleX = dpiScaleX;
        _followDpiScaleY = dpiScaleY;
        _followingRequested = true;
        _followingSuspended = false;

        if (!_isPointing)
        {
            EnsureFollowTimer();
            UpdateFollowPosition();
        }
    }

    /// <summary>Temporarily hides the cursor and stops tracking the OS cursor.</summary>
    public void SuspendFollowing()
    {
        _followingSuspended = true;
        StopFollowTimer();
        StopAllAnimations();
        _isPointing = false;
        _bubbleBorder.Visibility = Visibility.Collapsed;
        _canvas.Visibility = Visibility.Collapsed;
        _canvas.Opacity = 1.0;
        _isVisible = false;
    }

    /// <summary>Resumes cursor-following after a temporary suspension.</summary>
    public void ResumeFollowing()
    {
        if (!_followingRequested)
            return;

        _followingSuspended = false;
        if (!_isPointing)
        {
            EnsureFollowTimer();
            UpdateFollowPosition();
        }
    }

    /// <summary>
    /// Animates the blue cursor from the current OS cursor position to <paramref name="targetPoint"/>
    /// on the overlay whose bounds contain the target, showing <paramref name="bubbleText"/> on arrival.
    /// Coordinates are in global physical pixel space and will be converted to overlay-local DIPs.
    /// </summary>
    /// <param name="targetPoint">Target point in global physical desktop pixels.</param>
    /// <param name="overlayBounds">The monitor bounds of the overlay (physical pixels).</param>
    /// <param name="dpiScaleX">DPI scale factor for the monitor (1.0 at 100%, 1.5 at 150%).</param>
    /// <param name="dpiScaleY">DPI scale factor for the monitor (1.0 at 100%, 1.5 at 150%).</param>
    /// <param name="bubbleText">Text to show in the speech bubble (if null/empty, a random phrase is picked).</param>
    public void FlyTo(
        Point targetPoint,
        System.Drawing.Rectangle overlayBounds,
        double dpiScaleX,
        double dpiScaleY,
        string? bubbleText,
        bool pinpointMode = false,
        Action<string>? logger = null,
        Point? startPointOverride = null,
        double? lingerSeconds = null,
        double? fadeOutSeconds = null)
    {
        // Cancel any in-progress animation
        StopAllAnimations();
        StopFollowTimer();
        _isPointing = true;
        _currentLingerSeconds = lingerSeconds ?? LingerSeconds;
        _currentFadeOutSeconds = fadeOutSeconds ?? FadeOutSeconds;

        // Determine start point: current OS cursor position (physical pixels)
        var startGlobalPhysical = startPointOverride ?? GetCursorStartPoint();

        // Convert from physical pixels to overlay-local DIPs:
        // 1. Subtract the physical monitor origin to get monitor-local physical coords
        // 2. Divide by DPI scale to get DIPs
        var startLocal = new Point(
            (startGlobalPhysical.X - overlayBounds.X) / dpiScaleX,
            (startGlobalPhysical.Y - overlayBounds.Y) / dpiScaleY);
        var rawEndLocal = new Point(
            (targetPoint.X - overlayBounds.X) / dpiScaleX,
            (targetPoint.Y - overlayBounds.Y) / dpiScaleY);
        var overlayWidth = overlayBounds.Width / dpiScaleX;
        var overlayHeight = overlayBounds.Height / dpiScaleY;
        var endLocal = ComputePointingDestination(rawEndLocal, overlayWidth, overlayHeight, pinpointMode);

        logger?.Invoke(
            $"[POINT] cursor: startPhysical=({startGlobalPhysical.X:F1},{startGlobalPhysical.Y:F1}) " +
            $"targetPhysical=({targetPoint.X:F1},{targetPoint.Y:F1}) rawTargetLocal=({rawEndLocal.X:F2},{rawEndLocal.Y:F2}) " +
            $"finalLocal=({endLocal.X:F2},{endLocal.Y:F2}) overlayDip={overlayWidth:F2}x{overlayHeight:F2} " +
            $"dpiScale=({dpiScaleX:F3},{dpiScaleY:F3}) pinpoint={pinpointMode}");

        // Compute Bézier arc: P0=start, P2=end, P1=control point (offset upward)
        var dx = endLocal.X - startLocal.X;
        var dy = endLocal.Y - startLocal.Y;
        var distance = Math.Sqrt(dx * dx + dy * dy);

        var arcHeight = Math.Min(distance * 0.2, 80.0);
        var midPoint = new Point(
            (startLocal.X + endLocal.X) / 2.0,
            (startLocal.Y + endLocal.Y) / 2.0 - arcHeight);

        _bezierP0 = startLocal;
        _bezierP1 = midPoint;
        _bezierP2 = endLocal;

        // Flight duration scales with distance: min 0.6s, max 1.4s
        _flightDurationSeconds = Math.Min(Math.Max(distance / 800.0, 0.6), 1.4);

        // Set bubble text
        if (string.IsNullOrEmpty(bubbleText))
        {
            bubbleText = PointerPhrases[s_random.Next(PointerPhrases.Length)];
        }
        _bubbleText.Text = bubbleText;
        _bubbleBorder.Visibility = Visibility.Collapsed;

        // Show the cursor at start position
        SetPosition(startLocal, RestAngle, 1.0);
        _canvas.Visibility = Visibility.Visible;
        _canvas.Opacity = 1.0;
        _isVisible = true;

        // Start flight animation at ~60 FPS
        _flightStartTime = DateTime.UtcNow;
        _flightTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 60.0)
        };
        _flightTimer.Tick += OnFlightTick;
        _flightTimer.Start();
    }

    private static Point GetCursorStartPoint()
    {
        NativeMethods.GetCursorPos(out var cursorPos);
        return new Point(cursorPos.X + FollowOffsetX, cursorPos.Y + FollowOffsetY);
    }

    /// <summary>
    /// Immediately hides the cursor and cancels all animations.
    /// </summary>
    public void Hide()
    {
        _followingRequested = false;
        _followingSuspended = false;
        StopFollowTimer();
        StopAllAnimations();
        _isPointing = false;
        _bubbleBorder.Visibility = Visibility.Collapsed;
        _canvas.Visibility = Visibility.Collapsed;
        _canvas.Opacity = 1.0;
        _isVisible = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopFollowTimer();
        StopAllAnimations();
    }

    // ── Animation tick handlers ──────────────────────────────────────

    private void OnFlightTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.UtcNow - _flightStartTime).TotalSeconds;
        var linearProgress = Math.Min(elapsed / _flightDurationSeconds, 1.0);

        // Smoothstep ease-in-out: t² × (3 − 2t)
        var t = Smoothstep(linearProgress);

        // Quadratic Bézier: B(t) = (1−t)²P0 + 2(1−t)tP1 + t²P2
        var pos = EvaluateBezier(t);

        // Tangent for rotation: B'(t) = 2(1−t)(P1−P0) + 2t(P2−P1)
        var tangent = EvaluateBezierTangent(t);
        var angle = Math.Atan2(tangent.Y, tangent.X) * (180.0 / Math.PI) + 90.0;

        // Scale pulse: 1.0 + sin(π·progress) × 0.3
        var scale = 1.0 + Math.Sin(linearProgress * Math.PI) * 0.3;

        // Dynamic glow: 8 + (scale − 1.0) × 20
        _triangleGlow.BlurRadius = BaseGlowRadius + (scale - 1.0) * 20.0;

        SetPosition(pos, angle, scale);

        if (linearProgress >= 1.0)
        {
            // Flight complete — snap to target and show bubble
            _flightTimer!.Stop();
            _flightTimer.Tick -= OnFlightTick;
            _flightTimer = null;

            SetPosition(_bezierP2, RestAngle, 1.0);
            _triangleGlow.BlurRadius = BaseGlowRadius;

            ShowBubble();
            StartLinger();
        }
    }

    private void ShowBubble()
    {
        // Position bubble offset from triangle
        _bubbleTranslate.X = _translateTransform.X + 10;
        _bubbleTranslate.Y = _translateTransform.Y + 18;

        // Pop-in: start at 0.5× scale, animate to 1.0×
        _bubbleScale.ScaleX = 0.5;
        _bubbleScale.ScaleY = 0.5;
        _bubbleBorder.Visibility = Visibility.Visible;

        // Animate bubble scale with spring-like ease (approximated with DecelerateEase)
        var scaleXAnim = new DoubleAnimation(0.5, 1.0, TimeSpan.FromSeconds(0.4))
        {
            EasingFunction = new ElasticEase
            {
                Oscillations = 1,
                Springiness = 8,
                EasingMode = EasingMode.EaseOut
            }
        };
        var scaleYAnim = scaleXAnim.Clone();

        _bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, scaleXAnim);
        _bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, scaleYAnim);

        // Bubble glow pop
        _bubbleGlow.BlurRadius = 22;
        _bubbleGlow.Opacity = 1.5;
        var glowBlurAnim = new DoubleAnimation(22, 6, TimeSpan.FromSeconds(0.4));
        var glowOpacityAnim = new DoubleAnimation(1.0, 0.5, TimeSpan.FromSeconds(0.4));
        _bubbleGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, glowBlurAnim);
        _bubbleGlow.BeginAnimation(DropShadowEffect.OpacityProperty, glowOpacityAnim);
    }

    private void StartLinger()
    {
        _lingerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_currentLingerSeconds)
        };
        _lingerTimer.Tick += OnLingerComplete;
        _lingerTimer.Start();
    }

    private void OnLingerComplete(object? sender, EventArgs e)
    {
        _lingerTimer!.Stop();
        _lingerTimer.Tick -= OnLingerComplete;
        _lingerTimer = null;

        // Fade out the entire canvas
        var fadeOut = new DoubleAnimation(1.0, 0.0, TimeSpan.FromSeconds(_currentFadeOutSeconds))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
        };
        fadeOut.Completed += (_, _) => CompletePointingAndResumeFollowing();
        StartPointingCompletionFallbackTimer();
        _canvas.BeginAnimation(UIElement.OpacityProperty, fadeOut);
    }

    private void CompletePointingAndResumeFollowing()
    {
        StopPointingCompletionFallbackTimer();

        if (!_isPointing)
            return;

        _canvas.Visibility = Visibility.Collapsed;
        _canvas.Opacity = 1.0;
        _isVisible = false;
        _isPointing = false;
        _bubbleBorder.Visibility = Visibility.Collapsed;
        PointingCompleted?.Invoke(this, EventArgs.Empty);

        if (_followingRequested && !_followingSuspended)
        {
            EnsureFollowTimer();
            UpdateFollowPosition();
        }
    }

    private void StartPointingCompletionFallbackTimer()
    {
        StopPointingCompletionFallbackTimer();
        _pointingCompletionFallbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(_currentFadeOutSeconds + 0.1)
        };
        _pointingCompletionFallbackTimer.Tick += OnPointingCompletionFallback;
        _pointingCompletionFallbackTimer.Start();
    }

    private void StopPointingCompletionFallbackTimer()
    {
        if (_pointingCompletionFallbackTimer is null)
            return;

        _pointingCompletionFallbackTimer.Stop();
        _pointingCompletionFallbackTimer.Tick -= OnPointingCompletionFallback;
        _pointingCompletionFallbackTimer = null;
    }

    private void OnPointingCompletionFallback(object? sender, EventArgs e)
    {
        CompletePointingAndResumeFollowing();
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private void SetPosition(Point pos, double angleDeg, double scale)
    {
        _translateTransform.X = pos.X;
        _translateTransform.Y = pos.Y;
        _rotateTransform.Angle = angleDeg;
        _scaleTransform.ScaleX = scale;
        _scaleTransform.ScaleY = scale;
    }

    private void EnsureFollowTimer()
    {
        if (_followTimer is not null)
            return;

        _followTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromSeconds(1.0 / 60.0)
        };
        _followTimer.Tick += OnFollowTick;
        _followTimer.Start();
    }

    private void StopFollowTimer()
    {
        if (_followTimer is null)
            return;

        _followTimer.Stop();
        _followTimer.Tick -= OnFollowTick;
        _followTimer = null;
    }

    private void OnFollowTick(object? sender, EventArgs e)
    {
        UpdateFollowPosition();
    }

    private void UpdateFollowPosition()
    {
        if (!_followingRequested || _followingSuspended || _isPointing)
            return;

        NativeMethods.GetCursorPos(out var cursorPos);
        if (!_followOverlayBounds.Contains(new System.Drawing.Point(cursorPos.X, cursorPos.Y)))
        {
            _canvas.Visibility = Visibility.Collapsed;
            _isVisible = false;
            return;
        }

        var local = new Point(
            (cursorPos.X + FollowOffsetX - _followOverlayBounds.X) / _followDpiScaleX,
            (cursorPos.Y + FollowOffsetY - _followOverlayBounds.Y) / _followDpiScaleY);
        var overlayWidth = _followOverlayBounds.Width / _followDpiScaleX;
        var overlayHeight = _followOverlayBounds.Height / _followDpiScaleY;

        _bubbleBorder.Visibility = Visibility.Collapsed;
        _triangleGlow.BlurRadius = BaseGlowRadius;
        SetPosition(ClampFollowPosition(local, overlayWidth, overlayHeight), RestAngle, FollowScale);
        _canvas.Opacity = 1.0;
        _canvas.Visibility = Visibility.Visible;
        _isVisible = true;
    }

    private void StopAllAnimations()
    {
        if (_flightTimer != null)
        {
            _flightTimer.Stop();
            _flightTimer.Tick -= OnFlightTick;
            _flightTimer = null;
        }

        if (_lingerTimer != null)
        {
            _lingerTimer.Stop();
            _lingerTimer.Tick -= OnLingerComplete;
            _lingerTimer = null;
        }

        StopPointingCompletionFallbackTimer();

        // Clear any WPF storyboard animations
        _canvas.BeginAnimation(UIElement.OpacityProperty, null);
        _bubbleScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _bubbleScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _bubbleGlow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
        _bubbleGlow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
    }

    /// <summary>Smoothstep easing: t² × (3 − 2t).</summary>
    internal static double Smoothstep(double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return t * t * (3.0 - 2.0 * t);
    }

    /// <summary>Evaluates the quadratic Bézier curve at parameter t.</summary>
    internal Point EvaluateBezier(double t)
    {
        var omt = 1.0 - t;
        return new Point(
            omt * omt * _bezierP0.X + 2 * omt * t * _bezierP1.X + t * t * _bezierP2.X,
            omt * omt * _bezierP0.Y + 2 * omt * t * _bezierP1.Y + t * t * _bezierP2.Y);
    }

    /// <summary>Evaluates the tangent (first derivative) of the quadratic Bézier at parameter t.</summary>
    internal Point EvaluateBezierTangent(double t)
    {
        var omt = 1.0 - t;
        return new Point(
            2 * omt * (_bezierP1.X - _bezierP0.X) + 2 * t * (_bezierP2.X - _bezierP1.X),
            2 * omt * (_bezierP1.Y - _bezierP0.Y) + 2 * t * (_bezierP2.Y - _bezierP1.Y));
    }

    /// <summary>
    /// Computes the flight duration for a given distance (exposed for testing).
    /// </summary>
    internal static double ComputeFlightDuration(double distance)
    {
        return Math.Min(Math.Max(distance / 800.0, 0.6), 1.4);
    }

    /// <summary>
    /// Computes the arc height for a given distance (exposed for testing).
    /// </summary>
    internal static double ComputeArcHeight(double distance)
    {
        return Math.Min(distance * 0.2, 80.0);
    }

    /// <summary>
    /// Sets the Bézier control points directly (for testing without calling FlyTo).
    /// </summary>
    internal static Point ComputePointingDestination(Point targetLocal, double overlayWidth, double overlayHeight, bool pinpointMode = false)
    {
        var minX = EdgePadding;
        var minY = EdgePadding;
        var maxX = Math.Max(minX, overlayWidth - EdgePadding);
        var maxY = Math.Max(minY, overlayHeight - EdgePadding);
        var offsetX = pinpointMode ? 0.0 : PointingOffsetX;
        var offsetY = pinpointMode ? 0.0 : PointingOffsetY;

        return new Point(
            Math.Clamp(targetLocal.X + offsetX, minX, maxX),
            Math.Clamp(targetLocal.Y + offsetY, minY, maxY));
    }

    internal static Point ClampFollowPosition(Point local, double overlayWidth, double overlayHeight)
    {
        var minX = EdgePadding;
        var minY = EdgePadding;
        var maxX = Math.Max(minX, overlayWidth - EdgePadding);
        var maxY = Math.Max(minY, overlayHeight - EdgePadding);

        return new Point(
            Math.Clamp(local.X, minX, maxX),
            Math.Clamp(local.Y, minY, maxY));
    }

    internal void SetBezierPoints(Point p0, Point p1, Point p2)
    {
        _bezierP0 = p0;
        _bezierP1 = p1;
        _bezierP2 = p2;
    }
}
