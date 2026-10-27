namespace Glint.Mascots;

public enum MascotFace { Normal, Happy, Sing, Think, Alert, Sleep, Worried }

/// <summary>
/// Everything that can change between two frames of the character. The view redraws only when the quantized
/// pose differs from the one on screen, so a frame that moves nothing visible costs no rendering at all.
/// </summary>
public readonly record struct MascotPose
{
    /// <summary>Where the eyes look, -1..1 on each axis (positive is right / down).</summary>
    public double LookX { get; init; }
    public double LookY { get; init; }
    /// <summary>0 open .. 1 closed.</summary>
    public double Blink { get; init; }
    /// <summary>Jump height, 0..1 of what fits in the pill.</summary>
    public double Hop { get; init; }
    /// <summary>Added to the 1:1 shape: positive is flatter and wider (landing), negative taller (in the air).</summary>
    public double SquashDelta { get; init; }
    /// <summary>Sway, degrees around the feet.</summary>
    public double Tilt { get; init; }
    /// <summary>How wide the mouth opens while singing, 0..1.</summary>
    public double Open { get; init; }
    public MascotFace Face { get; init; }
    /// <summary>Lit thought dots while thinking, 1..3.</summary>
    public int Dots { get; init; }
    /// <summary>Drift of the sleeping Zzz, 0..1.</summary>
    public double Zzz { get; init; }
    /// <summary>A note rising while dancing, 0 = none, else 0..1 of its flight.</summary>
    public double Note { get; init; }

    /// <summary>Resting: eyes slightly down, toward whatever is under the pill.</summary>
    public static MascotPose Rest { get; } = new() { LookY = 0.25 };

    /// <summary>Rounded to steps finer than a pixel at the largest size, so equal frames compare equal.</summary>
    public MascotPose Quantized() => this with
    {
        LookX = Math.Round(LookX * 30) / 30,
        LookY = Math.Round(LookY * 30) / 30,
        Blink = Math.Round(Blink * 10) / 10,
        Hop = Math.Round(Hop * 40) / 40,
        SquashDelta = Math.Round(SquashDelta * 200) / 200,
        Tilt = Math.Round(Tilt * 2) / 2,
        Open = Math.Round(Open * 8) / 8,
        Zzz = Math.Round(Zzz * 24) / 24,
        Note = Math.Round(Note * 24) / 24,
    };
}
