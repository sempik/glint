using System.Text.Json.Serialization;

namespace Glint.Core;

/// <summary>
/// The character in the pill (Настройки → Персонаж): which one, how big, on which side, and what it reacts to.
/// Everything is local; the character reads the cursor position, the output level and the agents' state, and
/// draws nothing at all while nothing changes.
/// </summary>
public sealed class MascotSettings
{
    public const string None = "none";
    public const string DefaultCharacter = "puff";
    public const string Left = "left", Right = "right";

    /// <summary>Body sizes offered in Настройки, DIPs at 100 % (the pill is 36 tall).</summary>
    public static readonly int[] Sizes = { 20, 24, 28 };
    /// <summary>Minutes without mouse or keyboard input before the character falls asleep.</summary>
    public static readonly int[] SleepChoices = { 2, 5, 10, 15, 30 };

    /// <summary>Id from <see cref="Glint.Mascots.MascotDesigns"/>, or "none".</summary>
    public string Character { get; set; } = DefaultCharacter;
    public int Size { get; set; } = 24;
    /// <summary>"left" or "right" end of the pill.</summary>
    public string Side { get; set; } = Left;
    /// <summary>Eyes follow the cursor while it is near the pill.</summary>
    public bool FollowCursor { get; set; } = true;
    /// <summary>Bounces to whatever is playing (media controls or any app making sound).</summary>
    public bool Dance { get; set; } = true;
    /// <summary>Thinks while a connected coding agent (Claude Code, Codex) works, perks up when it waits for you.</summary>
    public bool ThinkWithAgents { get; set; } = true;
    /// <summary>Closes its eyes after <see cref="SleepMinutes"/> without input; wakes with the first move.</summary>
    public bool Sleep { get; set; } = true;
    public int SleepMinutes { get; set; } = 5;
    /// <summary>Paints the body in the theme's accents instead of the character's own colours.</summary>
    public bool ThemeColors { get; set; }

    [JsonIgnore]
    public bool Enabled => Character != None;

    public void Normalize()
    {
        if (Character != None && Glint.Mascots.MascotDesigns.Find(Character) is null) Character = DefaultCharacter;
        if (!Sizes.Contains(Size)) Size = Sizes.OrderBy(s => Math.Abs(s - Size)).First();
        if (Side != Right) Side = Left;
        if (!SleepChoices.Contains(SleepMinutes)) SleepMinutes = SleepChoices.OrderBy(m => Math.Abs(m - SleepMinutes)).First();
    }
}
