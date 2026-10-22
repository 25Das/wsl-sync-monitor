using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;

namespace WslSyncMonitor;

/// <summary>Coleta o estado do WSL e do Syncthing (REST API) e monta um <see cref="Snapshot"/>.</summary>
public sealed class StatusCollector : IDisposable
{
    readonly HttpClient _http;

    // credenciais descobertas no config.xml (por distro)
    string? _cachedDistro, _cachedKey, _cachedUrl;
    string? _fallbackUrl; // IP do WSL quando localhost não encaminha

    // para cálculo de taxa de transferência
    DateTime _lastTotalsAt;
    long _lastIn, _lastOut;
    readonly Dictionary<string, (long In, long Out)> _lastDev = new();

    public StatusCollector()
    {
        var handler = new HttpClientHandler
        {
            // GUI com HTTPS usa certificado autoassinado; só aceitamos para endereços locais/WSL.
            ServerCertificateCustomValidationCallback = (req, _, _, errors) =>
                errors == System.Net.Security.SslPolicyErrors.None || IsLocal(req.RequestUri),
            UseProxy = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(4) };
    }

    static bool IsLocal(Uri? u) => u != null && (u.IsLoopback || (IPAddress.TryParse(u.Host, out var ip) && IsPrivate(ip)));

    static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168));
    }

    /// <summary>URL atual da GUI (para o botão "Abrir Syncthing").</summary>
    public string GuiUrl(Settings s) =>
        !string.IsNullOrWhiteSpace(s.SyncthingUrl) ? s.SyncthingUrl.TrimEnd('/') :
        _fallbackUrl ?? _cachedUrl ?? "http://127.0.0.1:8384";

    public void InvalidateCredentials() { _cachedKey = null; _cachedUrl = null; _fallbackUrl = null; _cachedDistro = null; _configReadAt = default; }

    DateTime _configReadAt; // última leitura do config.xml (evita reler a cada atualização quando falha)

    /// <param name="allowDistroCommands">
    /// false quando o WSL deve poder desligar por ociosidade: nada é executado dentro da distro
    /// (cada comando reinicia a contagem de ociosidade, ou religaria uma distro que acabou de parar).
    /// </param>
    public async Task<Snapshot> RefreshAsync(Settings s, bool allowDistroCommands = true)
    {
        var snap = new Snapshot();

        if (!WslService.IsInstalled) { snap.WslInstalled = false; snap.WslError = "wsl.exe não encontrado"; return snap; }

        try { snap.Distros = await WslService.GetDistrosAsync(); }
        catch (Exception ex) { snap.WslError = ex.Message; }

        if (snap.Distros.Count == 0 && snap.WslError == null) snap.WslError = "Nenhuma distro instalada";

        snap.Target = string.IsNullOrWhiteSpace(s.Distro)
            ? snap.Distros.FirstOrDefault(d => d.IsDefault) ?? snap.Distros.FirstOrDefault()
            : snap.Distros.FirstOrDefault(d => d.Name.Equals(s.Distro, StringComparison.OrdinalIgnoreCase));

        if (snap.Target == null && !string.IsNullOrWhiteSpace(s.Distro))
            snap.WslError = $"Distro \"{s.Distro}\" não encontrada";

        if (snap.Target is not { Running: true })
        {
            snap.St = StState.DistroStopped;
            snap.Url = GuiUrl(s);
            return snap;
        }

        var distro = snap.Target.Name;
        try
        {
            // sem a chave a API recusa tudo; por isso a leitura do config.xml acontece mesmo com a distro
            // liberada (é rara: uma vez, e depois no máximo uma por minuto se a chave não for encontrada)
            await EnsureCredentialsAsync(s, distro);
            snap.Url = GuiUrl(s);
            await QuerySyncthingAsync(snap, s, snap.Url);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            snap.St = StState.AuthError;
            snap.StError = "Acesso negado pela API (verifique a chave da API).";
            // a chave pode ter mudado: relê o config.xml já na próxima atualização (e depois no máximo 1x/min)
            _cachedKey = null;
            _configReadAt = default;
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or TimeoutException) && !allowDistroCommands)
        {
            snap.St = StState.Unreachable;
            snap.StError = $"{GuiUrl(s)} não responde ({ex.Message}).";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            bool running = false;
            try { running = await WslService.IsProcessRunningAsync(distro, "syncthing"); } catch { }

            if (!running)
            {
                snap.St = StState.NotRunning;
                snap.StError = "Processo syncthing não encontrado na distro.";
            }
            else if (await TryWslIpFallbackAsync(snap, s, distro))
            {
                // ok, conseguimos pelo IP da VM
            }
            else
            {
                snap.St = StState.Unreachable;
                snap.StError = $"Syncthing roda, mas {GuiUrl(s)} não responde ({ex.Message}).";
            }
        }
        catch (Exception ex)
        {
            snap.St = StState.Unreachable;
            snap.StError = ex.Message;
        }
        return snap;
    }

    async Task<bool> TryWslIpFallbackAsync(Snapshot snap, Settings s, string distro)
    {
        if (!string.IsNullOrWhiteSpace(s.SyncthingUrl)) return false;
        try
        {
            var ip = await WslService.GetIpAsync(distro);
            if (ip == null || _cachedUrl == null) return false;
            var u = new UriBuilder(_cachedUrl) { Host = ip }.Uri.ToString().TrimEnd('/');
            if (u == _fallbackUrl) return false;
            await QuerySyncthingAsync(snap, s, u);
            _fallbackUrl = u;
            snap.Url = u;
            return true;
        }
        catch { return false; }
    }

    async Task EnsureCredentialsAsync(Settings s, string distro)
    {
        if (_cachedDistro != distro) InvalidateCredentials();
        if (!string.IsNullOrWhiteSpace(s.ApiKey) && (!string.IsNullOrWhiteSpace(s.SyncthingUrl) || _cachedUrl != null))
            return;
        if (_cachedKey != null && _cachedUrl != null) return;
        // sem chave: relê no máximo uma vez por minuto (o resultado não muda a cada atualização)
        if (_cachedUrl != null && DateTime.Now - _configReadAt < TimeSpan.FromMinutes(1)) return;
        _configReadAt = DateTime.Now;

        var xml = await WslService.ReadSyncthingConfigAsync(distro, s.SyncthingConfigPath);
        _cachedDistro = distro;
        _cachedUrl = "http://127.0.0.1:8384";
        if (xml == null) return;

        var gui = XDocument.Parse(xml).Root?.Element("gui");
        if (gui == null) return;
        _cachedKey = gui.Element("apikey")?.Value;
        bool tls = string.Equals((string?)gui.Attribute("tls"), "true", StringComparison.OrdinalIgnoreCase);
        var addr = gui.Element("address")?.Value ?? "127.0.0.1:8384";
        // endereço pode ser "0.0.0.0:8384", "[::]:8384", "127.0.0.1:8384" ou unix socket
        var idx = addr.LastIndexOf(':');
        var port = idx >= 0 && int.TryParse(addr[(idx + 1)..], out var p) ? p : 8384;
        if (addr.StartsWith("unix", StringComparison.OrdinalIgnoreCase) || addr.StartsWith('/')) port = 8384;
        _cachedUrl = $"{(tls ? "https" : "http")}://127.0.0.1:{port}";
    }

    /// <summary>Pede ao Syncthing para encerrar de forma limpa (POST /rest/system/shutdown).</summary>
    public async Task ShutdownSyncthingAsync(Settings s)
    {
        var key = !string.IsNullOrWhiteSpace(s.ApiKey) ? s.ApiKey : _cachedKey ?? "";
        using var req = new HttpRequestMessage(HttpMethod.Post, GuiUrl(s) + "/rest/system/shutdown");
        if (!string.IsNullOrEmpty(key)) req.Headers.Add("X-API-Key", key);
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    public sealed record SyncCheck(bool Synced, string Detail, TimeSpan Uptime, bool AllDevicesConnected);

    /// <summary>Pede ao Syncthing para verificar já todas as pastas compartilhadas (sem esperar o atraso do monitoramento).</summary>
    public async Task ScanAllAsync(Settings s)
    {
        var key = !string.IsNullOrWhiteSpace(s.ApiKey) ? s.ApiKey : _cachedKey ?? "";
        var url = GuiUrl(s);
        var config = await GetAsync(url, key, "/rest/config");
        foreach (var f in config.GetProperty("folders").EnumerateArray())
        {
            if (Bool(f, "paused")) continue;
            using var req = new HttpRequestMessage(HttpMethod.Post, url + "/rest/db/scan?folder=" + Uri.EscapeDataString(Str(f, "id") ?? ""));
            if (!string.IsNullOrEmpty(key)) req.Headers.Add("X-API-Key", key);
            using var resp = await _http.SendAsync(req);
        }
    }

    /// <summary>
    /// Verifica se tudo está sincronizado: pastas compartilhadas paradas e sem pendências, e todos os
    /// dispositivos conectados com 100%. Dispositivos desconectados não são esperados.
    /// </summary>
    public async Task<SyncCheck> CheckSyncedAsync(Settings s)
    {
        var key = !string.IsNullOrWhiteSpace(s.ApiKey) ? s.ApiKey : _cachedKey ?? "";
        var url = GuiUrl(s);
        var status = await GetAsync(url, key, "/rest/system/status");
        var myId = Str(status, "myID");
        var uptime = TimeSpan.FromSeconds(Num(status, "uptime"));
        var config = await GetAsync(url, key, "/rest/config");
        var conns = (await GetAsync(url, key, "/rest/system/connections")).GetProperty("connections");

        bool allConnected = true;
        foreach (var d in config.GetProperty("devices").EnumerateArray())
        {
            var id = Str(d, "deviceID") ?? "";
            if (id == myId || Bool(d, "paused")) continue;
            bool connected = conns.TryGetProperty(id, out var c) && Bool(c, "connected");
            if (!connected) { allConnected = false; continue; }
            var comp = await GetAsync(url, key, "/rest/db/completion?device=" + Uri.EscapeDataString(id));
            if (Num(comp, "completion", 100) < 100 || Num(comp, "needItems") > 0 || Num(comp, "needDeletes") > 0)
                return new(false, $"enviando para {NonEmpty(Str(d, "name")) ?? id[..Math.Min(7, id.Length)]} ({Num(comp, "completion", 100):0}%)", uptime, allConnected);
        }

        foreach (var f in config.GetProperty("folders").EnumerateArray())
        {
            if (Bool(f, "paused")) continue;
            bool shared = f.TryGetProperty("devices", out var fd) && fd.EnumerateArray().Any(x => Str(x, "deviceID") != myId);
            if (!shared) continue;
            var label = NonEmpty(Str(f, "label")) ?? Str(f, "id") ?? "";
            var db = await GetAsync(url, key, "/rest/db/status?folder=" + Uri.EscapeDataString(Str(f, "id") ?? ""));
            var state = Str(db, "state") ?? "";
            if (state != "idle") return new(false, $"{label}: {state}", uptime, allConnected);
            if (Num(db, "needTotalItems") > 0 || Num(db, "needBytes") > 0)
                return new(false, $"{label}: recebendo", uptime, allConnected);
        }
        return new(true, "sincronizado", uptime, allConnected);
    }

    async Task<JsonElement> GetAsync(string baseUrl, string key, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl + path);
        if (!string.IsNullOrEmpty(key)) req.Headers.Add("X-API-Key", key);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var resp = await _http.SendAsync(req);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} em {path}", null, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    async Task QuerySyncthingAsync(Snapshot snap, Settings s, string url)
    {
        var key = !string.IsNullOrWhiteSpace(s.ApiKey) ? s.ApiKey : _cachedKey ?? "";

        var tStatus = GetAsync(url, key, "/rest/system/status");
        var tVersion = GetAsync(url, key, "/rest/system/version");
        var tConfig = GetAsync(url, key, "/rest/config");
        var tConn = GetAsync(url, key, "/rest/system/connections");
        var tStats = GetAsync(url, key, "/rest/stats/device");
        await Task.WhenAll(tStatus, tVersion, tConfig, tConn, tStats);

        var status = tStatus.Result;
        snap.St = StState.Running;
        snap.MyId = Str(status, "myID");
        snap.StUptime = TimeSpan.FromSeconds(Num(status, "uptime"));
        snap.StVersion = Str(tVersion.Result, "version");

        var config = tConfig.Result;
        var conns = tConn.Result.TryGetProperty("connections", out var c) ? c : default;
        var stats = tStats.Result;

        // --- Taxas totais ---
        var now = DateTime.UtcNow;
        double dt = (now - _lastTotalsAt).TotalSeconds;
        if (tConn.Result.TryGetProperty("total", out var total))
        {
            long tin = (long)Num(total, "inBytesTotal"), tout = (long)Num(total, "outBytesTotal");
            if (_lastTotalsAt != default && dt > 0.5)
            {
                snap.InRate = Math.Max(0, (tin - _lastIn) / dt);
                snap.OutRate = Math.Max(0, (tout - _lastOut) / dt);
            }
            _lastIn = tin; _lastOut = tout;
        }

        // --- Dispositivos ---
        if (config.TryGetProperty("devices", out var devs))
        {
            foreach (var d in devs.EnumerateArray())
            {
                var id = Str(d, "deviceID") ?? "";
                if (id == snap.MyId) continue;
                var dev = new DeviceInfo
                {
                    Id = id,
                    Name = NonEmpty(Str(d, "name")) ?? id[..Math.Min(7, id.Length)],
                    Paused = Bool(d, "paused"),
                };
                if (conns.ValueKind == JsonValueKind.Object && conns.TryGetProperty(id, out var cn))
                {
                    dev.Connected = Bool(cn, "connected");
                    dev.Paused |= Bool(cn, "paused");
                    dev.Address = Str(cn, "address") ?? "";
                    dev.ClientVersion = Str(cn, "clientVersion") ?? "";
                    dev.ConnType = Str(cn, "type") ?? "";
                    long din = (long)Num(cn, "inBytesTotal"), dout = (long)Num(cn, "outBytesTotal");
                    if (_lastDev.TryGetValue(id, out var prev) && _lastTotalsAt != default && dt > 0.5)
                    {
                        dev.InRate = Math.Max(0, (din - prev.In) / dt);
                        dev.OutRate = Math.Max(0, (dout - prev.Out) / dt);
                    }
                    _lastDev[id] = (din, dout);
                }
                if (stats.ValueKind == JsonValueKind.Object && stats.TryGetProperty(id, out var st) &&
                    DateTime.TryParse(Str(st, "lastSeen"), out var ls))
                    dev.LastSeen = ls;
                snap.Devices.Add(dev);
            }
        }
        _lastTotalsAt = now;

        // --- Pastas ---
        if (config.TryGetProperty("folders", out var folders))
        {
            foreach (var f in folders.EnumerateArray())
            {
                var fi = new FolderInfo
                {
                    Id = Str(f, "id") ?? "",
                    Label = NonEmpty(Str(f, "label")) ?? Str(f, "id") ?? "",
                    Path = Str(f, "path") ?? "",
                    Type = Str(f, "type") ?? "",
                    Paused = Bool(f, "paused"),
                };
                if (f.TryGetProperty("devices", out var fd))
                    fi.SharedWith = fd.EnumerateArray().Count(x => Str(x, "deviceID") != snap.MyId);
                snap.Folders.Add(fi);
            }
        }

        // detalhes em paralelo
        var folderTasks = snap.Folders.Where(f => !f.Paused).Select(async f =>
        {
            try
            {
                var db = await GetAsync(url, key, "/rest/db/status?folder=" + Uri.EscapeDataString(f.Id));
                f.State = Str(db, "state") ?? "unknown";
                f.GlobalBytes = (long)Num(db, "globalBytes");
                f.InSyncBytes = (long)Num(db, "inSyncBytes");
                f.NeedBytes = (long)Num(db, "needBytes");
                f.NeedItems = (long)(Num(db, "needFiles") + Num(db, "needDirectories") + Num(db, "needSymlinks") + Num(db, "needDeletes"));
                f.Errors = (int)(Num(db, "errors") + Num(db, "pullErrors"));
                f.Error = NonEmpty(Str(db, "error")) ?? NonEmpty(Str(db, "invalid"));
            }
            catch (Exception ex) { f.State = "error"; f.Error = ex.Message; }
        });
        var devTasks = snap.Devices.Where(d => d.Connected).Select(async d =>
        {
            try
            {
                var comp = await GetAsync(url, key, "/rest/db/completion?device=" + Uri.EscapeDataString(d.Id));
                d.Completion = Num(comp, "completion", 100);
                d.NeedBytes = (long)Num(comp, "needBytes");
            }
            catch { }
        });
        await Task.WhenAll(folderTasks.Concat(devTasks));

        snap.AllFolders = snap.Folders;
        snap.Folders = snap.AllFolders.Where(f => !s.IsFolderHidden(f)).ToList();
    }

    static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static double Num(JsonElement e, string p, double def = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;

    static bool Bool(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

    static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    public void Dispose() => _http.Dispose();
}
