using System.Windows;
using Glint.Core;
using Glint.Mascots;
using Glint.Services.Agents;

namespace Glint.Pill;

/// <summary>
/// The character at one end of the collapsed pill (Настройки → Персонаж). <see cref="MascotDirector"/> decides what
/// it does; this part only places it, widens the pill for it and tells the director whether anyone can see it.
/// Out of sight (panel, flash, request card, auto-hidden, fullscreen app, locked session) it runs nothing at all.
/// </summary>
public partial class PillWindow
{
    // Space between the character's box and the rest of the pill.
    const double MascotGap = 4;

    readonly MascotView _mascotView = new();
    MascotDirector? _mascot;
    bool _mascotShown;
    string? _mascotLayout;
    bool _sessionLocked;

    double MascotExtraWidth => _mascotShown ? _mascotView.Width + MascotGap : 0;

    void InitMascot()
    {
        _mascot = new MascotDirector(_mascotView);
        AppServices.Settings.Changed += (_, _) => ApplyMascotSettings();
        // Raised on the UI thread.
        AgentHub.Shared.Changed += (_, _) => _mascot.UpdateAgents();
        // Covers flashes, the panel, request cards and the fullscreen rule (the root hides): PillView isn't visible then.
        PillView.IsVisibleChanged += (_, _) => UpdateMascotLive();
        Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLock) _sessionLocked = true;
            else if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock) _sessionLocked = false;
            else return;
            UpdateMascotLive();
        });
        ApplyMascotSettings();
    }

    void ApplyMascotSettings()
    {
        if (_mascot is null) return;
        var s = AppServices.Settings.Current.Mascot;
        var design = s.Enabled ? MascotDesigns.Find(s.Character) : null;
        var layout = design is null ? MascotSettings.None : $"{design.Id}|{s.Size}|{s.Side}|{s.ThemeColors}";
        if (layout != _mascotLayout)
        {
            _mascotLayout = layout;
            MascotSlotLeft.Child = null;
            MascotSlotRight.Child = null;
            if (design is not null)
            {
                var right = s.Side == MascotSettings.Right;
                // The lane for extras faces the pill's centre; the inner border leaves 34 DIPs of height.
                _mascotView.Configure(design, s.Size, PillHeight - 2, lane: true, mirror: right, s.ThemeColors);
                _mascotView.Margin = right ? new Thickness(MascotGap, 0, 0, 0) : new Thickness(0, 0, MascotGap, 0);
                (right ? MascotSlotRight : MascotSlotLeft).Child = _mascotView;
            }
            _mascotShown = design is not null;
            ResizePillForIndicators();
        }
        _mascot.ApplySettings();
        UpdateMascotLive();
    }

    void UpdateMascotLive() =>
        _mascot?.SetLive(_mascotShown && PillView.IsVisible && !_hiddenByRule && !_tucked && !_sessionLocked);
}
