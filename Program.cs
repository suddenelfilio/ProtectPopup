// ProtectPopup - toont een klein videovenster wanneer UniFi Protect een deurbel-
// of slimme-detectie-event meldt. Gebruikt de officiele Protect Integration API
// (API-key) voor events en RTSPS-streams, en mpv als videovenster.

using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var config = Config.Load(args);
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };

var api = new ProtectClient(config);
var popups = new PopupManager(config, api);
AppDomain.CurrentDomain.ProcessExit += (_, _) => popups.CloseAll();

var listener = new EventListener(config, api, popups);

if (args.Contains("--cameras"))
{
    // Toont een macOS-keuzelijst met de camera's uit Protect en bewaart de keuze in config.json.
    await CameraPicker.RunAsync(config, api, shutdown.Token);
    return;
}

if (args.Contains("--test"))
{
    // Opent meteen een popup voor de eerste (of gefilterde) camera, om de video te testen.
    await listener.TestAsync(shutdown.Token);
}

try
{
    await listener.RunAsync(shutdown.Token);
}
catch (OperationCanceledException) { }
finally
{
    popups.CloseAll();
}

static class Log
{
    public static void Info(string message) =>
        Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
}

static class CameraPicker
{
    public static async Task RunAsync(Config config, ProtectClient api, CancellationToken ct)
    {
        var cameras = await api.RefreshCamerasAsync(ct);
        if (cameras.Count == 0)
        {
            Log.Info("Geen camera's gevonden in Protect.");
            return;
        }

        var names = cameras.Values.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        var selected = config.Cameras.Length == 0
            ? names
            : names.Where(n => config.Cameras.Contains(n, StringComparer.OrdinalIgnoreCase)
                || config.Cameras.Contains(cameras.First(c => c.Value == n).Key, StringComparer.OrdinalIgnoreCase)).ToArray();

        // Namen gaan als argv naar osascript, zo hoeft niets in AppleScript ge-escaped te worden.
        const string script = """
            on run argv
                set n to (item 1 of argv) as integer
                set allNames to items 2 thru (n + 1) of argv
                if (count of argv) > (n + 1) then
                    set preselect to items (n + 2) thru -1 of argv
                else
                    set preselect to {}
                end if
                set picked to choose from list allNames with title "ProtectPopup" with prompt "Voor welke camera's moet een popup verschijnen? (Cmd-klik voor meerdere)" default items preselect with multiple selections allowed
                if picked is false then return "<cancel>"
                set AppleScript's text item delimiters to linefeed
                return picked as text
            end run
            """;
        var info = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-e");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(names.Length.ToString());
        foreach (var n in names) info.ArgumentList.Add(n);
        foreach (var n in selected) info.ArgumentList.Add(n);

        using var process = Process.Start(info)!;
        var output = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0 || output == "<cancel>")
        {
            Log.Info("Geannuleerd, config niet gewijzigd.");
            return;
        }

        var picked = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        // Alles gekozen = lege lijst, zodat nieuwe camera's ook automatisch meedoen.
        string[] value = picked.Length == names.Length ? [] : picked;
        config.SaveCameras(value);
        Log.Info(value.Length == 0 ? "Popups voor alle camera's." : $"Popups voor: {string.Join(", ", value)}");
    }
}

sealed class Config
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string FilePath { get; private set; } = "";
    public string Host { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public bool VerifyTls { get; set; } = false;
    public string Quality { get; set; } = "medium";
    public int PopupSeconds { get; set; } = 60;
    public int PopupWidth { get; set; } = 480;
    public bool Mute { get; set; } = true;
    public bool Doorbell { get; set; } = true;
    public string[] SmartDetectTypes { get; set; } = ["person", "vehicle", "package"];
    public string[] Cameras { get; set; } = [];
    public string? MpvPath { get; set; }
    public bool LogAllEvents { get; set; } = false;
    public int FlashSeconds { get; set; } = 5;
    public string FlashColor { get; set; } = "FF0000";
    public string? Sound { get; set; } = "Glass";

    public static Config Load(string[] args)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new List<string>();
        var explicitPath = args.FirstOrDefault(a => !a.StartsWith("--"));
        if (explicitPath is not null) candidates.Add(explicitPath);
        candidates.Add(Path.Combine(Environment.CurrentDirectory, "config.json"));
        candidates.Add(Path.Combine(home, ".config", "protect-popup", "config.json"));

        var path = candidates.FirstOrDefault(File.Exists);
        if (path is null)
        {
            Console.Error.WriteLine("Geen config.json gevonden. Gezocht in:");
            foreach (var c in candidates) Console.Error.WriteLine("  " + c);
            Environment.Exit(2);
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(path), options) ?? new Config();

        config.Host = config.Host.Trim().Replace("https://", "").TrimEnd('/');
        if (config.Host.Length == 0 || config.ApiKey.Length == 0 || config.ApiKey.StartsWith("PLAK"))
        {
            Console.Error.WriteLine($"Vul 'host' en 'apiKey' in in {path}");
            Environment.Exit(2);
        }
        config.FilePath = path;
        Log.Info($"Config geladen uit {path}");
        return config;
    }

    /// <summary>Vervangt enkel de "cameras"-regel, zodat de commentaar in config.json blijft staan.</summary>
    public void SaveCameras(string[] cameras)
    {
        var json = JsonSerializer.Serialize(cameras);
        var text = File.ReadAllText(FilePath);
        var pattern = new System.Text.RegularExpressions.Regex("\"cameras\"\\s*:\\s*\\[[^\\]]*\\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = pattern.IsMatch(text)
            ? pattern.Replace(text, $"\"cameras\": {json}", 1)
            : text.Insert(text.IndexOf('{') + 1, $"\n  \"cameras\": {json},");
        File.WriteAllText(FilePath, text);
        Cameras = cameras;
    }
}

