using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glint.Core;

namespace Glint.Services.Indicators;

/// <summary>
/// OBS recording indicator. By default it watches OBS's recording folder (read from its profile) and counts a
/// recording while the newest video there keeps growing; everything is event-driven until a file appears.
/// With obs-websocket enabled in settings it asks OBS directly on localhost instead, which also knows about
/// pauses and recordings to other folders.
/// </summary>
public sealed class ObsMonitor : IndicatorMonitor, IReconfigurable
{
    const string Id = "obs";
    static readonly string[] VideoExtensions = { ".mkv", ".mp4", ".mov", ".flv", ".ts", ".m3u8", ".fragmented_mp4", ".hybrid_mp4" };

    // What the monitor started with, to know when settings require a restart.
    (bool WebSocket, int Port, string? Password) _config;
    CancellationTokenSource? _cts;

    // File mode.
    static readonly TimeSpan GrowthCheck = TimeSpan.FromSeconds(3), PausedCheck = TimeSpan.FromSeconds(10);
    readonly object _gate = new();
    FileSystemWatcher? _recWatcher, _profileWatcher, _configWatcher;
    Timer? _poll, _rescan;
    string? _recFolder;
    string? _candidate;
    long _lastSize = -1;
    int _idleTicks;
    bool _paused;

    // Both modes.
    DateTime? _recordingSince;
    string? _lastOutput;

    public ObsMonitor(IndicatorHub hub) : base(hub) { }

    static IndicatorSettings S => AppServices.Settings.Current.Indicators;

    public bool NeedsRestart(IndicatorSettings s) =>
        _config != (s.ObsWebSocket, s.ObsWebSocketPort, s.ObsPasswordProtected);

    protected override void Start()
    {
        var s = S;
        _config = (s.ObsWebSocket, s.ObsWebSocketPort, s.ObsPasswordProtected);
        if (s.ObsWebSocket)
        {
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            var password = SecretBox.Unprotect(s.ObsPasswordProtected);
            var port = s.ObsWebSocketPort;
            _ = Task.Run(() => WebSocketLoopAsync(port, password, token));
        }
        else
        {
            StartFileMode();
        }
    }

    protected override void Stop()
    {
        _cts?.Cancel();
        _cts = null;
        _profileWatcher?.Dispose();
        _configWatcher?.Dispose();
        _rescan?.Dispose();
        _poll?.Dispose();
        _profileWatcher = _configWatcher = null;
        _rescan = _poll = null;
        lock (_gate)
        {
            // Under the lock: a rescan on a pool thread may be replacing it right now.
            _recWatcher?.Dispose();
            _recWatcher = null;
            _recFolder = null;
        }
        _candidate = null;
        _paused = false;
        _recordingSince = null;
        Hub.Remove(Id);
    }

    // ───────────── Shared state ─────────────

    void SetRecording(bool recording, string? outputPath = null, TimeSpan? alreadyFor = null)
    {
        if (!Running) return;
        if (outputPath is not null) _lastOutput = outputPath;
        if (recording)
        {
            var first = _recordingSince is null;
            _recordingSince ??= DateTime.Now - (alreadyFor ?? TimeSpan.Zero);
            Publish();
            if (first && alreadyFor is null) AppServices.Notifier.Flash("Идёт запись", "OBS", Glyphs.Record, TimeSpan.FromSeconds(1.6));
        }
        else if (_recordingSince is DateTime since)
        {
            _recordingSince = null;
            Hub.Remove(Id);
            var length = FormatDuration(DateTime.Now - since);
            if (_lastOutput is { } path && File.Exists(path))
                AppServices.Notifier.FlashClickable("Запись сохранена", $"{Path.GetFileName(path)} · {length}", Glyphs.Record, () => IndicatorNative.ShowInExplorer(path), TimeSpan.FromSeconds(5));
            else
                AppServices.Notifier.Flash("Запись остановлена", length, Glyphs.Record, TimeSpan.FromSeconds(2.4));
        }
    }

    void Publish()
    {
        if (_recordingSince is not DateTime since) return;
        var output = _lastOutput;
        Hub.Set(new Indicator(Id, Glyphs.Record, "DangerBrush", $"Запись OBS с {since:HH:mm}" + (output is null ? "" : $"\n{Path.GetFileName(output)}"))
        {
            // A start time stays true without ticking; a running counter would repaint every second.
            Label = $"с {since:HH:mm}",
            Order = 2,
            Click = output is null ? null : () => IndicatorNative.ShowInExplorer(output),
        }, this);
    }

