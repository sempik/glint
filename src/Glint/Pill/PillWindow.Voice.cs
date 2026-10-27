using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Glint.Services.Voice;

namespace Glint.Pill;

/// <summary>
/// Dictation in the pill: a pulsing mic and the phrase being heard, live; a click finishes it. The pulse is the only
/// animation, capped at 12 fps, and runs only while the pill itself is on screen.
/// </summary>
public partial class PillWindow
{
    const double VoiceWidth = 236;
    // Characters of the live phrase that fit next to the mic; the newest words matter, so the start is cut.
    const int VoiceTextChars = 27;

    bool _voiceShown;
    bool _voicePulsing;

    double VoiceExtraWidth => _voiceShown ? VoiceWidth : 0;

    bool IsOverVoiceChip => _voiceShown && PillVoice.IsMouseOver;

    void InitVoice()
    {
        DictationService.Shared.Changed += (_, _) => UpdateVoice();
        // Pressing here must not start dragging the pill; the release is the click.
        PillVoice.MouseLeftButtonDown += (_, e) => e.Handled = true;
        PillVoice.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            DictationService.Shared.Stop();
        };
        PillVoice.MouseEnter += (_, _) =>
        {
            _hoverIn.Stop();
            PillVoice.SetResourceReference(BackgroundProperty, "CardHoverBrush");
        };
        PillVoice.MouseLeave += (_, _) =>
        {
            PillVoice.Background = Brushes.Transparent;
            if (Shell.IsMouseOver && _state == State.Pill && _dragAnchor is null) _hoverIn.Start();
        };
        PillView.IsVisibleChanged += (_, _) => UpdatePulse();
    }

    void UpdateVoice()
    {
        var d = DictationService.Shared;
        var on = d.IsListening || d.IsStarting;
        if (on)
        {
            PillVoiceText.Text = d.IsStarting ? "Включаю микрофон…" : Tail(d.Partial) ?? $"Слушаю · {d.Target?.Name}";
            PillVoice.ToolTip = d.Target is WindowDictationTarget { Collected.Length: > 0 } w
                ? $"Вставится в {w.Name}: {w.Collected}"
                : "Закончить диктовку";
        }
        if (on == _voiceShown) return;
        _voiceShown = on;
        PillVoice.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdatePulse();
        if (on) Reveal();
        else
        {
            ScheduleTuck();
            // A panel kept open for dictation into Текст may close now that it's done.
            if (_state == State.Expanded && !Shell.IsMouseOver) _hoverOut.Start();
        }
        UpdateLyricLine();
        ResizePillForIndicators();
    }

    static string? Tail(string? text) =>
        string.IsNullOrEmpty(text) ? null : text.Length <= VoiceTextChars ? text : "…" + text[^VoiceTextChars..].TrimStart();

    void UpdatePulse()
    {
        var want = _voiceShown && PillView.IsVisible && !_hiddenByRule;
        if (want == _voicePulsing) return;
        _voicePulsing = want;
        if (!want)
        {
            PillVoiceIcon.BeginAnimation(OpacityProperty, null);
            PillVoiceIcon.Opacity = 1;
            return;
        }
        var anim = new DoubleAnimation(1, 0.35, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        // A slow fade looks the same at 12 fps; every frame is a software-rendered layered update.
        Timeline.SetDesiredFrameRate(anim, 12);
        PillVoiceIcon.BeginAnimation(OpacityProperty, anim);
    }

    /// <summary>True while dictation types into a box on this panel; the panel then stays open.</summary>
    bool DictatingHere => DictationService.Shared.Target is TextBoxDictationTarget t && t.IsIn(this);
}
