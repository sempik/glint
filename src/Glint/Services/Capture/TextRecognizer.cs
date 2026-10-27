using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Glint.Services.Capture;

/// <summary>Recognised text and the OCR language that produced it.</summary>
public sealed record RecognizedText(string Text, string LanguageName);

/// <summary>Thrown when Windows has no OCR language installed at all.</summary>
public sealed class NoOcrLanguageException : Exception
{
    public NoOcrLanguageException() : base("No OCR language is installed.") { }
}

/// <summary>
/// Text from the screen with the OCR built into Windows (Windows.Media.Ocr), entirely offline. Each OCR engine
/// reads one language; Russian and English are tried when both are installed and the cleaner result wins.
/// </summary>
public static class TextRecognizer
{
    /// <summary>Russian and English first (most of what people read), then the user's other languages.</summary>
    static readonly string[] Preferred = { "ru", "en" };

    /// <summary>True when at least one OCR language is installed.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    /// <summary>Null when nothing readable was found. Throws <see cref="NoOcrLanguageException"/> without OCR languages.</summary>
    public static async Task<RecognizedText?> RecognizeAsync(byte[] bgra, int width, int height)
    {
        var engines = PickEngines();
        if (engines.Count == 0) throw new NoOcrLanguageException();

        using var bitmap = await Task.Run(() => Prepare(bgra, width, height));
        var results = new List<(OcrEngine Engine, OcrResult Result)>();
        foreach (var engine in engines) results.Add((engine, await engine.RecognizeAsync(bitmap)));

        var ru = results.FirstOrDefault(r => Is(r.Engine, "ru"));
        var en = results.FirstOrDefault(r => Is(r.Engine, "en"));
        if (ru.Result is not null && en.Result is not null) return Merge(ru, en);

        // A single engine, or languages other than these two: the cleanest reading wins.
        return results
            .Select(r => new RecognizedText(JoinLines(r.Result.Lines.Select(l => l.Text)), r.Engine.RecognizerLanguage.NativeName))
            .Where(r => r.Text.Length > 0)
            .MaxBy(r => Score(r.Text));
    }

    static bool Is(OcrEngine engine, string tag) => engine.RecognizerLanguage.LanguageTag.StartsWith(tag, StringComparison.OrdinalIgnoreCase);

    static string JoinLines(IEnumerable<string> lines) => string.Join(Environment.NewLine, lines).Trim();

    /// <summary>
    /// Line by line: the Russian engine reads Cyrillic and most Latin text well, but turns some English words
    /// into lookalikes ("Open" → "Ореп"), while the English engine makes Latin garbage of Cyrillic. So a line
    /// that is mostly Latin is taken from the English reading of the same spot, the rest from the Russian one.
    /// </summary>
    static RecognizedText? Merge((OcrEngine Engine, OcrResult Result) ru, (OcrEngine Engine, OcrResult Result) en)
    {
        var lines = new List<string>();
        bool usedRu = false, usedEn = false;
        foreach (var line in ru.Result.Lines)
        {
            int cyrWords = 0, latWords = 0;
            foreach (var word in line.Words)
            {
                var (cyr, lat) = CountScripts(word.Text);
                if (cyr > lat) cyrWords++;
                else if (lat > cyr) latWords++;
            }
            if (latWords > 0 && latWords >= cyrWords && MatchLine(en.Result, Bounds(line)) is { } match)
            {
                lines.Add(match.Text);
                usedEn = true;
            }
            else
            {
                lines.Add(line.Text);
                usedRu = true;
            }
        }
        // The Russian engine found nothing at all; the English one may still have.
        if (lines.Count == 0)
        {
            lines.AddRange(en.Result.Lines.Select(l => l.Text));
            usedEn = lines.Count > 0;
        }
        var text = JoinLines(lines);
        if (text.Length == 0) return null;
        var language = usedRu && usedEn
            ? $"{ru.Engine.RecognizerLanguage.NativeName} + {en.Engine.RecognizerLanguage.NativeName}"
            : (usedEn ? en : ru).Engine.RecognizerLanguage.NativeName;
        return new RecognizedText(text, language);
    }

