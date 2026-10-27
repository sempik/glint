using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Glint.Services.Agents;

/// <summary>The agent process behind a hook call and the window it runs in, found by walking up the parent chain.</summary>
public sealed record AgentProcessInfo(int AgentPid, IntPtr Window);

/// <summary>
/// Finds which terminal an agent runs in, so a click on its card or flash brings that window forward, and which
/// process to watch so a closed terminal removes its session. One process snapshot per new session, off the UI thread.
/// </summary>
public static class AgentProcesses
{
    // Wrappers between the agent and the hook: none of them is "the agent".
    static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd.exe", "powershell.exe", "pwsh.exe", "bash.exe", "sh.exe", "conhost.exe", "openconsole.exe", "glint.exe",
    };

    // Climbing past these would land on an unrelated window (an Explorer window, a service).
    static readonly HashSet<string> Stops = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "wininit.exe", "winlogon.exe", "userinit.exe", "sihost.exe",
    };

    public static AgentProcessInfo? Find(int hookPid)
    {
        try
        {
            var table = Snapshot();
            if (!table.TryGetValue(hookPid, out var self)) return null;
            int agent = 0;
            var window = IntPtr.Zero;
            var pid = self.Parent;
            for (var depth = 0; depth < 16 && pid > 4 && table.TryGetValue(pid, out var p); depth++)
            {
                if (Stops.Contains(p.Name)) break;
                if (agent == 0 && !Shells.Contains(p.Name)) agent = pid;
                if (agent != 0 && MainWindow(pid) is var w && w != IntPtr.Zero)
                {
                    window = w;
                    break;
                }
                pid = p.Parent;
            }
            return agent == 0 ? null : new AgentProcessInfo(agent, window);
        }
        catch (Exception)
        {
            return null;
        }
    }

    static IntPtr MainWindow(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainWindowHandle;
        }
        catch (Exception)
        {
            return IntPtr.Zero;
        }
    }

    /// <summary>Brings the agent's terminal forward (restored if minimized). Best effort: false if it can't.</summary>
    public static bool Focus(IntPtr window)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) return false;
        if (IsIconic(window)) ShowWindow(window, SW_RESTORE);
        return Glint.Core.Native.SetForegroundWindow(window);
    }

    public static bool IsForeground(IntPtr window) => window != IntPtr.Zero && Glint.Core.Native.GetForegroundWindow() == window;

    public static bool IsVisible(IntPtr window) => IsWindow(window) && IsWindowVisible(window);

    public static bool Exists(IntPtr window) => window != IntPtr.Zero && IsWindow(window);

    static Dictionary<int, (int Parent, string Name)> Snapshot()
    {
        var table = new Dictionary<int, (int, string)>();
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return table;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref entry)) return table;
            do table[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile);
            while (Process32NextW(snap, ref entry));
        }
        finally
        {
            CloseHandle(snap);
        }
        return table;
    }

    const uint TH32CS_SNAPPROCESS = 0x2;
    const int SW_RESTORE = 9;
    static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ShowWindow(IntPtr hwnd, int cmd);
}