    static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";

    // ───────────── File mode ─────────────

    void StartFileMode()
    {
        _poll = new Timer(_ => PollCandidate());
        _rescan = new Timer(_ => WatchRecordingFolder());
        // Reading OBS's config and listing the folder is disk work; keep it off the UI thread.
        _rescan.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);

        // The recording path can change in OBS settings, and switching profiles changes which basic.ini counts.
        // OBS saves by writing a temp file and swapping it in, which arrives as renames, not as a change.
        var profiles = ProfilesRoot();
        if (Directory.Exists(profiles))
        {
            _profileWatcher = new FileSystemWatcher(profiles, "basic.ini")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            _profileWatcher.Changed += (_, _) => ScheduleRescan();
            _profileWatcher.Created += (_, _) => ScheduleRescan();
            _profileWatcher.Renamed += (_, _) => ScheduleRescan();
            _profileWatcher.EnableRaisingEvents = true;
        }
        var root = ObsConfigRoot();
        if (Directory.Exists(root))
        {
            _configWatcher = new FileSystemWatcher(root, "*.ini")
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
            };
            FileSystemEventHandler onChange = (_, e) => { if (IsProfileChoice(e.Name)) ScheduleRescan(); };
            _configWatcher.Changed += onChange;
            _configWatcher.Created += onChange;
            _configWatcher.Renamed += (_, e) => { if (IsProfileChoice(e.Name) || IsProfileChoice(e.OldName)) ScheduleRescan(); };
            _configWatcher.EnableRaisingEvents = true;
        }
    }

    // Newer OBS keeps the current profile in user.ini, older versions in global.ini.
    static bool IsProfileChoice(string? name) =>
        string.Equals(name, "user.ini", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "global.ini", StringComparison.OrdinalIgnoreCase);

    // One save is several events; read once they settle.
    void ScheduleRescan() => _rescan?.Change(TimeSpan.FromMilliseconds(800), Timeout.InfiniteTimeSpan);

    void WatchRecordingFolder()
    {
        lock (_gate)
        {
            if (!Running || _rescan is null) return;
            var folder = FindRecordingFolder();
            if (string.Equals(folder, _recFolder, StringComparison.OrdinalIgnoreCase) && _recWatcher is not null) return;
            _recWatcher?.Dispose();
            _recWatcher = null;
            _recFolder = folder;
            if (folder is null || !Directory.Exists(folder)) return;

            _recWatcher = new FileSystemWatcher(folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
            _recWatcher.Created += (_, e) => OnNewFile(e.FullPath);
            _recWatcher.Renamed += (_, e) => OnNewFile(e.FullPath);
            _recWatcher.EnableRaisingEvents = true;

            // A recording that was already running when Glint started.
            try
            {
                var newest = new DirectoryInfo(folder).EnumerateFiles()
                    .Where(f => IsVideo(f.Name) && DateTime.UtcNow - f.LastWriteTimeUtc < TimeSpan.FromSeconds(20))
                    .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
                if (newest is not null) OnNewFile(newest.FullName);
            }
            catch (Exception)
            {
                // Unreadable folder: wait for new files.
            }
        }
    }

    static bool IsVideo(string name) => VideoExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    void OnNewFile(string path)
    {
        if (!IsVideo(path)) return;
        // Remuxes and other tools write here too; only OBS running makes it a recording.
        if (!IsObsRunning()) return;
        lock (_gate)
        {
            _candidate = path;
            _lastSize = -1;
            _idleTicks = 0;
            _paused = false;
            _poll?.Change(TimeSpan.FromSeconds(1), GrowthCheck);
        }
    }

    void PollCandidate()
    {
        bool? recording = null;
        string? path;
        lock (_gate)
        {
            path = _candidate;
            if (path is null || !Running) return;
            var size = IndicatorNative.TryGetLiveSize(path);
            if (size is long s && _lastSize >= 0 && s > _lastSize)
            {
                _idleTicks = 0;
                recording = true;
                if (_paused)
                {
                    _paused = false;
                    _poll?.Change(GrowthCheck, GrowthCheck);
                }
            }
            else if (_lastSize >= 0 || size is null)
            {
                // Two quiet beats in a row (about 6 s): the recording ended. A split file hands over to the next one.
                if (size is not null && _idleTicks + 1 >= 2 && IndicatorNative.IsOpenForWriting(path))
                {
                    // Paused, or a long encoder stall: OBS still holds the file. It may grow again, and that fires
                    // no event, so keep looking on a slow beat until it does or the file is let go.
                    if (!_paused)
                    {
                        _paused = true;
                        _poll?.Change(PausedCheck, PausedCheck);
                    }
                }
                else if (++_idleTicks >= 2 || size is null)
                {
                    _candidate = null;
                    _paused = false;
                    _poll?.Change(Timeout.Infinite, Timeout.Infinite);
                    recording = false;
                }
            }
            _lastSize = size ?? -1;
        }
        if (recording == true)
        {
            SetRecording(true, path);
            Publish();
        }
        else if (recording == false)
        {
            // Splitting starts the next file right away; give it a moment before calling the recording finished.
            Thread.Sleep(1500);
            lock (_gate)
                if (_candidate is not null) return;
            SetRecording(false, path);
        }
    }

    static bool IsObsRunning()
    {
        foreach (var name in new[] { "obs64", "obs32", "obs" })
        {
            var processes = System.Diagnostics.Process.GetProcessesByName(name);
            var found = processes.Length > 0;
            foreach (var p in processes) p.Dispose();
            if (found) return true;
        }
        return false;
    }

    static string ObsConfigRoot() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio");

    /// <summary>OBS lets users move profiles elsewhere ([Locations] Profiles in global.ini).</summary>
    static string ProfilesRoot()
    {
        var global = ReadIni(Path.Combine(ObsConfigRoot(), "global.ini"));
        if (global.TryGetValue("Locations.Profiles", out var custom) && Directory.Exists(Path.Combine(custom, "obs-studio", "basic", "profiles")))
            return Path.Combine(custom, "obs-studio", "basic", "profiles");
        return Path.Combine(ObsConfigRoot(), "basic", "profiles");
    }

    /// <summary>The recording folder of the current OBS profile, or the default Videos folder.</summary>
    internal static string? FindRecordingFolder()
    {
        try
        {
            var root = ObsConfigRoot();
            // Newer OBS keeps the current profile in user.ini, older versions in global.ini.
            var user = ReadIni(Path.Combine(root, "user.ini"));
            var global = ReadIni(Path.Combine(root, "global.ini"));
            var profileDir = user.GetValueOrDefault("Basic.ProfileDir") ?? global.GetValueOrDefault("Basic.ProfileDir");
            var profiles = ProfilesRoot();
            string? ini = null;
            if (profileDir is not null) ini = Path.Combine(profiles, profileDir, "basic.ini");
            if (ini is null || !File.Exists(ini))
                ini = Directory.Exists(profiles) ? Directory.EnumerateFiles(profiles, "basic.ini", SearchOption.AllDirectories).FirstOrDefault() : null;

            if (ini is not null)
            {
                var basic = ReadIni(ini);
                var advanced = string.Equals(basic.GetValueOrDefault("Output.Mode"), "Advanced", StringComparison.OrdinalIgnoreCase);
                string? path;
                if (advanced)
                {
                    // Advanced mode with "Custom output (FFmpeg)" records to FFFilePath.
                    var ffmpeg = string.Equals(basic.GetValueOrDefault("AdvOut.RecType"), "FFmpeg", StringComparison.OrdinalIgnoreCase);
                    path = ffmpeg ? basic.GetValueOrDefault("AdvOut.FFFilePath") : basic.GetValueOrDefault("AdvOut.RecFilePath");
                }
                else
                {
                    path = basic.GetValueOrDefault("SimpleOutput.FilePath");
                }
                if (!string.IsNullOrWhiteSpace(path)) return path;
            }
        }
        catch (Exception)
        {
            // Fall back to OBS's default.
        }
        return Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
    }

    /// <summary>"Section.Key" -> value; OBS escapes backslashes in paths as "\\".</summary>
    static Dictionary<string, string> ReadIni(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        var section = string.Empty;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.StartsWith('[') && line.EndsWith(']')) { section = line[1..^1]; continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            result[$"{section}.{line[..eq]}"] = line[(eq + 1)..].Replace(@"\\", @"\");
        }
        return result;
    }

    // ───────────── obs-websocket mode ─────────────

    async Task WebSocketLoopAsync(int port, string? password, CancellationToken token)
    {
        var warnedAuth = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var socket = new ClientWebSocket();
                using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    connect.CancelAfter(TimeSpan.FromSeconds(3));
                    // Localhost only: the password never leaves this machine.
                    await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), connect.Token).ConfigureAwait(false);
                }
                await RunSessionAsync(socket, password, token).ConfigureAwait(false);
                if (socket.CloseStatus == (WebSocketCloseStatus)4009 && !warnedAuth)
                {
                    warnedAuth = true;
                    AppServices.Notifier.Flash("OBS: неверный пароль", "Проверьте пароль obs-websocket в настройках", Glyphs.Record, TimeSpan.FromSeconds(4));
                }
            }
            catch (Exception) when (!token.IsCancellationRequested)
            {
                // OBS isn't running or the server is off; try again later.
            }
            catch (Exception)
            {
                break;
            }
            SetRecording(false);
            try
            {
                // A refused localhost connection costs next to nothing, but there's no hurry either.
                await Task.Delay(TimeSpan.FromSeconds(warnedAuth ? 60 : 10), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    async Task RunSessionAsync(ClientWebSocket socket, string? password, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        while (socket.State == WebSocketState.Open && !token.IsCancellationRequested)
        {
            var message = await ReceiveAsync(socket, buffer, token).ConfigureAwait(false);
            if (message is null) return;
            using var doc = JsonDocument.Parse(message);
            var op = doc.RootElement.GetProperty("op").GetInt32();
            var d = doc.RootElement.GetProperty("d");
            switch (op)
            {
                case 0: // Hello
                    var identify = new Dictionary<string, object> { ["rpcVersion"] = 1, ["eventSubscriptions"] = 1 << 6 /* Outputs */ };
                    if (d.TryGetProperty("authentication", out var auth))
                        identify["authentication"] = AuthString(password ?? string.Empty, auth.GetProperty("salt").GetString() ?? "", auth.GetProperty("challenge").GetString() ?? "");
                    await SendAsync(socket, new { op = 1, d = identify }, token).ConfigureAwait(false);
                    break;
                case 2: // Identified
                    await SendAsync(socket, new { op = 6, d = new { requestType = "GetRecordStatus", requestId = "glint-status" } }, token).ConfigureAwait(false);
                    break;
                case 5: // Event
                    if (d.GetProperty("eventType").GetString() == "RecordStateChanged" && d.TryGetProperty("eventData", out var data))
                    {
                        // Only the settled states: STOPPED carries the file path, STARTING/STOPPING come before it.
                        var state = data.TryGetProperty("outputState", out var st) ? st.GetString() : null;
                        var path = data.TryGetProperty("outputPath", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
                        if (state == "OBS_WEBSOCKET_OUTPUT_STARTED") SetRecording(true, path);
                        else if (state == "OBS_WEBSOCKET_OUTPUT_STOPPED") SetRecording(false, path);
                    }
                    break;
                case 7: // RequestResponse
                    if (d.TryGetProperty("responseData", out var status) && status.TryGetProperty("outputActive", out var on) && on.GetBoolean())
                    {
                        var ms = status.TryGetProperty("outputDuration", out var dur) ? dur.GetDouble() : 0;
                        SetRecording(true, alreadyFor: TimeSpan.FromMilliseconds(ms));
                    }
                    break;
            }
        }
    }

    /// <summary>obs-websocket v5: base64(sha256(base64(sha256(password + salt)) + challenge)).</summary>
    internal static string AuthString(string password, string salt, string challenge)
    {
        var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(password + salt)));
        return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
    }

    static async Task<string?> ReceiveAsync(ClientWebSocket socket, byte[] buffer, CancellationToken token)
    {
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, token).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > 4 * 1024 * 1024) return null;
            if (result.EndOfMessage) return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    static Task SendAsync(ClientWebSocket socket, object payload, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(payload), WebSocketMessageType.Text, true, token);
}