sealed class ProtectClient
{
    private readonly Config _config;
    private readonly HttpClient _http;
    private readonly Dictionary<string, string> _cameraNames = new();
    private readonly Dictionary<string, string> _streamUrls = new();

    public ProtectClient(Config config)
    {
        _config = config;
        var handler = new HttpClientHandler();
        if (!config.VerifyTls)
            handler.ServerCertificateCustomValidationCallback = (_, _, _, _) => true;
        _http = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://{config.Host}/proxy/protect/integration/v1/"),
            Timeout = TimeSpan.FromSeconds(15),
        };
        _http.DefaultRequestHeaders.Add("X-API-KEY", config.ApiKey);
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public Uri EventsUri => new($"wss://{_config.Host}/proxy/protect/integration/v1/subscribe/events");

    public async Task<IReadOnlyDictionary<string, string>> RefreshCamerasAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("cameras", ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        lock (_cameraNames)
        {
            _cameraNames.Clear();
            foreach (var camera in doc.RootElement.EnumerateArray())
            {
                var id = Json.Str(camera, "id");
                if (id is not null) _cameraNames[id] = Json.Str(camera, "name") ?? id;
            }
            return new Dictionary<string, string>(_cameraNames);
        }
    }

    public async Task<string> GetCameraNameAsync(string cameraId, CancellationToken ct)
    {
        lock (_cameraNames)
            if (_cameraNames.TryGetValue(cameraId, out var known)) return known;
        try { await RefreshCamerasAsync(ct); } catch (HttpRequestException) { }
        lock (_cameraNames)
            return _cameraNames.TryGetValue(cameraId, out var name) ? name : cameraId;
    }

    /// <summary>Geeft de RTSPS-url van een camera; maakt de stream aan als die nog niet bestaat.</summary>
    public async Task<string?> GetStreamUrlAsync(string cameraId, CancellationToken ct)
    {
        lock (_streamUrls)
            if (_streamUrls.TryGetValue(cameraId, out var cached)) return cached;

        string? url = null;
        using (var response = await _http.GetAsync($"cameras/{cameraId}/rtsps-stream", ct))
        {
            if (response.IsSuccessStatusCode)
                url = PickQuality(await response.Content.ReadAsStringAsync(ct));
        }

        if (url is null)
        {
            var body = JsonSerializer.Serialize(new { qualities = new[] { _config.Quality } });
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"cameras/{cameraId}/rtsps-stream", content, ct);
            if (!response.IsSuccessStatusCode)
            {
                Log.Info($"RTSPS-stream aanmaken mislukt: HTTP {(int)response.StatusCode} {await response.Content.ReadAsStringAsync(ct)}");
                return null;
            }
            url = PickQuality(await response.Content.ReadAsStringAsync(ct));
        }

        if (url is null) return null;
        // De url gaat naar mpv: alleen echte rtsps-urls doorlaten, nooit iets dat als optie gelezen kan worden.
        if (!url.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
        {
            Log.Info("Onverwachte stream-url van Protect genegeerd.");
            return null;
        }

        // ffmpeg/mpv kent de '?enableSrtp'-parameter niet; zonder werkt de stream gewoon over TLS.
        var query = url.IndexOf('?');
        if (query >= 0) url = url[..query];

        lock (_streamUrls) _streamUrls[cameraId] = url;
        return url;
    }

    public void ForgetStreamUrl(string cameraId)
    {
        lock (_streamUrls) _streamUrls.Remove(cameraId);
    }

