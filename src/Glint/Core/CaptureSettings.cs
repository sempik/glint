namespace Glint.Core;

/// <summary>
/// Screen tools: screenshots with markup, QR and text from the screen, the eyedropper and screen recording.
/// All of it works locally; nothing here touches the network.
/// </summary>
public sealed class CaptureSettings
{
    /// <summary>After a screenshot, open the markup editor; off = copy and save straight away.</summary>
    public bool OpenEditor { get; set; } = true;
    /// <summary>The pill and panel stay out of Glint's own screenshots and recordings.</summary>
    public bool HideGlint { get; set; } = true;
    /// <summary>Mix what the speakers play (WASAPI loopback) into screen recordings.</summary>
    public bool RecordSystemAudio { get; set; } = true;
    public bool RecordCursor { get; set; } = true;
    /// <summary>30 or 60.</summary>
    public int RecordFrameRate { get; set; } = 30;

    public void Normalize()
    {
        RecordFrameRate = RecordFrameRate >= 45 ? 60 : 30;
    }
}
