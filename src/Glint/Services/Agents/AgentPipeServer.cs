using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Windows.Threading;

namespace Glint.Services.Agents;

/// <summary>
/// The named pipe the agent hooks talk to. Only this Windows user can open it (ACL with just the user's SID, network
/// logons denied). An idle server is one pending overlapped accept: no thread, no timer, no CPU. Each hook call is
/// one connection: status events are handed to <see cref="AgentHub"/> and acknowledged at once; a permission request
/// stays connected until the user chooses, the request times out, or the agent gives up (the hook process dies).
/// </summary>
public sealed class AgentPipeServer : IDisposable
{
    readonly AgentHub _hub;
    readonly Dispatcher _ui;
    readonly string _name;
    readonly CancellationTokenSource _stop = new();
    // Process lookups (one snapshot) only once per session, not on every event. The agent's start time tells a
    // recycled PID from the process that was found.
    readonly ConcurrentDictionary<string, (AgentProcessInfo Info, DateTime Started)> _processes = new();

    public AgentPipeServer(AgentHub hub, Dispatcher ui, string? name = null)
    {
        _hub = hub;
        _ui = ui;
        _name = name ?? AgentProtocol.PipeName;
    }

    public void Start() => _ = AcceptLoopAsync();

    async Task AcceptLoopAsync()
    {
        var failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = Create();
            }
            catch (Exception ex)
            {
                // Another copy of Glint for this user already serves the pipe, or the system refused; try again later.
                if (failures++ == 0) App.LogError(ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, 5 * failures)), _stop.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }
            failures = 0;
            try
            {
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception)
            {
                pipe.Dispose();
                if (_stop.IsCancellationRequested) return;
                continue;
            }
            // The next instance is created right away, so concurrent sessions never wait on each other.
            _ = HandleAsync(pipe);
        }
    }

    NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        using (var identity = WindowsIdentity.GetCurrent())
        {
            security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        }
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    async Task HandleAsync(NamedPipeServerStream pipe)
    {
        using (pipe)
        {
            try
            {
                string? line;
                // A client that connects and says nothing must not hold an instance forever.
                using (var first = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
                {
                    first.CancelAfter(TimeSpan.FromSeconds(10));
                    line = await AgentProtocol.ReadLineAsync(pipe, first.Token).ConfigureAwait(false);
                }
                if (string.IsNullOrWhiteSpace(line) || JsonNode.Parse(line) is not JsonObject envelope) return;
                if (envelope["payload"] is not JsonObject payload) return;
                var source = AgentProtocol.Str(envelope, "source") ?? AgentProtocol.Claude;
                var wait = envelope["wait"] is JsonValue w && w.TryGetValue<bool>(out var b) && b;
                var t = envelope["t"] is JsonValue tv && tv.TryGetValue<long>(out var tl) ? tl : 0;
                var console = envelope["console"] is JsonValue cv && cv.TryGetValue<long>(out var cl) ? new IntPtr(cl) : IntPtr.Zero;

                var process = LookupProcess(pipe, source, payload, console);

                if (!wait)
                {
                    await _ui.InvokeAsync(() => _hub.OnStatus(source, payload, t, process));
                    await AgentProtocol.WriteLineAsync(pipe, "{}", _stop.Token).ConfigureAwait(false);
                    return;
                }

                var request = await _ui.InvokeAsync(() => _hub.OpenRequest(source, payload, process));
                if (request is null)
                {
                    await AgentProtocol.WriteLineAsync(pipe, Reply(AgentProtocol.Pass), _stop.Token).ConfigureAwait(false);
                    return;
                }

                // The hook writes nothing more, so a read that returns means it was killed (the agent's own
                // timeout, Ctrl+C, a closed terminal): the request is withdrawn.
                using var watch = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                var gone = WaitForCloseAsync(pipe, watch.Token);
                var timeout = Task.Delay(request.Remaining, watch.Token);
                var done = await Task.WhenAny(request.Reply.Task, gone, timeout).ConfigureAwait(false);
                if (done == request.Reply.Task)
                {
                    watch.Cancel();
                    await AgentProtocol.WriteLineAsync(pipe, request.Reply.Task.Result.ToJsonString(AgentProtocol.Compact), CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                var withdrawn = done == gone;
                watch.Cancel();
                await _ui.InvokeAsync(() => _hub.CloseRequest(request, withdrawn ? AgentHub.CloseReason.Withdrawn : AgentHub.CloseReason.Timeout));
                // Timed out: the agent asks in its terminal as usual.
                if (!withdrawn) await AgentProtocol.WriteLineAsync(pipe, Reply(AgentProtocol.Pass), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // The hook went away or Glint is closing; nothing to answer.
            }
            catch (Exception ex)
            {
                App.LogError(ex);
            }
        }
    }

    static async Task WaitForCloseAsync(PipeStream pipe, CancellationToken ct)
    {
        var one = new byte[1];
        try
        {
            while (await pipe.ReadAsync(one.AsMemory(), ct).ConfigureAwait(false) > 0)
            {
                // A well-behaved hook sends nothing after its line; ignore anything that does come.
            }
        }
        catch (IOException)
        {
            // Broken pipe: closed.
        }
        catch (OperationCanceledException)
        {
            // Cancelled only after an answer or the timeout won the WhenAny, so finishing now changes nothing.
        }
    }

    internal static string Reply(string decision) => new JsonObject { ["decision"] = decision }.ToJsonString();

    AgentProcessInfo? LookupProcess(NamedPipeServerStream pipe, string source, JsonObject payload, IntPtr console)
    {
        var key = AgentHub.SessionKey(source, payload);
        if (key is null) return null;
        // A resumed conversation (same id, new terminal) or a late hook must not get a dead process or a closed window.
        if (_processes.TryGetValue(key, out var known) && StillThere(known.Info, known.Started)) return known.Info;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out var pid)) return null;
        var found = AgentProcesses.Find((int)pid);
        // Under Windows Terminal the console window is a hidden stand-in, so only a visible one counts.
        var visibleConsole = console != IntPtr.Zero && AgentProcesses.IsVisible(console) ? console : IntPtr.Zero;
        if (visibleConsole != IntPtr.Zero && (found is null || found.Window == IntPtr.Zero))
            found = new AgentProcessInfo(found?.AgentPid ?? 0, visibleConsole);
        if (found is null) _processes.TryRemove(key, out _);
        else _processes[key] = (found, StartTime(found.AgentPid));
        return found;
    }

    static bool StillThere(AgentProcessInfo info, DateTime started) =>
        (info.AgentPid <= 0 || StartTime(info.AgentPid) == started) && (info.Window == IntPtr.Zero || AgentProcesses.Exists(info.Window));

    static DateTime StartTime(int pid)
    {
        if (pid <= 0) return DateTime.MinValue;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return p.StartTime;
        }
        catch (Exception)
        {
            // Gone, or not ours to ask.
            return DateTime.MinValue;
        }
    }

    /// <summary>The hub dropped the session; its next event looks the process up again.</summary>
    internal void ForgetProcess(string key) => _processes.TryRemove(key, out _);

    public void Dispose()
    {
        _stop.Cancel();
        _processes.Clear();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);
}
