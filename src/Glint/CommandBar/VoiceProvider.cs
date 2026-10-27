using Glint.Core;
using Glint.Services.Voice;

namespace Glint.CommandBar;

/// <summary>"диктовка", "голос": dictate into the window that was in front before the command bar opened.</summary>
public sealed class VoiceProvider : ICommandProvider
{
    static readonly string[] Keywords = { "диктовка", "диктовать", "надиктовать", "голос", "голосовой ввод", "dictate", "dictation", "voice" };

    public IEnumerable<CommandResult> Query(string query, CancellationToken ct)
    {
        var lower = query.Trim().ToLowerInvariant();
        if (lower.Length < 3 || !Keywords.Any(k => k.StartsWith(lower, StringComparison.Ordinal))) return Array.Empty<CommandResult>();
        var whole = Keywords.Contains(lower);

        var hotkey = AppServices.Settings.Current.Voice.Hotkey;
        var listening = DictationService.Shared.IsListening;
        return new[]
        {
            new CommandResult
            {
                Title = listening ? "Закончить диктовку" : "Голосовой ввод",
                Subtitle = listening ? "Вставить продиктованное" : string.IsNullOrEmpty(hotkey)
                    ? "Продиктовать текст в окно, где вы печатали"
                    : $"Продиктовать текст в окно, где вы печатали · {hotkey}",
                Hint = "Голос",
                Glyph = Glyphs.Microphone,
                Score = 90,
                PrefixOnly = !whole,
                // The bar has hidden itself by now, so the previous window is the one to type into.
                Execute = () => DictationService.Shared.ToggleForWindow(AppServices.Clipboard.PreviousWindow),
            },
        };
    }
}
