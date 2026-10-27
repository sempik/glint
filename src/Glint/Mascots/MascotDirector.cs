using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Glint.Core;
using Glint.Services;
using Glint.Services.Agents;

namespace Glint.Mascots;

/// <summary>
/// Decides what the pill's character does and when it is drawn. Rendering is software-only into a layered window,
/// so the rule is: no frames unless something on the character moves.
/// <list type="bullet">
/// <item>Eyes follow the cursor. Its position is read twice a second while it is far or resting, about 16 times
/// a second while it moves within <see cref="NearDistance"/> of the pill; a still cursor draws nothing.</item>
/// <item>A blink (or now and then a look to the side) every few seconds: a handful of frames.</item>
/// <item>Dancing to music: the only steady animation, at the equalizer's 24 fps so both share frames. The hops
/// follow peaks of the output level read from the endpoint meter on each frame.</item>
/// <item>Thinking while a coding agent works: thought dots stepping at 3 fps, a glance up every few seconds.</item>
/// <item>Asleep after a few minutes without input (and without hooks from a busy agent): breathing and Zzz at
/// 5 fps for a minute, then a still pose.</item>
/// </list>
/// Everything stops while the character is out of sight (panel open, flash, auto-hidden, fullscreen app, locked).
/// UI thread only.
/// </summary>
public sealed class MascotDirector
{
    // The dance matches the equalizer's 24 fps; motion (blinks, hops, eyes catching up) is 20.
    const int DanceFps = 24, MotionFps = 20, BreathFps = 5, ThinkFps = 3;
    // DIPs from the eyes: closer than Near switches to the fast cursor reads, farther than Far back to slow.
    const double NearDistance = 360, FarDistance = 430;
    // The eyes turn all the way once the cursor is this far; closer, they look less to the side.
    const double FullLookDistance = 90;
    const double Settled = 0.03, RestLookY = 0.25;
    // Seconds.
    const double BlinkLength = 0.16, HopLength = 0.42, BeatHop = 0.3, LandLength = 0.12, NoteLength = 1.1;
    const double ThinkGlancePeriod = 2.6;
    // Breathing and drifting Zzz for the first minute of sleep; then the pose freezes and nothing is drawn.
    const double BreathFor = 60;
    static readonly TimeSpan SlowProbe = TimeSpan.FromMilliseconds(500);
    static readonly TimeSpan FastProbe = TimeSpan.FromMilliseconds(60);
    // A cursor this long in one place near the pill drops back to the slow reads.
    const double StillFor = 1;
    static readonly TimeSpan AwakeIdleCheck = TimeSpan.FromSeconds(15);
    static readonly TimeSpan AsleepIdleCheck = TimeSpan.FromSeconds(1);
    static readonly TimeSpan ExpressionFor = TimeSpan.FromSeconds(3.5);

    readonly MascotView _view;
    readonly DispatcherTimer _probe = new() { Interval = SlowProbe };
    readonly DispatcherTimer _idle = new() { Interval = AwakeIdleCheck };
    readonly DispatcherTimer _blink = new();
    readonly DispatcherTimer _expression = new() { Interval = ExpressionFor };
    readonly Stopwatch _time = Stopwatch.StartNew();
    readonly Random _random = new();
    readonly Dictionary<string, AgentState> _agentStates = new();

    bool _live;
    bool _music;
    bool _asleep;
    bool _working;
    bool _waiting;
    bool _near;
    bool _glancing;
    bool _singing;
    int _tickerFps;
    double _lastFrame;
    double _lookX, _lookY = RestLookY, _targetX, _targetY = RestLookY;
    double _blinkAt = -1, _hopAt = -1, _hopPower;
    double _asleepAt;
    POINT _cursor;
    double _cursorMovedAt;
    DateTime _agentActivity = DateTime.MinValue;
    // Dance: a fast envelope and a slow average of the output level; a jump above the average is a beat.
    double _env, _avg, _beatAt = -10, _beatPower, _noteAt = -10;
    MascotFace? _expressionFace;

