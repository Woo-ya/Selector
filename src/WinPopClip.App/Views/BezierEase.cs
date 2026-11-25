using System.Windows;
using System.Windows.Media.Animation;

namespace WinPopClip.App.Views;

/// <summary>
/// CSS cubic-bezier(x1, y1, x2, y2) easing function matching DropShelf's motion curves.
/// Standard UI curve: BezierEase(0.2, 0, 0, 1) — fast launch, smooth decelerating settle.
/// </summary>
public sealed class BezierEase(double x1, double y1, double x2, double y2) : IEasingFunction
{
    public static readonly IEasingFunction Standard = new BezierEase(0.2, 0, 0, 1);

    public double Ease(double t) => Cubic(y1, y2, Solve(x1, x2, t));

    private static double Solve(double p1, double p2, double value)
    {
        double low = 0, high = 1;
        for (var i = 0; i < 30; i++)
        {
            var mid = (low + high) / 2.0;
            if (Cubic(p1, p2, mid) < value) low = mid;
            else high = mid;
        }
        return (low + high) / 2.0;
    }

    private static double Cubic(double p1, double p2, double s) =>
        3 * p1 * s * (1 - s) * (1 - s) + 3 * p2 * s * s * (1 - s) + s * s * s;
}

/// <summary>
/// Centralized motion timing & easing tokens calibrated to DropShelf's tactile feel.
/// Respects Windows OS animation setting (SystemParameters.ClientAreaAnimation).
/// </summary>
public static class SelectorMotion
{
    public static bool Enabled => SystemParameters.ClientAreaAnimation;

    public static readonly TimeSpan PageTransitionDuration = TimeSpan.FromMilliseconds(340);
    public static readonly TimeSpan PageBackDuration = TimeSpan.FromMilliseconds(260);
    public static readonly TimeSpan SegmentedSlideDuration = TimeSpan.FromMilliseconds(220);
    public static readonly TimeSpan SwitchSlideDuration = TimeSpan.FromMilliseconds(160);
    public static readonly TimeSpan ToolbarOpenDuration = TimeSpan.FromMilliseconds(320);
    public static readonly TimeSpan ToolbarFadeInDuration = TimeSpan.FromMilliseconds(180);
    public static readonly TimeSpan ToolbarCloseDuration = TimeSpan.FromMilliseconds(170);

    public static readonly IEasingFunction StandardEase = BezierEase.Standard;
    public static readonly IEasingFunction CubicOutEase = new CubicEase { EasingMode = EasingMode.EaseOut };
    public static readonly IEasingFunction CubicInEase = new CubicEase { EasingMode = EasingMode.EaseIn };
    public static readonly IEasingFunction ToolbarPopEase = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
}