    private string? PickQuality(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
        // Gevraagde kwaliteit eerst; anders wat er al bestaat.
        return Json.Str(doc.RootElement, _config.Quality);
    }
}

sealed class PopupManager(Config config, ProtectClient api)
{
    private sealed class Popup
    {
        public required Process Process { get; init; }
        public required int Slot { get; init; }
        public CancellationTokenSource Timer { get; set; } = new();
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Popup> _open = new();
    private readonly string _mpv = FindMpv(config.MpvPath);
    // mpv-script dat de eerste seconden een knipperende rand over het beeld tekent; staat naast de binary.
    private readonly string _flashScript = Path.Combine(AppContext.BaseDirectory, "flash.lua");

    public async Task ShowAsync(string cameraId, string title, CancellationToken ct)
    {
        PlaySound();

        lock (_gate)
        {
            if (_open.TryGetValue(cameraId, out var existing))
            {
                // Venster staat al open: de minuut begint opnieuw.
                StartTimer(existing);
                return;
            }
        }

        var url = await api.GetStreamUrlAsync(cameraId, ct);
        if (url is null)
        {
            Log.Info($"Geen RTSPS-stream beschikbaar voor camera {cameraId}");
            return;
        }

        lock (_gate)
        {
            var slot = 0;
            while (_open.Values.Any(p => p.Slot == slot)) slot++;

            var width = config.PopupWidth;
            var height = width * 9 / 16;
            var top = 48 + slot * (height + 44);

            var info = new ProcessStartInfo(_mpv) { UseShellExecute = false };
            foreach (var argument in new[]
            {
                $"--title={title}",
                $"--geometry={width}x{height}-24+{top}",   // rechtsboven, onder elkaar gestapeld
                "--ontop",
                "--force-window=immediate",
                "--profile=low-latency",
                "--rtsp-transport=tcp",
                "--no-terminal",
                "--no-input-default-bindings",
                "--input-conf=/dev/null",
                "--osc=no",
                "--idle=no",
                $"--script={_flashScript}",
                $"--script-opts=protect-flash-seconds={config.FlashSeconds},protect-flash-color={config.FlashColor}",
                config.Mute ? "--mute=yes" : "--mute=no",
                "--",   // alles hierna is een url, geen optie
                url,
            })
            {
                info.ArgumentList.Add(argument);
            }

            Process process;
            try
            {
                process = Process.Start(info) ?? throw new InvalidOperationException("mpv startte niet");
            }
            catch (Exception ex)
            {
                Log.Info($"Kan mpv niet starten ({_mpv}): {ex.Message}. Installeer met 'brew install mpv' of zet 'mpvPath' in config.json.");
                return;
            }

            var popup = new Popup { Process = process, Slot = slot };
            _open[cameraId] = popup;
            var started = DateTime.UtcNow;

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                lock (_gate)
                {
                    if (_open.TryGetValue(cameraId, out var current) && ReferenceEquals(current, popup))
                        _open.Remove(cameraId);
                    popup.Timer.Cancel();
                }
                if (DateTime.UtcNow - started < TimeSpan.FromSeconds(3))
                {
                    // Direct gesloten: waarschijnlijk een ongeldige stream-url. Volgende keer opnieuw ophalen.
                    api.ForgetStreamUrl(cameraId);
                    Log.Info("mpv sloot meteen weer af; de stream-url wordt bij het volgende event opnieuw opgehaald.");
                }
            };

            StartTimer(popup);
        }
    }

    public void CloseAll()
    {
        lock (_gate)
        {
            foreach (var popup in _open.Values) Kill(popup.Process);
            _open.Clear();
        }
    }

    // Aanroepen binnen lock (_gate).
    private void StartTimer(Popup popup)
    {
        popup.Timer.Cancel();
        var timer = popup.Timer = new CancellationTokenSource();
        var process = popup.Process;
        _ = Task.Delay(TimeSpan.FromSeconds(config.PopupSeconds), timer.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) Kill(process);
        }, TaskScheduler.Default);
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void PlaySound()
    {
        if (string.IsNullOrWhiteSpace(config.Sound)) return;
        // Naam van een macOS-systeemgeluid (Glass, Ping, Hero...) of een volledig pad.
        var file = config.Sound.Contains('/') ? config.Sound : $"/System/Library/Sounds/{config.Sound}.aiff";
        try
        {
            var info = new ProcessStartInfo("/usr/bin/afplay") { UseShellExecute = false };
            info.ArgumentList.Add(file);
            Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Info($"Geluid afspelen mislukt ({file}): {ex.Message}");
        }
    }

    private static string FindMpv(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        // launchd geeft een kale PATH mee, dus de Homebrew-locaties expliciet proberen.
        foreach (var path in new[] { "/opt/homebrew/bin/mpv", "/usr/local/bin/mpv" })
            if (File.Exists(path)) return path;
        return "mpv";
    }
}