    public MascotDirector(MascotView view)
    {
        _view = view;
        _view.FrameTick = OnFrame;
        _probe.Tick += (_, _) => Probe();
        _idle.Tick += (_, _) => { if (CheckIdle()) Update(); };
        _blink.Tick += (_, _) => OnBlinkTimer();
        _expression.Tick += (_, _) =>
        {
            _expression.Stop();
            _expressionFace = null;
            Update();
        };
    }

    static MascotSettings S => AppServices.Settings.Current.Mascot;

    double Now => _time.Elapsed.TotalSeconds;

    bool Dancing => _live && _music && !_asleep && S.Dance;

    bool Thinking => _working && !_waiting;

    bool EyesSettled => Math.Abs(_lookX - _targetX) < Settled && Math.Abs(_lookY - _targetY) < Settled;

    /// <summary>On screen or not: the pill is collapsed and visible, not tucked away, the session not locked.</summary>
    public void SetLive(bool live)
    {
        if (live == _live) return;
        _live = live;
        if (live)
        {
            CheckIdle();
        }
        else
        {
            // Back at rest when it comes back: no half-finished blink or hop frozen on screen.
            _blinkAt = _hopAt = -1;
            _near = _glancing = _singing = false;
            _expressionFace = null;
            _expression.Stop();
            _lookX = _targetX = 0;
            _lookY = _targetY = RestLookY;
        }
        Update();
    }

    /// <summary>Something is playing (the same rule as the equalizer).</summary>
    public void SetMusic(bool playing)
    {
        if (playing == _music) return;
        _music = playing;
        if (!playing) _singing = false;
        Update();
    }

    /// <summary>Settings changed: toggles may have turned parts on or off.</summary>
    public void ApplySettings()
    {
        UpdateAgents();
        CheckIdle();
        Update();
    }

    /// <summary>Agent sessions changed (AgentHub.Changed, UI thread).</summary>
    public void UpdateAgents()
    {
        var hub = AgentHub.Shared;
        var on = S.ThinkWithAgents && AppServices.Settings.Current.Agents.Enabled && hub.IsListening;
        var sessions = on ? hub.Sessions : Array.Empty<AgentSession>();
        var working = sessions.Any(x => x.Shown == AgentState.Working);
        var waiting = on && (hub.Requests.Count > 0 || sessions.Any(x => x.Shown == AgentState.Waiting));
        // The last sign of life from a busy agent: sleep may win once it has been silent as long as the user.
        var activity = sessions.Where(x => x.Shown is AgentState.Working or AgentState.Waiting).Select(x => x.LastActivity)
            .Concat(on ? hub.Requests.Select(r => r.Created) : Enumerable.Empty<DateTime>())
            .DefaultIfEmpty(DateTime.MinValue).Max();
        var fresh = activity > _agentActivity;
        _agentActivity = activity;

        // Only a change seen here counts: a session already done when Glint started is old news.
        MascotFace? news = null;
        foreach (var x in sessions)
        {
            if (!_agentStates.TryGetValue(x.Key, out var before) || before == x.State) continue;
            if (x.State == AgentState.Error) news = MascotFace.Worried;
            else if (x.State == AgentState.Done) news ??= MascotFace.Happy;
        }
        _agentStates.Clear();
        foreach (var x in sessions) _agentStates[x.Key] = x.State;

        // A new hook from the agent wakes a character that dozed off over its silence.
        var changed = working != _working || waiting != _waiting || (_asleep && fresh);
        _working = working;
        _waiting = waiting;
        if (news is { } face && _live)
        {
            _expressionFace = face;
            _expression.Stop();
            _expression.Start();
            if (face == MascotFace.Happy) Hop(0.8);
            changed = true;
        }
        if (!changed) return;
        // An agent at work keeps the character awake; one that finished lets it doze off later.
        CheckIdle();
        Update();
    }

