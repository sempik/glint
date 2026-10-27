using Glint.Capture;
using Glint.Core;

namespace Glint.CommandBar;

/// <summary>"скриншот", "qr", "текст с экрана", "пипетка", "запись": the screen tools from Главная.</summary>
public sealed class ScreenToolsProvider : ICommandProvider
{
    sealed record Tool(string Title, string Subtitle, string Glyph, string[] Keywords, Action Run)
    {
        public string Lower { get; } = Title.ToLowerInvariant();
        public string Initials { get; } = Matcher.Initials(Title);
    }

    // Built per query: the recording entry reads differently while a recording runs.
    static Tool[] Tools()
    {
        var recording = ScreenTools.IsRecording;
        return new[]
        {
            new Tool("Скриншот", "Область или окно, потом пометки", Glyphs.Camera,
                new[] { "скриншот", "скрин", "снимок", "screenshot", "screen", "snip", "область" }, ScreenTools.CaptureArea),
            new Tool("Скриншот всего экрана", "Все мониторы, потом пометки", Glyphs.Image,
                new[] { "весь экран", "скриншот экрана", "full screen", "экран" }, ScreenTools.CaptureFullScreen),
            new Tool(recording ? "Остановить запись экрана" : "Запись экрана",
                recording ? "Сохранить MP4 в «Видео\\Glint»" : "Монитор или область в MP4" + (AppServices.Settings.Current.Capture.RecordSystemAudio ? ", со звуком" : ""),
                recording ? Glyphs.Stop : Glyphs.Record,
                // "стоп" only means something while a recording runs; Enter does what the row says, even if the state changed since.
                recording
                    ? new[] { "запись", "запись экрана", "остановить", "стоп", "stop", "record", "видео", "video" }
                    : new[] { "запись", "запись экрана", "записать", "record", "видео", "video" },
                recording ? () => _ = ScreenTools.StopRecordingAsync(null) : ScreenTools.StartRecording),
            new Tool("Текст с экрана", "Распознать и скопировать", Glyphs.ScanText,
                new[] { "текст с экрана", "текст", "распознать", "ocr", "text" }, ScreenTools.ReadText),
            new Tool("QR-код с экрана", "Прочитать QR или штрихкод", Glyphs.QrCode,
                new[] { "qr", "кьюар", "куар", "штрихкод", "barcode", "код" }, ScreenTools.ScanQr),
            new Tool("Пипетка", "Цвет с экрана под лупой, копирует HEX", Glyphs.Eyedropper,
                new[] { "пипетка", "цвет", "color", "picker", "eyedropper", "hex" }, ScreenTools.PickColor),
        };
    }

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var lower = query.Trim().ToLowerInvariant();
        if (lower.Length < 2) return Array.Empty<CommandResult>();
        var list = new List<CommandResult>();
        foreach (var t in Tools())
        {
            var score = Matcher.Score(t.Lower, t.Initials, lower, fuzzy: false);
            // A keyword typed in part ("скр") or in full with more after it ("запись экрана").
            if (t.Keywords.Any(k => k.StartsWith(lower, StringComparison.Ordinal) || lower == k)) score = Math.Max(score, 90);
            if (score > 0) list.Add(ToResult(t, score));
        }
        return list.OrderByDescending(r => r.Score).ToList();
    }

    /// <summary>Shown when the query is empty: the screenshot, or the stop button while recording.</summary>
    public static IEnumerable<CommandResult> QuickSuggestions()
    {
        var tools = Tools();
        yield return ToResult(ScreenTools.IsRecording ? tools[2] : tools[0], 0);
    }

    static CommandResult ToResult(Tool t, double score) => new()
    {
        Title = t.Title,
        Subtitle = t.Subtitle,
        Hint = "Экран",
        Glyph = t.Glyph,
        Score = score,
        Execute = t.Run,
    };
}
