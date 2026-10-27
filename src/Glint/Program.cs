using System.Runtime.CompilerServices;
using Glint.Services.Agents;

namespace Glint;

/// <summary>
/// Entry point. Claude Code and Codex start Glint.exe for every hook event ("--agent-hook"); that mode must start
/// in milliseconds and stay invisible, so it is handled before WPF, the single-instance mutex or the settings are
/// touched. Everything else is the normal app.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == AgentProtocol.Flag) return AgentHookClient.Run(args);
        return RunApp();
    }

    /// <summary>Kept out of Main so the hook mode doesn't even load the WPF assemblies to JIT it.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static int RunApp()
    {
        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
