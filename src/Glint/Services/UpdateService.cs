using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace Glint.Services;

public sealed record UpdateInfo(Version Version, string Tag, string ExeUrl, string? SumsUrl, string PageUrl);

/// <summary>
/// Updates Glint from the latest GitHub release. Runs only when the user presses the button: no background
/// checks. The new exe is verified against the release's SHA256SUMS.txt, then swapped in by renaming the
/// running file (Windows allows renaming, not overwriting, a running exe) and started with --updated.
/// </summary>
public static class UpdateService
{
    const string LatestApi = "https://api.github.com/repos/sempik/glint/releases/latest";
    const string ExeAsset = "Glint.exe";
    const string SumsAsset = "SHA256SUMS.txt";
    public const string ReleasesPage = "https://github.com/sempik/glint/releases/latest";
    /// <summary>Command-line flag of the freshly installed copy; it waits for the old one to exit.</summary>
    public const string UpdatedFlag = "--updated";

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"Glint/{CurrentVersion}");
        return client;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>
    /// Only the published single-file exe can replace itself; a dev build (Glint.dll next to the exe) can't.
    /// Single-file apps have an empty Assembly.Location.
    /// </summary>
    public static bool CanSelfUpdate =>
        string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location) && Environment.ProcessPath is not null;

    /// <summary>The latest release if it is newer than this build, otherwise null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var response = await Http.GetAsync(LatestApi, ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        // "v0.2.0-beta" or "v0.2.0+build" still name version 0.2.0; System.Version rejects the suffix.
        var core = tag.TrimStart('v', 'V').Split('-', '+')[0];
        if (!Version.TryParse(core, out var version))
            throw new InvalidDataException($"Не удалось распознать версию релиза «{tag}».");
        version = new Version(version.Major, version.Minor, Math.Max(0, version.Build));
        if (version <= CurrentVersion) return null;

        string? exe = null, sums = null;
        foreach (var asset in root.GetProperty("assets").EnumerateArray())
        {
            var name = asset.GetProperty("name").GetString();
            var url = asset.GetProperty("browser_download_url").GetString();
            if (string.Equals(name, ExeAsset, StringComparison.OrdinalIgnoreCase)) exe = url;
            else if (string.Equals(name, SumsAsset, StringComparison.OrdinalIgnoreCase)) sums = url;
        }
        if (exe is null) return null;
        var page = root.TryGetProperty("html_url", out var html) ? html.GetString() ?? ReleasesPage : ReleasesPage;
        return new UpdateInfo(version, tag, exe, sums, page);
    }

    /// <summary>Downloads, verifies and swaps in the new exe, then starts it. The caller shuts the app down.</summary>
    public static async Task InstallAsync(UpdateInfo update, IProgress<double>? progress, CancellationToken ct = default)
    {
        var exePath = Environment.ProcessPath ?? throw new InvalidOperationException("Не удалось определить путь к Glint.exe.");
        var dir = Path.GetDirectoryName(exePath)!;
        var downloaded = Path.Combine(dir, "Glint.exe.new");
        var old = Path.Combine(dir, "Glint.exe.old");

        try
        {
            await DownloadAsync(update.ExeUrl, downloaded, progress, ct);
            await VerifyAsync(update, downloaded, ct);

            // A leftover .old may be the image of a copy still running after a failed launch; don't let it block.
            if (File.Exists(old))
            {
                try { File.Delete(old); }
                catch (UnauthorizedAccessException) { old = Path.Combine(dir, $"Glint.exe.old{Environment.ProcessId}"); }
                catch (IOException) { old = Path.Combine(dir, $"Glint.exe.old{Environment.ProcessId}"); }
            }
            File.Move(exePath, old);
            try
            {
                File.Move(downloaded, exePath);
                Process.Start(new ProcessStartInfo(exePath, UpdatedFlag) { UseShellExecute = false, WorkingDirectory = dir })?.Dispose();
            }
            catch
            {
                // Put the working copy back rather than leave a Glint.exe that can't start (or none at all).
                if (File.Exists(exePath)) File.Move(exePath, downloaded, overwrite: true);
                File.Move(old, exePath);
                throw;
            }
        }
        catch
        {
            TryDelete(downloaded);
            throw;
        }
    }

    /// <summary>The download stopped receiving data (dropped Wi-Fi, a VPN reconnect); distinct from a user cancel.</summary>
    public sealed class StalledException : IOException
    {
        public StalledException() : base("Скачивание остановилось: данные перестали приходить.") { }
    }

    /// <summary>Removes the previous exe left behind by an update; it can't be deleted while it is still running.</summary>
    public static void CleanupOldVersion()
    {
        if (Environment.ProcessPath is not { } exe || Path.GetDirectoryName(exe) is not { } dir) return;
        try
        {
            // Also Glint.exe.old<pid>, used when a plain .old was still locked.
            foreach (var file in Directory.EnumerateFiles(dir, "Glint.exe.old*")) TryDelete(file);
        }
        catch (Exception)
        {
            // Unreadable folder; nothing to clean.
        }
    }

    static async Task DownloadAsync(string url, string target, IProgress<double>? progress, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        var buffer = new byte[81920];
        long read = 0;
        // HttpClient.Timeout stops at the headers; a black-holed connection would otherwise hang the read forever.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        while (true)
        {
            idle.CancelAfter(ReadIdleTimeout);
            int n;
            try
            {
                n = await source.ReadAsync(buffer, idle.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new StalledException();
            }
            if (n == 0) break;
            await file.WriteAsync(buffer.AsMemory(0, n), ct);
            read += n;
            if (total > 0) progress?.Report((double)read / total.Value);
        }
        if (total > 0 && read != total) throw new IOException("Скачивание оборвалось.");
    }

    static readonly TimeSpan ReadIdleTimeout = TimeSpan.FromSeconds(30);

    static async Task VerifyAsync(UpdateInfo update, string file, CancellationToken ct)
    {
        // Older releases may lack the sums file; then a successful HTTPS download from GitHub is all we have.
        if (update.SumsUrl is null) return;
        var sums = await Http.GetStringAsync(update.SumsUrl, ct);
        var expected = sums.Split('\n')
            .Select(line => line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2 && string.Equals(parts[1].Trim().TrimStart('*'), ExeAsset, StringComparison.OrdinalIgnoreCase))
            .Select(parts => parts[0])
            .FirstOrDefault();
        // The release publishes sums but not for the exe: something is off, don't install unchecked.
        if (expected is null) throw new InvalidDataException("В SHA256SUMS.txt нет строки для Glint.exe.");

        await using var stream = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Файл повреждён при скачивании: не совпала контрольная сумма.");
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception)
        {
            // Still locked or no rights; it will be retried on the next start.
        }
    }
}
