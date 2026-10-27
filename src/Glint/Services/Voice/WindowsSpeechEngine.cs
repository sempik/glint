using Microsoft.Win32;
using Windows.Media.SpeechRecognition;

namespace Glint.Services.Voice;

/// <summary>
/// Dictation with the Windows speech recognizer (Windows.Media.SpeechRecognition, continuous session with the
/// dictation topic). It works from an unpackaged app, but needs a speech language installed in Windows and
/// "Online speech recognition" on: Windows sends the audio to Microsoft's service. Glint itself sends nothing.
/// A recognizer is created per session and disposed after, so the microphone is free between sessions.
/// </summary>
public sealed class WindowsSpeechEngine : IDictationEngine
{
    // HRESULTs seen from SpeechRecognizer.
    const int E_NOTFOUND = unchecked((int)0x80070490);            // no recognizer installed at all
    const int SPERR_NOT_SUPPORTED_LANG = unchecked((int)0x800455BC); // the requested language has no recognizer
    const int SPERR_PRIVACY_NOT_ACCEPTED = unchecked((int)0x80045509);
    const int E_ACCESSDENIED = unchecked((int)0x80070005);
    const int SPERR_NO_MICROPHONE = unchecked((int)0x8004503A);

    SpeechRecognizer? _recognizer;
    bool _stopping;

    public event Action<string>? Hypothesis;
    public event Action<string>? Phrase;
    public event Action<DictationProblem?>? Ended;

    public async Task StartAsync(string? languageTag)
    {
        try
        {
            var language = PickLanguage(languageTag);
            _recognizer = language is null ? new SpeechRecognizer() : new SpeechRecognizer(language);
            _recognizer.Constraints.Add(new SpeechRecognitionTopicConstraint(SpeechRecognitionScenario.Dictation, "dictation"));
            var compiled = await _recognizer.CompileConstraintsAsync();
            if (compiled.Status != SpeechRecognitionResultStatus.Success) throw new DictationException(Map(compiled.Status) ?? DictationProblem.Other, compiled.Status.ToString());

            // Give the user a moment to start, end a phrase on a short pause, end the session after a longer one.
            _recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(8);
            _recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.2);
            _recognizer.ContinuousRecognitionSession.AutoStopSilenceTimeout = TimeSpan.FromSeconds(6);
            _recognizer.HypothesisGenerated += OnHypothesis;
            _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResult;
            _recognizer.ContinuousRecognitionSession.Completed += OnCompleted;
            await _recognizer.ContinuousRecognitionSession.StartAsync();
        }
        catch (DictationException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new DictationException(DictationProblem.MicrophoneDenied, ex.Message, ex);
        }
        catch (Exception ex) when (Classify(ex.HResult) is { } problem)
        {
            throw new DictationException(problem, ex.Message, ex);
        }
    }

    public async Task StopAsync()
    {
        if (_recognizer is not { } r || _stopping) return;
        _stopping = true;
        try
        {
            if (r.State != SpeechRecognizerState.Idle) await r.ContinuousRecognitionSession.StopAsync();
        }
        catch (InvalidOperationException)
        {
            // The session already ended on silence.
        }
    }

    void OnHypothesis(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args) => Hypothesis?.Invoke(args.Hypothesis.Text);

    void OnResult(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        var result = args.Result;
        if (result.Status != SpeechRecognitionResultStatus.Success || result.Confidence == SpeechRecognitionConfidence.Rejected) return;
        if (!string.IsNullOrWhiteSpace(result.Text)) Phrase?.Invoke(result.Text);
    }

    void OnCompleted(SpeechContinuousRecognitionSession sender, SpeechContinuousRecognitionCompletedEventArgs args) =>
        Ended?.Invoke(_stopping ? null : Map(args.Status));

    /// <summary>Null for the ways a session normally ends (stopped, silence).</summary>
    static DictationProblem? Map(SpeechRecognitionResultStatus status) => status switch
    {
        SpeechRecognitionResultStatus.Success or SpeechRecognitionResultStatus.UserCanceled
            or SpeechRecognitionResultStatus.TimeoutExceeded or SpeechRecognitionResultStatus.PauseLimitExceeded => null,
        SpeechRecognitionResultStatus.TopicLanguageNotSupported or SpeechRecognitionResultStatus.GrammarLanguageMismatch => DictationProblem.NoLanguage,
        SpeechRecognitionResultStatus.MicrophoneUnavailable => DictationProblem.NoMicrophone,
        SpeechRecognitionResultStatus.NetworkFailure => DictationProblem.Network,
        _ => DictationProblem.Other,
    };

    static DictationProblem? Classify(int hresult) => hresult switch
    {
        E_NOTFOUND or SPERR_NOT_SUPPORTED_LANG => DictationProblem.NoLanguage,
        SPERR_PRIVACY_NOT_ACCEPTED => DictationProblem.PrivacyOff,
        E_ACCESSDENIED => DictationProblem.MicrophoneDenied,
        SPERR_NO_MICROPHONE => DictationProblem.NoMicrophone,
        _ => null,
    };

    /// <summary>
    /// The chosen language if Windows can dictate in it; null lets Windows use its own speech language (also when
    /// the chosen one was uninstalled since). With no languages at all the recognizer itself reports it.
    /// </summary>
    static Windows.Globalization.Language? PickLanguage(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var supported = SupportedLanguages();
        var exact = supported.Where(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));
        var sameLanguage = supported.Where(l => l.Tag.StartsWith(tag.Split('-')[0] + "-", StringComparison.OrdinalIgnoreCase));
        var match = exact.Concat(sameLanguage).Select(l => l.Tag).FirstOrDefault();
        return match is null ? null : new Windows.Globalization.Language(match);
    }

    /// <summary>Languages Windows can dictate in (installed speech packs). Empty when none or on error.</summary>
    public static IReadOnlyList<(string Tag, string Name)> SupportedLanguages()
    {
        try
        {
            return SpeechRecognizer.SupportedTopicLanguages.Select(l => (l.LanguageTag, l.NativeName)).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<(string, string)>();
        }
    }

    /// <summary>
    /// The "Online speech recognition" switch (Параметры → Конфиденциальность → Голосовые функции); dictation
    /// with this recognizer needs it. Null when Windows doesn't say.
    /// </summary>
    public static bool? OnlineRecognitionAllowed()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy");
            return key?.GetValue("HasAccepted") is int v ? v != 0 : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        var r = _recognizer;
        _recognizer = null;
        if (r is null) return;
        try
        {
            r.HypothesisGenerated -= OnHypothesis;
            r.ContinuousRecognitionSession.ResultGenerated -= OnResult;
            r.ContinuousRecognitionSession.Completed -= OnCompleted;
            r.Dispose();
        }
        catch (Exception)
        {
            // Disposing a recognizer whose session failed to start can throw; it's gone either way.
        }
    }
}
