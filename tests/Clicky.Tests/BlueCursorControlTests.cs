using System.Windows;
using Clicky.Overlay;
using Xunit;

namespace Clicky.Tests;

/// <summary>
/// Tests for <see cref="BlueCursorControl"/> — the animated blue cursor
/// that flies to a target point on the overlay.
/// Pure math tests (Bézier evaluation, smoothstep, flight duration, arc height)
/// run without a WPF dispatcher. Visual/animation tests that need WPF run
/// on an STA thread with a try/catch guard for headless CI.
/// </summary>
public class BlueCursorControlTests
{
    // ── Smoothstep easing ────────────────────────────────────────────

    [Fact]
    public void Smoothstep_AtZero_ReturnsZero()
    {
        Assert.Equal(0.0, BlueCursorControl.Smoothstep(0.0), 6);
    }

    [Fact]
    public void Smoothstep_AtOne_ReturnsOne()
    {
        Assert.Equal(1.0, BlueCursorControl.Smoothstep(1.0), 6);
    }

    [Fact]
    public void Smoothstep_AtHalf_ReturnsHalf()
    {
        // 0.5² × (3 − 2×0.5) = 0.25 × 2.0 = 0.5
        Assert.Equal(0.5, BlueCursorControl.Smoothstep(0.5), 6);
    }

    [Fact]
    public void Smoothstep_ClampsNegative()
    {
        Assert.Equal(0.0, BlueCursorControl.Smoothstep(-0.5), 6);
    }

    [Fact]
    public void Smoothstep_ClampsAboveOne()
    {
        Assert.Equal(1.0, BlueCursorControl.Smoothstep(1.5), 6);
    }

    // ── Flight duration ──────────────────────────────────────────────

    [Fact]
    public void FlightDuration_ShortDistance_ClampedToMinimum()
    {
        // distance = 100 → 100/800 = 0.125 → clamped to 0.6
        Assert.Equal(0.6, BlueCursorControl.ComputeFlightDuration(100));
    }

    [Fact]
    public void FlightDuration_LongDistance_ClampedToMaximum()
    {
        // distance = 2000 → 2000/800 = 2.5 → clamped to 1.4
        Assert.Equal(1.4, BlueCursorControl.ComputeFlightDuration(2000));
    }

    [Fact]
    public void FlightDuration_MediumDistance_ProportionalToDistance()
    {
        // distance = 800 → 800/800 = 1.0 (within [0.6, 1.4])
        Assert.Equal(1.0, BlueCursorControl.ComputeFlightDuration(800));
    }

    // ── Arc height ───────────────────────────────────────────────────

    [Fact]
    public void ArcHeight_ShortDistance_ProportionalToDistance()
    {
        // distance = 200 → 200*0.2 = 40 (under 80 cap)
        Assert.Equal(40.0, BlueCursorControl.ComputeArcHeight(200));
    }

    [Fact]
    public void ArcHeight_LongDistance_ClampedAt80()
    {
        // distance = 1000 → 1000*0.2 = 200 → clamped to 80
        Assert.Equal(80.0, BlueCursorControl.ComputeArcHeight(1000));
    }

    // ── Bézier evaluation ────────────────────────────────────────────

    [Fact]
    public void PointingDestination_AppliesMacStyleOffset()
    {
        var destination = BlueCursorControl.ComputePointingDestination(
            new Point(100, 200),
            overlayWidth: 500,
            overlayHeight: 400);

        Assert.Equal(108.0, destination.X, 6);
        Assert.Equal(212.0, destination.Y, 6);
    }

    [Fact]
    public void PointingDestination_ClampsToOverlayPadding()
    {
        var destination = BlueCursorControl.ComputePointingDestination(
            new Point(495, 395),
            overlayWidth: 500,
            overlayHeight: 400);

        Assert.Equal(480.0, destination.X, 6);
        Assert.Equal(380.0, destination.Y, 6);
    }

    [Fact]
    public void PointingDestination_PinpointMode_RemovesArtOffset()
    {
        var destination = BlueCursorControl.ComputePointingDestination(
            new Point(100, 200),
            overlayWidth: 500,
            overlayHeight: 400,
            pinpointMode: true);

        Assert.Equal(100.0, destination.X, 6);
        Assert.Equal(200.0, destination.Y, 6);
    }