    /// <summary>Re-decides the timers and the frame rate after any change, then draws the pose once.</summary>
    void Update()
    {
        var s = S;
        var follow = _live && s.FollowCursor && !_asleep && !Thinking;
        if (follow)
        {
            // Near the pill the probe picks its own pace (fast while the cursor moves).
            if (!_near) _probe.Interval = SlowProbe;
            if (!_probe.IsEnabled) _probe.Start();
        }
        else
        {
            _probe.Stop();
            _near = false;
            if (!_glancing) Aim(0, RestLookY);
        }

        var idleInterval = _asleep ? AsleepIdleCheck : AwakeIdleCheck;
        if (_idle.Interval != idleInterval) _idle.Interval = idleInterval;
        if (_live && s.Sleep) { if (!_idle.IsEnabled) _idle.Start(); }
        else _idle.Stop();

        if (_live && !_asleep) { if (!_blink.IsEnabled) ScheduleBlink(); }
        else _blink.Stop();

        // The meter is held only while dancing.
        if (!Dancing) AudioPeakMeter.Shared.Release();

        AimForThought(Now);
        Render(Now);
        UpdateTicker();
    }

    // ───────────── Frames ─────────────

    int WantedFps(double now)
    {
        if (!_live) return 0;
        if (Dancing) return DanceFps;
        if (_blinkAt >= 0 || _hopAt >= 0 || !EyesSettled) return MotionFps;
        if (_asleep) return now - _asleepAt < BreathFor ? BreathFps : 0;
        if (Thinking) return ThinkFps;
        return 0;
    }

