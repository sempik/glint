using ZXing;
using ZXing.Common;

namespace Glint.Services.Capture;

/// <summary>A code found on the screen: its text and what kind of code it was.</summary>
public sealed record ScannedCode(string Text, string Format);

/// <summary>
/// Decodes QR codes (and the common barcode kinds) from raw BGRA pixels with ZXing. No imaging bindings: the
/// snapshot already has the bytes ZXing wants.
/// </summary>
public static class QrReader
{
    static readonly IList<BarcodeFormat> Formats = new[]
    {
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.ITF,
    };

    /// <summary>All codes in the image, best first; empty when there are none. CPU-bound, call off the UI thread.</summary>
    public static IReadOnlyList<ScannedCode> Decode(byte[] bgra, int width, int height)
    {
        if (width < 8 || height < 8) return Array.Empty<ScannedCode>();
        var found = DecodeOnce(bgra, width, height);
        // A small code on a high-DPI screen can be just a few pixels per module; give ZXing a bigger picture.
        if (found.Count == 0 && Math.Max(width, height) < 600)
        {
            var (big, w, h) = Upscale(bgra, width, height, 3);
            found = DecodeOnce(big, w, h);
        }
        return found;
    }

    static List<ScannedCode> DecodeOnce(byte[] bgra, int width, int height)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                // Dark-theme sites show light-on-dark codes.
                TryInverted = true,
                PossibleFormats = Formats,
            },
        };
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var results = reader.DecodeMultiple(source) ?? Array.Empty<Result>();
        return results
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .GroupBy(r => r.Text)
            .Select(g => new ScannedCode(g.Key, Describe(g.First().BarcodeFormat)))
            .ToList();
    }

    /// <summary>Nearest-neighbour enlargement, which keeps module edges sharp.</summary>
    static (byte[] Pixels, int Width, int Height) Upscale(byte[] bgra, int width, int height, int factor)
    {
        var w = width * factor;
        var h = height * factor;
        var result = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            var srcRow = (y / factor) * width * 4;
            var dstRow = y * w * 4;
            for (var x = 0; x < w; x++)
                Buffer.BlockCopy(bgra, srcRow + (x / factor) * 4, result, dstRow + x * 4, 4);
        }
        return (result, w, h);
    }

    static string Describe(BarcodeFormat format) => format switch
    {
        BarcodeFormat.QR_CODE => "QR-код",
        BarcodeFormat.DATA_MATRIX => "Data Matrix",
        BarcodeFormat.AZTEC => "Aztec",
        BarcodeFormat.PDF_417 => "PDF417",
        _ => "Штрихкод " + format.ToString().Replace('_', '-'),
    };

    // ───────────── What the text means ─────────────

    /// <summary>A web address the result window can open, or null.</summary>
    public static Uri? AsLink(string text)
    {
        var t = text.Trim();
        if (t.Contains('\n') || t.Contains(' ')) return null;
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) t = "https://" + t;
        return Uri.TryCreate(t, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    /// <summary>"WIFI:T:WPA;S:Home;P:secret;;" → (network, password); null for anything else.</summary>
    public static (string Ssid, string? Password)? AsWifi(string text)
    {
        if (!text.StartsWith("WIFI:", StringComparison.OrdinalIgnoreCase)) return null;
        string? ssid = null, password = null;
        foreach (var field in SplitEscaped(text[5..]))
        {
            if (field.Length < 2 || field[1] != ':') continue;
            var value = field[2..];
            switch (char.ToUpperInvariant(field[0]))
            {
                case 'S': ssid = value; break;
                case 'P': password = value; break;
            }
        }
        return string.IsNullOrEmpty(ssid) ? null : (ssid, string.IsNullOrEmpty(password) ? null : password);
    }

    /// <summary>Splits on ';' honouring the backslash escapes the Wi-Fi format uses.</summary>
    static IEnumerable<string> SplitEscaped(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[++i]);
            }
            else if (c == ';')
            {
                yield return sb.ToString();
                sb.Clear();
            }
            else
            {
                sb.Append(c);
            }
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
