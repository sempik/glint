using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using Glint.Capture;

namespace Glint.Pill;

/// <summary>
/// Screen tools in the pill: a red dot with the running time while the screen is recorded (a click on it stops
/// the recording), and keeping the pill out of Glint's own screenshots and recordings.
/// </summary>
public partial class PillWindow
{
    const double RecordingWidth = 62, RecordingLongWidth = 76;

    int _captureExclusions;
    bool _recShown;
    bool _recLong;
    bool _hoverHeldAfterStop;

    /// <summary>True while the panel is open (screen tools keep it out of screenshots).</summary>
    public bool IsExpanded => _state == State.Expanded;

    double RecordingExtraWidth => _recShown ? (_recLong ? RecordingLongWidth : RecordingWidth) : 0;

    bool IsOverRecordingChip => _recShown && PillRec.IsMouseOver;

    void InitCapture()
    {
        ScreenTools.RecordingChanged += (_, _) => UpdateRecording();
        // Rides on the clock's one-second tick; no timer of its own.
        _clock.Tick += (_, _) => TickRecording();
        // Pressing here must not start dragging the pill; the release is the click.
        PillRec.MouseLeftButtonDown += (_, e) => e.Handled = true;
        PillRec.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (!ScreenTools.IsRecording) return;
            // The dot vanishes under the cursor; that is not a reason to open the panel (until the mouse leaves).
            _hoverHeldAfterStop = true;
            _hoverIn.Stop();
            _ = ScreenTools.StopRecordingAsync(null);
        };
        PillRec.MouseEnter += (_, _) =>
        {
            // Aiming at the stop button shouldn't unfold the panel under the cursor.
            _hoverIn.Stop();
            PillRec.SetResourceReference(BackgroundProperty, "CardHoverBrush");
        };
        PillRec.MouseLeave += (_, _) =>
        {
            PillRec.Background = Brushes.Transparent;
            if (Shell.IsMouseOver && _state == State.Pill && _dragAnchor is null && !_hoverHeldAfterStop) _hoverIn.Start();
        };
        Shell.MouseLeave += (_, _) => _hoverHeldAfterStop = false;
    }

    void UpdateRecording()
    {
        var recording = ScreenTools.IsRecording;
        PillRec.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
        _recLong = false;
        if (recording && !_recShown)
        {
            // A new recording starts from zero, not from the last one's final time.
            PillRecTime.Text = ScreenTools.FormatLength(TimeSpan.Zero);
            PillRecDot.Opacity = 1;
        }
        TickRecording();
        if (recording == _recShown) return;
        _recShown = recording;
        // A recording keeps the pill in view (it is out of the video anyway when Glint hides itself).
        if (recording) Reveal();
        else ScheduleTuck();
        ResizePillForIndicators();
    }

    void TickRecording()
    {
        if (!_recShown || ScreenTools.RecordingSince is not DateTime since) return;
        var elapsed = DateTime.Now - since;
        PillRecTime.Text = ScreenTools.FormatLength(elapsed);
        // A slow blink: the time repaints every second anyway, so it costs nothing extra.
        PillRecDot.Opacity = PillRecDot.Opacity < 1 ? 1 : 0.4;
        var isLong = elapsed >= TimeSpan.FromHours(1);
        if (isLong == _recLong) return;
        _recLong = isLong;
        ResizePillForIndicators();
    }

    /// <summary>
    /// Keeps the pill (and panel) out of screen captures: Glint's own snapshots, its recordings, and other
    /// capture apps while it lasts. Counted, so overlapping users don't undo each other.
    /// </summary>
    public void ExcludeFromCapture(bool exclude)
    {
        _captureExclusions = Math.Max(0, _captureExclusions + (exclude ? 1 : -1));
        if (_hwnd == IntPtr.Zero) return;
        SetWindowDisplayAffinity(_hwnd, _captureExclusions > 0 ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