    /// <summary>Starts, retimes or stops the frame clock: an endless animation on the view, at the rate needed now.</summary>
    void UpdateTicker()
    {
        var fps = WantedFps(Now);
        if (fps == _tickerFps) return;
        var wasStopped = _tickerFps == 0;
        _tickerFps = fps;
        if (fps == 0)
        {
            _view.BeginAnimation(MascotView.FrameProperty, null);
            return;
        }
        if (wasStopped) _lastFrame = Now;
        var clock = new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever };
        Timeline.SetDesiredFrameRate(clock, fps);
        _view.BeginAnimation(MascotView.FrameProperty, clock);
    }

    void OnFrame()
    {
        // Removing the clock resets the property, which calls here once more.
        if (_tickerFps == 0) return;
        var now = Now;
        var dt = Math.Clamp(now - _lastFrame, 0, 0.25);
        _lastFrame = now;
        if (Dancing) Listen(now, dt);
        AimForThought(now);
        // The eyes catch up with their target: about 90 % of the way in a fifth of a second.
        var k = 1 - Math.Exp(-dt * 12);
        _lookX += (_targetX - _lookX) * k;
        _lookY += (_targetY - _lookY) * k;
        if (EyesSettled)
        {
            _lookX = _targetX;
            _lookY = _targetY;
        }
        Render(now);
        UpdateTicker();
    }

    void Render(double now)
    {
        if (_blinkAt >= 0 && now - _blinkAt >= BlinkLength) _blinkAt = -1;
        if (_hopAt >= 0 && now - _hopAt >= HopLength) _hopAt = -1;
        // The view compares and invalidates only when something visible changed.
        _view.Pose = _live ? Pose(now).Quantized() : MascotPose.Rest;
    }

    MascotFace Face()
    {
        if (_asleep) return MascotFace.Sleep;
        if (_waiting) return MascotFace.Alert;
        if (_working) return MascotFace.Think;
        if (_expressionFace is { } f) return f;
        if (Dancing)
        {
            // A little hysteresis so the mouth doesn't flicker between a smile and singing.
            _singing = _singing ? _env > 0.22 : _env > 0.34;
            if (_singing) return MascotFace.Sing;
        }
        return MascotFace.Normal;
    }

    MascotPose Pose(double now)
    {
        var face = Face();
        double hop = 0, squash = 0, tilt = 0, open = 0, note = 0, zzz = 0, blink = 0;
        var dots = 0;

        if (_blinkAt >= 0)
        {
            var t = (now - _blinkAt) / BlinkLength;
            blink = 1 - Math.Abs(2 * t - 1);
        }
        if (_hopAt >= 0)
            hop = Math.Sin(Math.PI * (now - _hopAt) / HopLength) * _hopPower;

        if (Dancing)
        {
            var bt = now - _beatAt;
            if (bt < BeatHop) hop = Math.Max(hop, Math.Sin(Math.PI * bt / BeatHop) * _beatPower * 0.8);
            else if (bt < BeatHop + LandLength) squash = 0.12 * _beatPower * (1 - (bt - BeatHop) / LandLength);
            tilt = Math.Sin(now * Math.PI * 1.9) * (2.5 + 6 * _env);
            open = Math.Clamp(_env, 0, 1);
            if (now - _noteAt < NoteLength && face is not (MascotFace.Think or MascotFace.Alert)) note = Math.Max(0.01, (now - _noteAt) / NoteLength);
        }

        if (_asleep)
        {
            var slept = now - _asleepAt;
            var breathing = slept < BreathFor;
            squash = breathing ? 0.035 * Math.Sin(slept / 2.8 * Math.PI * 2) : 0.02;
            zzz = breathing ? slept / 2.4 % 1 : 0.4;
        }

        if (face == MascotFace.Think) dots = (int)(now * ThinkFps) % 3 + 1;
        // Stretched a little while in the air.
        if (hop > 0 && squash == 0) squash = -0.05 * hop;

        return new MascotPose
        {
            LookX = _lookX,
            LookY = _lookY,
            Blink = blink,
            Hop = hop,
            SquashDelta = squash,
            Tilt = tilt,
            Open = open,
            Face = face,
            Dots = dots,
            Zzz = zzz,
            Note = note,
        };
    }

    void Hop(double power)
    {
        _hopAt = Now;
        _hopPower = power;
    }

    void Aim(double x, double y)
    {
        _targetX = x;
        _targetY = y;
    }

    /// <summary>While thinking the eyes look up, to one side and then the other.</summary>
    void AimForThought(double now)
    {
        if (!Thinking || _asleep) return;
        Aim((int)(now / ThinkGlancePeriod) % 2 == 0 ? -0.6 : 0.6, -0.75);
    }

    // ───────────── Dance ─────────────

    void Listen(double now, double dt)
    {
        if (AudioPeakMeter.Shared.Read() is not float peak)
        {
            // No meter to read (no output device, audio service restarting): a steady two hops a second.
            _env = 0.45;
            if (now - _beatAt > 0.5) Beat(now, 0.6);
            return;
        }
        // Square root: quiet music still moves, loud music doesn't pin everything at the top.
        var level = Math.Sqrt(peak);
        _env = Math.Max(level, _env - dt * 1.6);
        _avg += (level - _avg) * Math.Min(1, dt * 1.3);
        var since = now - _beatAt;
        if (level > 0.08 && level > _avg * 1.15 + 0.035 && since > 0.26)
            Beat(now, Math.Clamp(0.45 + (level - _avg) * 2.2, 0.45, 1));
        // Dense, even music has few peaks above its average; keep moving to it anyway.
        else if (level > 0.15 && since > 0.9)
            Beat(now, 0.5);
    }

    void Beat(double now, double power)
    {
        _beatAt = now;
        _beatPower = power;
        if (power > 0.8 && now - _noteAt > 2.4) _noteAt = now;
    }

    // ───────────── Cursor ─────────────

    void Probe()
    {
        if (!_live || !GetCursorPos(out var cursor)) return;
        var now = Now;
        var moved = cursor.X != _cursor.X || cursor.Y != _cursor.Y;
        if (moved)
        {
            _cursor = cursor;
            _cursorMovedAt = now;
        }
        if (_near)
        {
            // A cursor parked near the pill (browser tabs, the address bar) needs no fast reads: slow ones until it moves.
            var interval = moved || now - _cursorMovedAt < StillFor ? FastProbe : SlowProbe;
            if (_probe.Interval != interval) _probe.Interval = interval;
            // A fast read of a still cursor has nothing new to aim at.
            if (!moved && interval == FastProbe) return;
        }
        if (!TryCursorOffset(cursor, out var dx, out var dy)) return;
        var distance = Math.Sqrt(dx * dx + dy * dy);
        var near = _near ? distance < FarDistance : distance < NearDistance;
        if (near != _near)
        {
            _near = near;
            _probe.Interval = near ? FastProbe : SlowProbe;
            if (near) _glancing = false;
        }
        if (near)
        {
            var reach = Math.Min(1, distance / FullLookDistance);
            if (distance > 0.5) Aim(dx / distance * reach, dy / distance * reach);
        }
        else if (!_glancing)
        {
            Aim(0, RestLookY);
        }
        UpdateTicker();
    }

    /// <summary>Cursor minus the point between the eyes, in DIPs.</summary>
    bool TryCursorOffset(POINT cursor, out double dx, out double dy)
    {
        dx = dy = 0;
        if (PresentationSource.FromVisual(_view) is null) return false;
        try
        {
            var eyes = _view.PointToScreen(_view.EyeCenter);
            var dpi = VisualTreeHelper.GetDpi(_view).DpiScaleX;
            dx = (cursor.X - eyes.X) / dpi;
            dy = (cursor.Y - eyes.Y) / dpi;
            return true;
        }
        catch (InvalidOperationException)
        {
            // Not connected to a presentation source after all.
            return false;
        }
    }

    // ───────────── Blinks and glances ─────────────

    void ScheduleBlink()
    {
        _blink.Interval = TimeSpan.FromSeconds(2.5 + _random.NextDouble() * 4.5);
        _blink.Start();
    }

    void OnBlinkTimer()
    {
        _blink.Stop();
        if (!_live || _asleep) return;
        if (_glancing)
        {
            // Back from a look around.
            _glancing = false;
            if (!_near && !Thinking) Aim(0, RestLookY);
        }
        else if (!_near && !Dancing && !_working && !_waiting && _random.Next(5) == 0)
        {
            // Now and then, with nothing to watch, a look to one side and back a second later.
            _glancing = true;
            Aim((_random.Next(2) == 0 ? -1 : 1) * (0.55 + _random.NextDouble() * 0.35), RestLookY - 0.15);
            _blink.Interval = TimeSpan.FromSeconds(1.1);
            _blink.Start();
            UpdateTicker();
            return;
        }
        _blinkAt = Now;
        UpdateTicker();
        ScheduleBlink();
    }

    // ───────────── Sleep ─────────────

    /// <summary>Asleep after the chosen minutes without input, unless an agent is busy. True if that changed.</summary>
    bool CheckIdle()
    {
        var s = S;
        var after = TimeSpan.FromMinutes(s.SleepMinutes);
        // A busy agent keeps it awake, but not one that has been silent as long: an interrupted turn or a prompt
        // answered in the terminal sends nothing more, and must not keep the character thinking all night.
        var agentBusy = (_working || _waiting) && DateTime.Now - _agentActivity < after;
        var away = _live && s.Sleep && !agentBusy && IdleTime() >= after;
        if (away == _asleep) return false;
        _asleep = away;
        if (away)
        {
            _asleepAt = Now;
            _blinkAt = -1;
            _glancing = false;
        }
        else if (_live)
        {
            // Waking up: a small hop.
            Hop(0.55);
        }
        return true;
    }

    static TimeSpan IdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return TimeSpan.Zero;
        // Both are milliseconds since boot and wrap together after 49 days.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime));
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetLastInputInfo(ref LASTINPUTINFO info);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out POINT point);
}
