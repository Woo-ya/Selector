namespace WinPopClip.Core.Models;

/// <summary>
/// Screen-space bounding rectangle in physical pixels.
/// </summary>
public readonly record struct ScreenRect(double Left, double Top, double Width, double Height)
{
    public double Right => Left + Width;
    public double Bottom => Top + Height;
    public double CenterX => Left + Width / 2.0;
    public bool IsEmpty => Width <= 0 && Height <= 0;
}

/// <summary>
/// Screen-space point in physical pixels.
/// </summary>
public readonly record struct ScreenPoint(double X, double Y);

/// <summary>
/// How the text selection was obtained.
/// </summary>
public enum SelectionCaptureSource
{
    UiAutomation,
    DeferredCapture,
    ClipboardFallback,
    ManualTest
}

/// <summary>
/// Snapshot of the current text selection and active window context when triggering the toolbar.
/// </summary>
public sealed class SelectionContext
{
    public required string Text { get; init; }
    public string TrimmedText => Text.Trim();
    public string ProcessName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
    public nint WindowHandle { get; init; }
    public bool IsEditable { get; init; }
    public bool IsPasswordField { get; init; }
    public bool IsEmptyInputSummon { get; init; }
    public bool IsDeferredCapture { get; init; }
    public bool HasClipboardText { get; init; }
    public SelectionCaptureSource Source { get; init; } = SelectionCaptureSource.UiAutomation;
    public ScreenRect? SelectionBounds { get; init; }
    public ScreenPoint AnchorPoint { get; init; }
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Extracted entities (URLs, emails, etc.) populated by PluginContextMatcher.
    /// </summary>
    public IReadOnlyList<string> DetectedUrls { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> DetectedEmails { get; init; } = Array.Empty<string>();
}
