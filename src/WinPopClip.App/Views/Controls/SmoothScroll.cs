using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WinPopClip.App.Views.Controls;

/// <summary>
/// Provides buttery 60fps damped mouse-wheel scrolling for WPF ScrollViewers while respecting
/// user scrollbar drags and reduced-motion preferences.
/// </summary>
public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(SmoothScroll),
            new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty StateProperty =
        DependencyProperty.RegisterAttached(
            "State",
            typeof(ScrollState),
            typeof(SmoothScroll));

    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;

        if ((bool)e.NewValue)
        {
            var state = new ScrollState(sv);
            sv.SetValue(StateProperty, state);
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
            sv.Unloaded += OnUnloaded;
        }
        else
        {
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
            sv.Unloaded -= OnUnloaded;
            if (sv.GetValue(StateProperty) is ScrollState oldState)
            {
                oldState.Stop();
                sv.ClearValue(StateProperty);
            }
        }
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer sv && sv.GetValue(StateProperty) is ScrollState state)
        {
            state.Stop();
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;

        // Don't hijack scrolling if the cursor is inside a nested scrollable control (e.g., multiline TextBox)
        if (IsInsideNestedScrollable(e.OriginalSource as DependencyObject, sv))
        {
            return;
        }

        // Scale standard 120-unit wheel notch to ~88px (supports high-res trackpads proportionally too)
        var deltaPx = -(e.Delta / 120.0) * 88.0;
        e.Handled = true;

        if (!SelectorMotion.Enabled)
        {
            sv.ScrollToVerticalOffset(Math.Clamp(sv.VerticalOffset + deltaPx, 0, sv.ScrollableHeight));
            return;
        }

        if (sv.GetValue(StateProperty) is ScrollState state)
        {
            state.AddDelta(deltaPx);
        }
    }

    private static bool IsInsideNestedScrollable(DependencyObject? source, ScrollViewer outer)
    {
        var cur = source;
        while (cur is not null && !ReferenceEquals(cur, outer))
        {
            if (cur is ScrollViewer inner && inner.ScrollableHeight > 0)
            {
                return true;
            }
            cur = cur is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(cur)
                : LogicalTreeHelper.GetParent(cur);
        }
        return false;
    }

    private sealed class ScrollState
    {
        private readonly ScrollViewer _sv;
        private double _targetOffset;
        private double _currentOffset;
        private long _lastTick;
        private bool _running;

        public ScrollState(ScrollViewer sv) => _sv = sv;

        public void AddDelta(double deltaPx)
        {
            if (!_running)
            {
                _currentOffset = _sv.VerticalOffset;
                _targetOffset = _currentOffset;
            }
            else if (Math.Abs(_sv.VerticalOffset - _currentOffset) > 2.0)
            {
                // User dragged scrollbar thumb manually — resync smoothly
                _currentOffset = _sv.VerticalOffset;
                _targetOffset = _currentOffset;
            }

            _targetOffset = Math.Clamp(_targetOffset + deltaPx, 0, _sv.ScrollableHeight);

            if (!_running)
            {
                _running = true;
                _lastTick = Stopwatch.GetTimestamp();
                CompositionTarget.Rendering += OnRender;
            }
        }

        private void OnRender(object? sender, EventArgs e)
        {
            var now = Stopwatch.GetTimestamp();
            var dt = Math.Clamp((now - _lastTick) / (double)Stopwatch.Frequency, 0.001, 0.05);
            _lastTick = now;

            _targetOffset = Math.Clamp(_targetOffset, 0, _sv.ScrollableHeight);
            var diff = _targetOffset - _currentOffset;
            if (Math.Abs(diff) < 0.45)
            {
                _currentOffset = _targetOffset;
                _sv.ScrollToVerticalOffset(_currentOffset);
                Stop();
                return;
            }

            // Critically damped exponential ease (~140ms feel, frame-rate independent)
            var factor = 1.0 - Math.Exp(-18.0 * dt);
            _currentOffset += diff * factor;
            _sv.ScrollToVerticalOffset(_currentOffset);
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            CompositionTarget.Rendering -= OnRender;
        }
    }
}