    static (int Cyrillic, int Latin) CountScripts(string text)
    {
        int cyr = 0, lat = 0;
        foreach (var c in text)
        {
            if (IsCyrillic(c)) cyr++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') lat++;
        }
        return (cyr, lat);
    }

    static Windows.Foundation.Rect Bounds(OcrLine line)
    {
        var r = Windows.Foundation.Rect.Empty;
        foreach (var word in line.Words) r.Union(word.BoundingRect);
        return r;
    }

    /// <summary>The line of the other reading that sits at the same height, if any.</summary>
    static OcrLine? MatchLine(OcrResult other, Windows.Foundation.Rect bounds)
    {
        OcrLine? best = null;
        double bestOverlap = 0.5;
        foreach (var line in other.Lines)
        {
            var b = Bounds(line);
            if (b.IsEmpty || bounds.IsEmpty) continue;
            var vertical = Math.Min(b.Bottom, bounds.Bottom) - Math.Max(b.Top, bounds.Top);
            var horizontal = Math.Min(b.Right, bounds.Right) - Math.Max(b.Left, bounds.Left);
            if (horizontal <= 0) continue;
            var overlap = vertical / Math.Max(1, Math.Min(b.Height, bounds.Height));
            if (overlap <= bestOverlap) continue;
            bestOverlap = overlap;
            best = line;
        }
        return best;
    }

    static bool IsCyrillic(char c) => c is >= 'А' and <= 'я' or 'Ё' or 'ё';

    /// <summary>One engine per preferred language that OCR supports here, else the user's own default.</summary>
    static List<OcrEngine> PickEngines()
    {
        var engines = new List<OcrEngine>();
        IReadOnlyList<Language> available;
        try
        {
            available = OcrEngine.AvailableRecognizerLanguages;
        }
        catch (Exception)
        {
            return engines;
        }
        foreach (var tag in Preferred)
        {
            var language = available.FirstOrDefault(l => l.LanguageTag.StartsWith(tag, StringComparison.OrdinalIgnoreCase));
            if (language is not null && OcrEngine.TryCreateFromLanguage(language) is { } engine) engines.Add(engine);
        }
        if (engines.Count == 0 && OcrEngine.TryCreateFromUserProfileLanguages() is { } profile) engines.Add(profile);
        if (engines.Count == 0 && available.Count > 0 && OcrEngine.TryCreateFromLanguage(available[0]) is { } any) engines.Add(any);
        return engines;
    }

    /// <summary>
    /// Small text reads much better enlarged, and the engine refuses images over its size limit; scale into
    /// the range it likes.
    /// </summary>
    static SoftwareBitmap Prepare(byte[] bgra, int width, int height)
    {
        BitmapSource source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, bgra, width * 4);
        var max = (double)OcrEngine.MaxImageDimension;
        var scale = 1.0;
        // Screen text is ~12-16 px tall; OCR is happiest around twice that.
        if (height < 400 && width < max / 2.5) scale = Math.Min(2.5, max / Math.Max(width, height));
        if (Math.Max(width, height) * scale > max) scale = max / Math.Max(width, height);
        if (Math.Abs(scale - 1) > 0.01)
        {
            var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            source = scaled;
        }
        var w = source.PixelWidth;
        var h = source.PixelHeight;
        var pixels = new byte[w * h * 4];
        source.CopyPixels(pixels, w * 4, 0);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 0xFF;
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
    }

    /// <summary>
    /// How believable a result is. An engine reading the other alphabet produces words that mix Cyrillic and
    /// Latin letters ("Пpивeт") or lone symbols, so those cost; clean words earn their length.
    /// </summary>
    internal static double Score(string text)
    {
        double score = 0;
        foreach (var word in text.Split(new[] { ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int cyr = 0, lat = 0, digits = 0, other = 0;
            foreach (var c in word)
            {
                if (IsCyrillic(c)) cyr++;
                else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') lat++;
                else if (char.IsDigit(c)) digits++;
                else if (!char.IsPunctuation(c)) other++;
            }
            if (cyr > 0 && lat > 0) score -= word.Length;
            else score += cyr + lat + digits;
            score -= other * 0.5;
        }
        return score;
    }
}
