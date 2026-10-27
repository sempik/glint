using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Glint.Core;
using Glint.Pages;
using Glint.Services.Agents;

namespace Glint.Pill;

/// <summary>
/// Coding agents in the pill. A small chip next to the clock shows the most urgent session ("Glint · работает +1");
/// its dot breathes while an agent works (8 fps, only while the pill is on screen). A permission request or a
/// question turns the pill into a request card with answer buttons until it is answered, handed to the terminal
/// or runs out of time. Several waiting requests are asked one after another, oldest first.
/// </summary>
public partial class PillWindow
{
    const double RequestWidth = 470, RequestMaxHeight = 330;
    const int AgentProjectChars = 18;

    bool _agentShown;
    double _agentWidth;
    bool _agentWorking;
    bool _agentPulsing;
    AgentRequestCard? _requestCard;
    // The window that had the keyboard before a click on the request card; it gets it back after the answer.
    IntPtr _requestReturnFocus;
    // The last foreground window other than the pill, kept by the 1.5 s rule check and ShowRequest.
    IntPtr _lastForeground;
    Guid? _requestNotified;

    double AgentExtraWidth => _agentShown ? _agentWidth : 0;

    bool IsOverAgentChip => _agentShown && PillAgent.IsMouseOver;

    void InitAgents()
    {
        // Raised on the UI thread.
        AgentHub.Shared.Changed += (_, _) =>
        {
            UpdateAgentChip();
            UpdateAgentRequests();
        };
        AppServices.Settings.Changed += (_, _) =>
        {
            UpdateAgentsTab();
            UpdateAgentChip();
        };
        // Pressing here must not start dragging the pill; the release is the click.
        PillAgent.MouseLeftButtonDown += (_, e) => e.Handled = true;
        PillAgent.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowAgents();
        };
        PillAgent.MouseEnter += (_, _) =>
        {
            _hoverIn.Stop();
            PillAgent.SetResourceReference(BackgroundProperty, "CardHoverBrush");
        };
        PillAgent.MouseLeave += (_, _) =>
        {
            PillAgent.Background = Brushes.Transparent;
            if (Shell.IsMouseOver && _state == State.Pill && _dragAnchor is null) _hoverIn.Start();
        };
        PillView.IsVisibleChanged += (_, _) => UpdateAgentPulse();
        // The countdown rides on the clock's tick and repaints once a minute until the last one.
        _clock.Tick += (_, _) => { if (_state == State.Request) _requestCard?.Tick(); };
        RequestView.PreviewMouseLeftButtonDown += (_, _) =>
        {
            // The click has activated the pill by now, so the foreground is usually ours; the window from before it
            // is the one the rule timer and ShowRequest saw last.
            var fg = Native.GetForegroundWindow();
            if (fg == _hwnd) fg = _lastForeground;
            _requestReturnFocus = IsOurWindow(fg) ? IntPtr.Zero : fg;
        };
        UpdateAgentsTab();
    }

    /// <summary>The Агенты tab is there only while agent events are accepted.</summary>
    void UpdateAgentsTab()
    {
        var on = AppServices.Settings.Current.Agents.Enabled;
        var button = _tabButtons[AgentsTab];
        var visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (button.Visibility == visibility) return;
        button.Visibility = visibility;
        if (!on && _currentTab == AgentsTab) _tabButtons[HomeTab].IsChecked = true;
        FitTabsSoon();
    }

    // ───────────── Status chip ─────────────

    void UpdateAgentChip()
    {
        var hub = AgentHub.Shared;
        var visible = AppServices.Settings.Current.Agents.ShowInPill && hub.IsListening
            ? hub.Sessions.Where(s => s.Shown != AgentState.Idle).ToList()
            : new List<AgentSession>();
        var top = visible.FirstOrDefault();
        if (top is not null)
        {
            PillAgentText.Text = $"{Shorten(top.Project)} · {AgentSession.StateText(top.Shown)}";
            PillAgentMore.Text = $"+{visible.Count - 1}";
            PillAgentMore.Visibility = visible.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            PillAgentDot.SetResourceReference(Shape.FillProperty, AgentSession.StateBrush(top.Shown));
            PillAgent.ToolTip = string.Join("\n", visible.Select(s =>
                $"{s.AgentName} · {s.Project}: {AgentSession.StateText(s.Shown)}{(string.IsNullOrEmpty(s.Detail) ? "" : " — " + s.Detail)}"));
            PillAgent.Visibility = Visibility.Visible;
            PillAgent.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        }
        else
        {
            PillAgent.Visibility = Visibility.Collapsed;
        }
        var shown = top is not null;
        var width = shown ? Math.Ceiling(PillAgent.DesiredSize.Width) : 0;
        _agentWorking = top?.Shown == AgentState.Working;
        UpdateAgentPulse();
        if (shown == _agentShown && Math.Abs(width - _agentWidth) < 0.5) return;
        _agentShown = shown;
        _agentWidth = width;
        ResizePillForIndicators();
    }

    static string Shorten(string project) =>
        project.Length <= AgentProjectChars ? project : project[..(AgentProjectChars - 1)].TrimEnd() + "…";

    void UpdateAgentPulse()
    {
        var want = _agentShown && _agentWorking && PillView.IsVisible && !_hiddenByRule && !_tucked;
        if (want == _agentPulsing) return;
        _agentPulsing = want;
        if (!want)
        {
            PillAgentDot.BeginAnimation(OpacityProperty, null);
            PillAgentDot.Opacity = 1;
            return;
        }
        var anim = new DoubleAnimation(1, 0.3, TimeSpan.FromMilliseconds(900))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };
        // An agent can work for an hour; a slow breath looks the same at 8 fps and costs a fraction of 60.
        Timeline.SetDesiredFrameRate(anim, 8);
        PillAgentDot.BeginAnimation(OpacityProperty, anim);
    }

    // ───────────── Request card ─────────────

    void UpdateAgentRequests()
    {
        var requests = AgentHub.Shared.Requests;
        if (_state == State.Request)
        {
            var shown = _requestCard?.Request;
            if (shown is not null && shown.IsOpen)
            {
                _requestCard!.SetPosition(Position(requests, shown), requests.Count);
                return;
            }
            // Answered here, in the Агенты tab, withdrawn or timed out: the next one, or back to the pill.
            if (requests.Count > 0) ShowRequest(requests[0]);
            else EndRequest();
            return;
        }
        if (requests.Count > 0) TryShowRequest();
    }

    static int Position(IReadOnlyList<AgentRequest> requests, AgentRequest request)
    {
        for (var i = 0; i < requests.Count; i++)
            if (requests[i] == request) return i + 1;
        return 1;
    }

    /// <summary>Shows the oldest waiting request if the pill is free for it. True if it is on screen now.</summary>
    bool TryShowRequest()
    {
        var next = AgentHub.Shared.Requests.FirstOrDefault();
        if (next is null) return false;
        // No fresh ApplyVisibilityRules here: it can hide the pill and re-enter the flash code that called this.
        // The rule timer re-checks every 1.5 s and shows the card when the pill comes back.
        if (_hiddenByRule)
        {
            // Over a fullscreen app the card can't show; Windows tells the user once per request.
            if (_requestNotified != next.Id)
            {
                _requestNotified = next.Id;
                AppServices.SystemNotify?.Invoke($"{next.Session.AgentName} ждёт ответа", $"{next.Session.Project}: {next.Action}");
            }
            return false;
        }
        // An open panel has the Агенты tab and a header chip for it; the card follows when the panel closes.
        if (_state is State.Expanded or State.Request) return false;
        ShowRequest(next);
        return true;
    }

    void ShowRequest(AgentRequest request)
    {
        _hoverIn.Stop();
        _flashTimer.Stop();
        RememberForeground();
        if (_requestCard?.Request != request)
        {
            var card = new AgentRequestCard(request);
            card.Acted += (_, _) => ReturnFocusAfterAnswer();
            card.LayoutChanged += (_, _) => SizeRequestView();
            _requestCard = card;
            RequestView.Child = card;
        }
        var requests = AgentHub.Shared.Requests;
        _requestCard.SetPosition(Position(requests, request), requests.Count);

        if (_state != State.Request)
        {
            if (_state == State.Flash)
            {
                RequeueCurrentImportant();
                _currentFlash = null;
                Fade(FlashView, false);
            }
            else
            {
                Fade(PillView, false);
            }
            _state = State.Request;
            Reveal();
            UpdateClickThrough();
            Fade(RequestView, true, delayMs: 80);
            SetShadow(full: true);
        }
        SizeRequestView();
    }

    /// <summary>The card decides the height: a one-line command is short, a question with descriptions is tall.</summary>
    void SizeRequestView()
    {
        if (_state != State.Request || _requestCard is not { } card) return;
        // The card itself, not RequestView: a view that was collapsed until a moment ago skips the measure and
        // reports zero until the next layout pass.
        var margin = RequestView.Margin;
        card.Measure(new Size(RequestWidth - 2 - margin.Left - margin.Right, double.PositiveInfinity));
        var height = Math.Min(RequestMaxHeight, Math.Ceiling(card.DesiredSize.Height + margin.Top + margin.Bottom) + 2);
        Shell.CornerRadius = new CornerRadius(24);
        AnimateShell(RequestWidth, height);
    }

    void EndRequest()
    {
        if (_state != State.Request) return;
        _state = State.Pill;
        _requestCard = null;
        Fade(RequestView, false);
        SetShadow(full: false);
        // Before the early return: a queued flash after the card passes clicks like the pill does.
        UpdateClickThrough();
        if (TryShowRequest() || TryShowQueued()) return;
        Fade(PillView, true, delayMs: 100);
        Shell.CornerRadius = new CornerRadius(PillHeight / 2);
        AnimateShell(CurrentPillWidth, PillHeight);
        // The cursor is still on the pill after a click on a button; don't unfold the panel under it.
        if (!Shell.IsMouseOver) ScheduleTuck();
    }

    /// <summary>The panel opens or the pill hides: the card goes, the request stays open and comes back later.</summary>
    void LeaveRequestView()
    {
        if (_state != State.Request) return;
        _requestCard = null;
        Fade(RequestView, false);
    }

    /// <summary>A fullscreen app came to the front: back to the plain pill under the hidden root.</summary>
    void HideRequestForRule()
    {
        if (_state != State.Request) return;
        LeaveRequestView();
        _state = State.Pill;
        Fade(PillView, true);
        Shell.CornerRadius = new CornerRadius(PillHeight / 2);
        SetShadow(full: false);
        AnimateShell(CurrentPillWidth, PillHeight);
    }

    void ReturnFocusAfterAnswer()
    {
        var target = _requestReturnFocus;
        _requestReturnFocus = IntPtr.Zero;
        if (target != IntPtr.Zero && IsActive) Native.SetForegroundWindow(target);
    }

    /// <summary>Notes the window in front unless it is the pill itself: where the keyboard goes back after an answer.</summary>
    void RememberForeground()
    {
        var fg = Native.GetForegroundWindow();
        if (fg != IntPtr.Zero && fg != _hwnd) _lastForeground = fg;
    }

    /// <summary>Zero, or a window of Glint itself (command bar, a dialog): nothing to hand the keyboard back to.</summary>
    static bool IsOurWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return true;
        Native.GetWindowThreadProcessId(window, out var pid);
        return pid == Environment.ProcessId;
    }

    // ───────────── Navigation ─────────────

    /// <summary>Opens the Агенты tab (the pill chip, the header chip, the tray); Настройки when agents are off.</summary>
    public void ShowAgents()
    {
        if (!AppServices.Settings.Current.Agents.Enabled)
        {
            ShowAgentSettings();
            return;
        }
        Activate();
        Expand(AgentsTab);
    }

    /// <summary>Opens Настройки at the Агенты card.</summary>
    public void ShowAgentSettings()
    {
        Activate();
        Expand(SettingsTab);
        if (_pages.TryGetValue(SettingsTab, out var page) && page is SettingsPage settings) settings.RevealAgents();
    }
}
