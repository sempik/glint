using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace Glint.Services.Agents;

/// <summary>
/// "Glint.exe --agent-hook claude|codex|codex-notify": what Claude Code and Codex start for each hook event.
/// Reads the event JSON from stdin (Codex's notify passes it as the last argument), hands it to the running Glint
/// over the pipe and, for a permission request or a question, waits for the user's choice and prints the answer.
/// </summary>
/// <remarks>
/// Runs before any WPF type is touched (see Program.Main): no window, no single-instance mutex, no settings file.
/// Whatever goes wrong, it exits 0 with nothing on stdout, which every agent reads as "no opinion": the agent then
/// carries on exactly as if Glint weren't installed. With Glint not running that takes a few milliseconds.
/// </remarks>
public static class AgentHookClient
{
    public static int Run(string[] args)
    {
        // Taken first: async hooks race each other, and the start order is the order the agent fired them in.
        var startedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            // Glint.exe is a GUI program, so Windows shows the "app starting" cursor until it reads its message
            // queue; a hook waiting for the user would keep that spinner up for seconds. One peek ends it.
            PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);

            var source = args.Length > 1 ? args[1].Trim().ToLowerInvariant() : AgentProtocol.Claude;
            if (source is not (AgentProtocol.Claude or AgentProtocol.Codex or AgentProtocol.CodexNotify)) return 0;
            var pipePath = @"\\.\pipe\" + AgentProtocol.PipeName;
            // No pipe means Glint isn't running (or agents are switched off): don't even read the input.
            if (!PipeExists(pipePath)) return 0;

            var json = source == AgentProtocol.CodexNotify ? (args.Length > 2 ? args[^1] : null) : ReadStdin();
            if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject payload) return 0;
            // Opened before the console lookup below, which may touch the standard handles.
            using var stdout = Console.OpenStandardOutput();

            var wait = AgentProtocol.NeedsDecision(source, payload);
            var output = Exchange(source, payload, wait, startedAt, ConsoleWindowOfParent());
            if (output is null) return 0;
            var bytes = new UTF8Encoding(false).GetBytes(output);
            stdout.Write(bytes, 0, bytes.Length);
            stdout.Flush();
        }
        catch (Exception)
        {
            // Never in the agent's way: no output is "ask as usual".
        }
        return 0;
    }

    static string? Exchange(string source, JsonObject payload, bool wait, long startedAt, IntPtr console)
    {
        using var pipe = new NamedPipeClientStream(".", AgentProtocol.PipeName, PipeDirection.InOut,
            // CurrentUserOnly also checks that the pipe belongs to this user, so nobody else can pose as Glint.
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        pipe.Connect(1500);

        var envelope = new JsonObject
        {
            ["v"] = 1,
            ["source"] = source,
            ["wait"] = wait,
            ["t"] = startedAt,
            ["payload"] = payload.DeepClone(),
        };
        // A classic console window has no owner process with a main window; the server can't find it by walking up.
        if (console != IntPtr.Zero) envelope["console"] = console.ToInt64();
        // Status events get an empty acknowledgement at once; a decision can take up to the 10-minute cap.
        var limit = wait ? TimeSpan.FromSeconds(AgentProtocol.MaxWaitSeconds + 5) : TimeSpan.FromSeconds(3);
        using var cts = new CancellationTokenSource(limit);
        var task = Task.Run(async () =>
        {
            await AgentProtocol.WriteLineAsync(pipe, envelope.ToJsonString(AgentProtocol.Compact), cts.Token).ConfigureAwait(false);
            return await AgentProtocol.ReadLineAsync(pipe, cts.Token).ConfigureAwait(false);
        });
        string? line;
        try
        {
            line = task.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        if (!wait || string.IsNullOrWhiteSpace(line) || JsonNode.Parse(line) is not JsonObject reply) return null;
        return AgentProtocol.BuildOutput(source, payload, reply);
    }

    static string? ReadStdin()
    {
        try
        {
            using var stdin = Console.OpenStandardInput();
            using var reader = new StreamReader(stdin, new UTF8Encoding(false));
            var text = reader.ReadToEnd();
            return text.Length > AgentProtocol.MaxLineBytes ? null : text;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// WaitNamedPipe answers "no such pipe" immediately, while NamedPipeClientStream.Connect would keep retrying
    /// until its timeout: the no-Glint case should cost the agent nothing.
    /// </summary>
    static bool PipeExists(string path)
    {
        if (WaitNamedPipe(path, 1)) return true;
        // Busy (every instance taken for a moment) still means Glint is there.
        return Marshal.GetLastWin32Error() is ERROR_SEM_TIMEOUT or ERROR_PIPE_BUSY;
    }

    /// <summary>The console window of the agent's terminal, if it has one (conhost); zero otherwise.</summary>
    static IntPtr ConsoleWindowOfParent()
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS)) return IntPtr.Zero;
            var window = GetConsoleWindow();
            FreeConsole();
            return window;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    const int ERROR_SEM_TIMEOUT = 121, ERROR_PIPE_BUSY = 231;
    const uint PM_NOREMOVE = 0;
    const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool FreeConsole();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "WaitNamedPipeW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WaitNamedPipe(string name, uint timeout);
}
