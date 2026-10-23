using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Xml.Linq;

namespace WslSyncMonitor;

/// <summary>Coleta o estado do WSL e do Syncthing (REST API) e monta um <see cref="Snapshot"/>.</summary>
public sealed class StatusCollector : IDisposable
{
    readonly HttpClient _http = SyncthingHttp.Create(TimeSpan.FromSeconds(4));

    // credenciais descobertas no config.xml (por distro)
    string? _cachedDistro, _cachedKey, _cachedUrl;
    string? _fallbackUrl; // IP do WSL quando localhost não encaminha

    // para cálculo de taxa de transferência
    readonly TransferRates _rates = new();

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
        catch (Exception ex)
        {
            snap.WslError = ex.Message;
            if (ex is WslService.SessionEndingException) throw; // nada a mostrar: o app está para fechar
        }

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
            await EnsureCredentialsAsync(s, distro, allowDistroCommands);
            snap.Url = GuiUrl(s);
            await QuerySyncthingAsync(snap, s, snap.Url);
            LastApiError = null;
            if (!string.IsNullOrWhiteSpace(s.ApiKey)) ApiKeySource = "settings.json (ApiKey)";
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            snap.St = StState.AuthError;
            snap.StError = "Acesso negado pela API. " + (_credentialsInfo ?? "Verifique a chave da API.") + SeeTechInfo;
            LastApiError = ex.Message;
            if (CredentialRules.DiscardKeyOnAuthError(allowDistroCommands))
            {
                // a chave pode ter mudado: relê o config.xml já na próxima atualização (e depois no máximo 1x/min)
                _cachedKey = null;
                _configReadAt = default;
            }
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or TimeoutException) && !allowDistroCommands)
        {
            snap.St = StState.Unreachable;
            snap.StError = $"{GuiUrl(s)} não responde ({ex.Message}).";
            LastApiError = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException)
        {
            LastApiError = ex.Message;
            bool running = false;
            try { running = await WslService.IsProcessRunningAsync(distro, "syncthing"); }
            catch (Exception pex) { Log.Failure("syncthing", "pgrep", "não foi possível saber se o Syncthing roda: " + pex.Message); }

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
            snap.StError = ex.Message + SeeTechInfo;
            LastApiError = ex.ToString();
        }
        if (snap.St == StState.Running) Log.Recovered("syncthing", "api", "a API do Syncthing voltou a responder");
        else Log.Failure("syncthing", "api", $"{snap.StStatusText}: {snap.StError ?? LastApiError}");
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

    async Task EnsureCredentialsAsync(Settings s, string distro, bool allowDistroCommands)
    {
        if (_cachedDistro != distro) InvalidateCredentials();
        if (!string.IsNullOrWhiteSpace(s.ApiKey) && (!string.IsNullOrWhiteSpace(s.SyncthingUrl) || _cachedUrl != null))
            return;
        if (_cachedUrl != null && !CredentialRules.ShouldReadConfig(_cachedKey != null, _configReadAt, DateTime.Now, allowDistroCommands))
            return;
        if (!allowDistroCommands) { _cachedUrl ??= "http://127.0.0.1:8384"; return; }
        _configReadAt = DateTime.Now;

        var (files, exit, error) = await WslService.ReadSyncthingConfigsAsync(distro, s.SyncthingConfigPath);
        ConfigFiles = files.Select(f => f.Path).ToList();
        ConfigFileUsed = null;
        ApiKeySource = "não encontrada";
        LastScriptExitCode = exit;
        LastScriptError = error;
        _cachedDistro = distro;
        _cachedUrl = "http://127.0.0.1:8384";
        _credentialsInfo = files.Count == 0 ? "config.xml do Syncthing não encontrado na distro." : null;

        foreach (var (path, xml) in files)
        {
            try
            {
                var gui = XDocument.Parse(xml).Root?.Element("gui");
                if (gui == null) continue;
                bool tls = string.Equals((string?)gui.Attribute("tls"), "true", StringComparison.OrdinalIgnoreCase);
                var addr = gui.Element("address")?.Value ?? "127.0.0.1:8384";
                // endereço pode ser "0.0.0.0:8384", "[::]:8384", "127.0.0.1:8384" ou unix socket
                var idx = addr.LastIndexOf(':');
                var port = idx >= 0 && int.TryParse(addr[(idx + 1)..], out var p) ? p : 8384;
                if (addr.StartsWith("unix", StringComparison.OrdinalIgnoreCase) || addr.StartsWith('/')) port = 8384;
                _cachedKey = gui.Element("apikey")?.Value;
                _cachedUrl = $"{(tls ? "https" : "http")}://127.0.0.1:{port}";
                ConfigFileUsed = path;
                ApiKeySource = string.IsNullOrEmpty(_cachedKey) ? "config.xml sem chave" : "config.xml";
                _credentialsInfo = $"Chave lida de {path}.";
                return;
            }
            catch { /* arquivo malformado: tenta o próximo */ }
        }
    }

    string? _credentialsInfo; // diagnóstico mostrado quando a API recusa a chave

    // ---- informações técnicas (menu → Informações técnicas) ----
    public IReadOnlyList<string> ConfigFiles { get; private set; } = [];
    public string? ConfigFileUsed { get; private set; }
    public string? ApiKeySource { get; private set; }
    public int? LastScriptExitCode { get; private set; }
    public string? LastScriptError { get; private set; }
    public string? LastApiError { get; private set; }

    const string SeeTechInfo = " Detalhes: menu → Informações técnicas.";

    /// <summary>Endereço e chave da API (para o fluxo de eventos, que tem conexão própria).</summary>
    public (string Url, string Key) ApiAccess(Settings s) => (GuiUrl(s), ApiKey(s));

    string ApiKey(Settings s)
    {
        var key = !string.IsNullOrWhiteSpace(s.ApiKey) ? s.ApiKey : _cachedKey ?? "";
        Log.AddSecret(key); // a chave nunca aparece no log, mesmo dentro de uma mensagem de erro
        return key;
    }

    /// <summary>Envia um comando à API (POST, PATCH…); lança exceção se o Syncthing recusar.</summary>
    async Task SendAsync(Settings s, HttpMethod method, string path, string? json = null)
    {
        using var req = new HttpRequestMessage(method, GuiUrl(s) + path);
        var key = ApiKey(s);
        if (key.Length > 0) req.Headers.Add("X-API-Key", key);
        if (json != null) req.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        using var resp = await _http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    static string Q(string value) => Uri.EscapeDataString(value);

    // ---- comandos (menus de Syncthing, pastas e dispositivos) ----

    /// <summary>Pede ao Syncthing para encerrar de forma limpa (POST /rest/system/shutdown).</summary>
    public Task ShutdownSyncthingAsync(Settings s) => SendAsync(s, HttpMethod.Post, "/rest/system/shutdown");

    /// <summary>
    /// Espera a API parar de responder (o Syncthing terminou de fechar) sem executar nada na distro: no
    /// desligamento do Windows, cada wsl.exe novo pode falhar. true = parou dentro do prazo.
    /// </summary>
    public async Task<bool> WaitApiDownAsync(Settings s, TimeSpan timeout)
    {
        var end = DateTime.Now + timeout;
        while (DateTime.Now < end)
        {
            try { await GetAsync(GuiUrl(s), ApiKey(s), "/rest/system/ping"); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TimeoutException) { return true; }
            await Task.Delay(500);
        }
        return false;
    }
    public Task RestartSyncthingAsync(Settings s) => SendAsync(s, HttpMethod.Post, "/rest/system/restart");

    /// <summary>Pausa ou retoma todos os dispositivos (como "Pausar tudo" da interface do Syncthing).</summary>
    public Task SetAllPausedAsync(Settings s, bool paused) =>
        SendAsync(s, HttpMethod.Post, paused ? "/rest/system/pause" : "/rest/system/resume");

    public Task SetDevicePausedAsync(Settings s, string deviceId, bool paused) =>
        SendAsync(s, HttpMethod.Post, $"/rest/system/{(paused ? "pause" : "resume")}?device={Q(deviceId)}");

    public Task SetFolderPausedAsync(Settings s, string folderId, bool paused) =>
        SendAsync(s, HttpMethod.Patch, $"/rest/config/folders/{Q(folderId)}", paused ? """{"paused":true}""" : """{"paused":false}""");

    public Task ScanFolderAsync(Settings s, string folderId) =>
        SendAsync(s, HttpMethod.Post, $"/rest/db/scan?folder={Q(folderId)}");

    /// <summary>Pede ao Syncthing para verificar já todas as pastas não pausadas (sem esperar o atraso do monitoramento).</summary>
    public async Task ScanAllAsync(Settings s)
    {
        var config = await GetAsync(GuiUrl(s), ApiKey(s), "/rest/config");
        foreach (var f in config.GetProperty("folders").EnumerateArray())
            if (!Bool(f, "paused")) await ScanFolderAsync(s, Str(f, "id") ?? "");
    }

    /// <summary>Erros recentes do Syncthing (GET /rest/system/error).</summary>
    public async Task<List<(DateTimeOffset When, string Message)>> GetErrorsAsync(Settings s) =>
        SyncthingParsers.Errors((await GetAsync(GuiUrl(s), ApiKey(s), "/rest/system/error")).GetRawText());

    /// <summary>
    /// Verifica se tudo está sincronizado (regras em <see cref="SyncthingApi.EvaluateSync"/>): busca o estado geral,
    /// o andamento de cada dispositivo conectado e o estado de cada pasta compartilhada.
    /// </summary>
    public async Task<SyncCheck> CheckSyncedAsync(Settings s)
    {
        var key = ApiKey(s);
        var url = GuiUrl(s);
        var tStatus = GetAsync(url, key, "/rest/system/status");
        var tConfig = GetAsync(url, key, "/rest/config");
        var tConns = GetAsync(url, key, "/rest/system/connections");
        await Task.WhenAll(tStatus, tConfig, tConns);
        var (status, config, conns) = (tStatus.Result, tConfig.Result, tConns.Result.GetProperty("connections"));
        var myId = Str(status, "myID");

        // em paralelo, como na leitura completa: no desligamento cada segundo conta
        var tCompletions = SyncthingApi.DevicesToCheck(config, conns, myId).Distinct()
            .Select(async id => (id, json: await GetAsync(url, key, "/rest/db/completion?device=" + Q(id)))).ToList();
        var tFolders = SyncthingApi.FoldersToCheck(config, myId).Distinct()
            .Select(async id => (id, json: await GetAsync(url, key, "/rest/db/status?folder=" + Q(id)))).ToList();
        await Task.WhenAll(tCompletions.Cast<Task>().Concat(tFolders)); // espera todos (nenhuma falha fica sem ser vista)
        var completions = tCompletions.ToDictionary(t => t.Result.id, t => t.Result.json);
        var folders = tFolders.ToDictionary(t => t.Result.id, t => t.Result.json);
        return SyncthingApi.EvaluateSync(status, config, conns, completions, folders);
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

    /// <summary>Busca o estado do Syncthing; a interpretação das respostas fica em <see cref="SyncthingApi"/>.</summary>
    async Task QuerySyncthingAsync(Snapshot snap, Settings s, string url)
    {
        var key = ApiKey(s);

        var tStatus = GetAsync(url, key, "/rest/system/status");
        var tVersion = GetAsync(url, key, "/rest/system/version");
        var tConfig = GetAsync(url, key, "/rest/config");
        var tConn = GetAsync(url, key, "/rest/system/connections");
        var tStats = GetAsync(url, key, "/rest/stats/device");
        await Task.WhenAll(tStatus, tVersion, tConfig, tConn, tStats);

        SyncthingApi.ApplySystem(snap, tStatus.Result, tVersion.Result);
        var conns = tConn.Result.TryGetProperty("connections", out var c) ? c : default;
        snap.Devices = SyncthingApi.Devices(tConfig.Result, conns, tStats.Result, snap.MyId);
        _rates.Apply(snap, tConn.Result, DateTime.UtcNow);
        snap.Folders = SyncthingApi.Folders(tConfig.Result, snap.MyId);

        // detalhes em paralelo; uma pasta ou dispositivo que falha não derruba o resto
        var folderTasks = snap.Folders.Where(f => !f.Paused).Select(async f =>
        {
            try { SyncthingApi.ApplyFolderStatus(f, await GetAsync(url, key, "/rest/db/status?folder=" + Q(f.Id))); }
            catch (Exception ex) { f.State = "error"; f.Error = ex.Message; }
        });
        var devTasks = snap.Devices.Where(d => d.Connected).Select(async d =>
        {
            try { SyncthingApi.ApplyCompletion(d, await GetAsync(url, key, "/rest/db/completion?device=" + Q(d.Id))); }
            catch (Exception ex) { Log.Failure("syncthing", "completion " + d.Id, $"andamento de {d.Name}: {ex.Message}"); }
        });
        await Task.WhenAll(folderTasks.Concat(devTasks));

        snap.AllFolders = snap.Folders;
        snap.Folders = snap.AllFolders.Where(f => !s.IsFolderHidden(f)).ToList();
        snap.AllDevices = snap.Devices;
        snap.Devices = snap.AllDevices.Where(d => !s.IsDeviceHidden(d)).ToList();
    }

    static string? Str(JsonElement e, string p) => SyncthingApi.Str(e, p);
    static bool Bool(JsonElement e, string p) => SyncthingApi.Bool(e, p);
    static string? NonEmpty(string? v) => SyncthingApi.NonEmpty(v);

    public void Dispose() => _http.Dispose();
}
