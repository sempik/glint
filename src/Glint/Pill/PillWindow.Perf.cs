using System.Diagnostics;
using System.Text;
using System.Windows.Media;
using Glint.Core;

namespace Glint.Pill;

// ───────────── Animation timing log ─────────────
// Off unless %AppData%\Glint\perf.on exists. For each open/close it writes to perf.log how long the synchronous
// part took and the interval between rendered frames for the next 700 ms, plus the process CPU used meanwhile.

public partial class PillWindow
{
    static readonly bool PerfEnabled = File.Exists(Path.Combine(SettingsStore.DataDir, "perf.on"));
    static readonly string PerfPath = Path.Combine(SettingsStore.DataDir, "perf.log");

    readonly Stopwatch _perfClock = new();
    readonly List<(string Step, double Ms)> _perfSteps = new();
    readonly List<double> _perfFrames = new();
    string? _perfName;
    double _perfLastFrame;
    TimeSpan _perfCpuStart;

    void PerfBegin(string name)
    {
        if (!PerfEnabled) return;
        if (_perfName is not null) PerfFlush();
        _perfName = name;
        _perfSteps.Clear();
        _perfFrames.Clear();
        _perfCpuStart = Process.GetCurrentProcess().TotalProcessorTime;
        _perfClock.Restart();
        _perfLastFrame = 0;
        CompositionTarget.Rendering += OnPerfFrame;
    }

    void PerfMark(string step)
    {
        if (_perfName is null) return;
        _perfSteps.Add((step, _perfClock.Elapsed.TotalMilliseconds));
    }

    void OnPerfFrame(object? sender, EventArgs e)
    {
        var now = _perfClock.Elapsed.TotalMilliseconds;
        _perfFrames.Add(now - _perfLastFrame);
        _perfLastFrame = now;
        if (now > 700) PerfFlush();
    }

    void PerfFlush()
    {
        CompositionTarget.Rendering -= OnPerfFrame;
        if (_perfName is null) return;
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - _perfCpuStart).TotalMilliseconds;
        var sb = new StringBuilder();
        sb.Append($"{DateTime.Now:HH:mm:ss.fff} {_perfName} scale={_scale:0.##} state={_state} tab={_currentTab} cpu={cpu:0}ms ");
        sb.Append("steps=[" + string.Join(", ", _perfSteps.Select(s => $"{s.Step}@{s.Ms:0.0}")) + "] ");
        if (_perfFrames.Count > 0)
        {
            var sorted = _perfFrames.OrderBy(f => f).ToList();
            sb.Append($"frames={_perfFrames.Count} first={_perfFrames[0]:0} max={sorted[^1]:0} p50={sorted[sorted.Count / 2]:0} ");
            sb.Append("all=[" + string.Join(" ", _perfFrames.Select(f => f.ToString("0"))) + "]");
        }
        _perfName = null;
        try
        {
            File.AppendAllText(PerfPath, sb.AppendLine().ToString());
        }
        catch (Exception)
        {
            // Diagnostics only.
        }
    }
}
