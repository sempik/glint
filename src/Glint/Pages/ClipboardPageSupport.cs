using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Glint.Core;

namespace Glint.Pages;

/// <summary>Single-line-ish preview of a clip: whitespace collapsed, capped so huge texts don't slow layout.</summary>
public sealed class ClipboardPreviewConverter : IValueConverter
{
    const int MaxChars = 300;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ClipEntry entry) return string.Empty;
        if (entry.Kind == ClipKind.Image) return "Изображение";

        var text = entry.Text ?? string.Empty;
        var sb = new StringBuilder(Math.Min(text.Length, MaxChars));
        var pendingSpace = false;
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                pendingSpace = sb.Length > 0;
                continue;
            }
            if (pendingSpace) sb.Append(' ');
            pendingSpace = false;
            sb.Append(ch);
            if (sb.Length >= MaxChars) break;
        }
        return sb.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>"chrome · 5 мин"</summary>
public sealed class ClipboardMetaConverter : IValueConverter
{
    static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ClipEntry entry) return string.Empty;
        var time = RelativeTime(entry.Time);
        return string.IsNullOrEmpty(entry.SourceApp) ? time : $"{entry.SourceApp} · {time}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;

    public static string RelativeTime(DateTime time)
    {
        var now = DateTime.Now;
        var age = now - time;
        if (age < TimeSpan.FromMinutes(1)) return "только что";
        if (age < TimeSpan.FromHours(1)) return $"{(int)age.TotalMinutes} мин";
        if (time.Date == now.Date) return $"{(int)age.TotalHours} ч";
        if (time.Date == now.Date.AddDays(-1)) return "вчера";
        return time.ToString(time.Year == now.Year ? "d MMM" : "d MMM yyyy", Russian);
    }
}

/// <summary>Attached property that loads a small, frozen thumbnail off the UI thread into an <see cref="Image"/>.</summary>
public static class ClipboardThumb
{
    const int DecodeHeight = 96;

    // Thumbnails are tiny, but cap the cache anyway; UI thread only.
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached(
        "Path", typeof(string), typeof(ClipboardThumb), new PropertyMetadata(null, OnPathChanged));

    public static string? GetPath(DependencyObject element) => (string?)element.GetValue(PathProperty);

    public static void SetPath(DependencyObject element, string? value) => element.SetValue(PathProperty, value);

    static async void OnPathChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        image.Source = null;
        if (e.NewValue is not string path || path.Length == 0) return;

        if (Cache.TryGetValue(path, out var cached))
        {
            image.Source = cached;
            return;
        }

        try
        {
            var thumb = await Task.Run(() => Load(path));
            if (thumb is null) return;
            if (Cache.Count > 300) Cache.Clear();
            Cache[path] = thumb;
            // The container may have been recycled for another entry while we were decoding.
            if (string.Equals(GetPath(image), path, StringComparison.OrdinalIgnoreCase)) image.Source = thumb;
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
    }

    static ImageSource? Load(string path)
    {
        if (!File.Exists(path)) return null;
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        // OnLoad releases the file immediately so the service can delete it later.
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        bitmap.DecodePixelHeight = DecodeHeight;
        bitmap.UriSource = new Uri(path, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }
}
