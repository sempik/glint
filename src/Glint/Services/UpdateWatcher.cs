using System.Net.Http;
using System.Windows.Threading;
using Glint.Core;
using Glint.Pill;

namespace Glint.Services;

/// <summary>
/// Optional automatic update check (Настройки → О программе, off by default): once shortly after launch, then
/// every 6 hours, one request to the GitHub releases API each time. A newer version shows a clickable flash that
/// leads to the install button in Настройки; each version is announced once per session.
/// </summary>
public sealed class UpdateWatcher
{
    static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45), Interval = TimeSpan.FromHours(6);

    static readonly TimeSpan AnnounceRetry = TimeSpan.FromSeconds(20);

    // One timer serves both waits: the short one after launch, then the long one.
    readonly DispatcherTimer _timer = new();
    // Runs only while a found version waits for the pill to be free (a fullscreen app, the open panel).
    readonly DispatcherTimer _announceTimer = new() { Interval = AnnounceRetry };
    Version? _announced;
    UpdateInfo? _waiting;
    bool _checking;

    public void Start()
    {
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            await CheckAsync();
        };
        _announceTimer.Tick += (_, _) => Announce();
        AppServices.Settings.Changed += (_, _) => Apply();
        Apply();
    }

    void Apply()
    {
        var on = AppServices.Settings.Current.AutoUpdateCheck;
        if (on == _timer.IsEnabled) return;
        if (on)
        {
            // Turning the option on is a good moment to look, but not instantly in the middle of a click.
            _timer.Interval = FirstDelay;
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _announceTimer.Stop();
            _waiting = null;
        }
    }

    async Task CheckAsync()
    {
        if (_checking || !AppServices.Settings.Current.AutoUpdateCheck) return;
        _checking = true;
        try
        {
            var update = await UpdateService.CheckAsync();
            if (update is null || update.Version == _announced) return;
            _waiting = update;
            Announce();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Offline or GitHub is busy; the next round tries again.
        }
        catch (Exception ex)
        {
            App.LogError(ex);
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Shows the flash once the pill can show it: a plain flash is dropped over a fullscreen app and expires
    /// behind the open panel, and the version would then count as announced without anyone seeing it.
    /// </summary>
    void Announce()
    {
        if (_waiting is not { } update) return;
        if (AppServices.Notifier is PillWindow { CanFlashNow: false })
        {
            if (!_announceTimer.IsEnabled) _announceTimer.Start();
            return;
        }
        _announceTimer.Stop();
        _waiting = null;
        _announced = update.Version;
        AppServices.Notifier.FlashClickable(
            "Доступно обновление",
            $"Glint {update.Version.ToString(3)} · нажмите, чтобы открыть",
            Glyphs.Download,
            ShowInSettings,
            TimeSpan.FromSeconds(8));
    }

    static void ShowInSettings()
    {
        if (AppServices.Notifier is PillWindow pill) pill.ShowUpdate();
    }
}