sealed class EventListener(Config config, ProtectClient api, PopupManager popups)
{
    private readonly HashSet<string> _handled = new();

    public async Task TestAsync(CancellationToken ct)
    {
        var cameras = await api.RefreshCamerasAsync(ct);
        var camera = cameras.FirstOrDefault(c => IsCameraEnabled(c.Key, c.Value));
        if (camera.Key is null)
        {
            Log.Info("Test: geen camera gevonden.");
            return;
        }
        Log.Info($"Test: popup voor {camera.Value}");
        await popups.ShowAsync(camera.Key, $"Test - {camera.Value}", ct);
    }

    public async Task RunAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(2);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cameras = await api.RefreshCamerasAsync(ct);
                Log.Info($"Verbonden met Protect, {cameras.Count} camera's: {string.Join(", ", cameras.Values)}");

                using var socket = new ClientWebSocket();
                socket.Options.SetRequestHeader("X-API-KEY", config.ApiKey);
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                if (!config.VerifyTls)
                    socket.Options.RemoteCertificateValidationCallback = (_, _, _, _) => true;

                await socket.ConnectAsync(api.EventsUri, ct);
                Log.Info("Luistert naar events...");
                delay = TimeSpan.FromSeconds(2);

                await ReceiveLoopAsync(socket, ct);
                Log.Info("Verbinding door Protect gesloten.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is WebSocketException or HttpRequestException or IOException or OperationCanceledException)
            {
                Log.Info($"Verbindingsfout: {ex.Message}");
            }

            Log.Info($"Opnieuw verbinden over {delay.TotalSeconds:0} s");
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (socket.State == WebSocketState.Open)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text) continue;

            try
            {
                using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
                await HandleAsync(doc.RootElement, ct);
            }
            catch (JsonException ex)
            {
                Log.Info($"Onleesbaar bericht overgeslagen: {ex.Message}");
            }
            catch (HttpRequestException ex)
            {
                Log.Info($"Event kon niet verwerkt worden: {ex.Message}");
            }
        }
    }

    private async Task HandleAsync(JsonElement root, CancellationToken ct)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return;

        var id = Json.Str(item, "id");
        var type = Json.Str(item, "type");
        var device = Json.Str(item, "device");
        if (config.LogAllEvents) Log.Info($"event: {root.GetRawText()}");
        if (id is null || type is null || device is null) return;

        string? reason = null;
        if (type == "ring")
        {
            if (config.Doorbell) reason = "Er wordt aangebeld";
        }
        else if (type.StartsWith("smartDetect", StringComparison.OrdinalIgnoreCase))
        {
            var match = Json.Strings(item, "smartDetectTypes")
                .Where(t => config.SmartDetectTypes.Contains(t, StringComparer.OrdinalIgnoreCase))
                .ToArray();
            if (match.Length > 0) reason = Describe(match);
        }
        if (reason is null) return;

        // Elk event komt als 'add' en daarna als meerdere 'update's binnen: maar een keer reageren.
        if (_handled.Contains(id)) return;

        // Oude events (bv. een late update na herverbinden) niet alsnog tonen.
        if (item.TryGetProperty("start", out var start) && start.TryGetInt64(out var startMs))
        {
            var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(startMs);
            if (age > TimeSpan.FromSeconds(45)) return;
        }

        if (_handled.Count > 5000) _handled.Clear();
        _handled.Add(id);

        var name = await api.GetCameraNameAsync(device, ct);
        if (!IsCameraEnabled(device, name)) return;

        Log.Info($"{name}: {reason}");
        await popups.ShowAsync(device, $"{name} - {reason}", ct);
    }

    private bool IsCameraEnabled(string id, string name) =>
        config.Cameras.Length == 0 ||
        config.Cameras.Any(c => c.Equals(name, StringComparison.OrdinalIgnoreCase) || c.Equals(id, StringComparison.OrdinalIgnoreCase));

    private static string Describe(string[] types) => string.Join(", ", types.Select(t => t.ToLowerInvariant() switch
    {
        "person" => "persoon",
        "vehicle" => "voertuig",
        "package" => "pakket",
        "animal" => "dier",
        "licenseplate" => "nummerplaat",
        "face" => "gezicht",
        _ => t,
    })) + " gedetecteerd";
}

static class Json
{
    public static string? Str(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static IEnumerable<string> Strings(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Array)
            yield break;
        foreach (var entry in value.EnumerateArray())
            if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { } text)
                yield return text;
    }
}