    [Fact]
    public void Bezier_AtZero_ReturnsP0()
    {
        // Requires STA thread for WPF object construction
        RunOnSta(() =>
        {
            var control = new BlueCursorControl();
            control.SetBezierPoints(
                new Point(0, 0),
                new Point(50, -30),
                new Point(100, 0));

            var pos = control.EvaluateBezier(0.0);
            Assert.Equal(0.0, pos.X, 6);
            Assert.Equal(0.0, pos.Y, 6);
        });
    }

    [Fact]
    public void Bezier_AtOne_ReturnsP2()
    {
        RunOnSta(() =>
        {
            var control = new BlueCursorControl();
            control.SetBezierPoints(
                new Point(0, 0),
                new Point(50, -30),
                new Point(100, 0));

            var pos = control.EvaluateBezier(1.0);
            Assert.Equal(100.0, pos.X, 6);
            Assert.Equal(0.0, pos.Y, 6);
        });
    }

    [Fact]
    public void Bezier_AtHalf_IsAboveMidpoint()
    {
        RunOnSta(() =>
        {
            var control = new BlueCursorControl();
            control.SetBezierPoints(
                new Point(0, 0),
                new Point(50, -40),
                new Point(100, 0));

            var pos = control.EvaluateBezier(0.5);
            // X should be at midpoint (50)
            Assert.Equal(50.0, pos.X, 6);
            // Y should be negative (above baseline), influenced by control point
            Assert.True(pos.Y < 0, "Midpoint Y should be above baseline (negative)");
        });
    }

    [Fact]
    public void BezierTangent_AtZero_PointsTowardControlPoint()
    {
        RunOnSta(() =>
        {
            var control = new BlueCursorControl();
            control.SetBezierPoints(
                new Point(0, 0),
                new Point(50, -30),
                new Point(100, 0));

            var tangent = control.EvaluateBezierTangent(0.0);
            // Tangent at t=0: 2(P1-P0) = 2(50,-30) = (100,-60)
            Assert.Equal(100.0, tangent.X, 6);
            Assert.Equal(-60.0, tangent.Y, 6);
        });
    }

    [Fact]
    public void BezierTangent_AtOne_PointsFromControlToEnd()
    {
        RunOnSta(() =>
        {
            var control = new BlueCursorControl();
            control.SetBezierPoints(
                new Point(0, 0),
                new Point(50, -30),
                new Point(100, 0));

            var tangent = control.EvaluateBezierTangent(1.0);
            // Tangent at t=1: 2(P2-P1) = 2(50,30) = (100,60)
            Assert.Equal(100.0, tangent.X, 6);
            Assert.Equal(60.0, tangent.Y, 6);
        });
    }

    // ── Visual properties ────────────────────────────────────────────

    [Fact]
    public void AccentColor_Is3380FF()
    {
        Assert.Equal(0x33, BlueCursorControl.AccentColor.R);
        Assert.Equal(0x80, BlueCursorControl.AccentColor.G);
        Assert.Equal(0xFF, BlueCursorControl.AccentColor.B);
    }

    [Fact]
    public void TriangleSize_Is16()
    {
        Assert.Equal(16.0, BlueCursorControl.TriangleSize);
    }

    [Fact]
    public void FollowScale_IsSmallerThanPointingCursor()
    {
        Assert.Equal(0.85, BlueCursorControl.FollowScale);
    }

    [Fact]
    public void FollowPosition_ClampsToOverlayPadding()
    {
        var destination = BlueCursorControl.ClampFollowPosition(
            new Point(495, 395),
            overlayWidth: 500,
            overlayHeight: 400);

        Assert.Equal(480.0, destination.X, 6);
        Assert.Equal(380.0, destination.Y, 6);
    }

    [Fact]
    public void IsVisible_DefaultsFalse()
    {
        RunOnSta(() =>
        {
            var control = new BlueCursorControl();
            Assert.False(control.IsVisible);
        });
    }

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Runs an action on an STA thread (required for WPF objects).</summary>
    private static void RunOnSta(Action action)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                caught = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (caught != null)
        {
            throw new Xunit.Sdk.XunitException(
                $"STA thread threw: {caught.GetType().Name}: {caught.Message}",
                caught);
        }
    }
}
