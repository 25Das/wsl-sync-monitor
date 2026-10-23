namespace WslSyncMonitor;

/// <summary>
/// Proteção de sincronização: enquanto a distro monitorada estiver ligada, o app a mantém ligada e só a solta
/// (deixando o desligamento por ociosidade do WSL agir) depois de confirmar que o Syncthing terminou de sincronizar.
/// Também cobre a saída do app, o desligamento do Windows e a sincronização periódica.
/// </summary>
public sealed partial class TrayApp
{
    GuardState _guard = GuardState.Off;
    CancellationTokenSource? _verifyCts;
    int _idleTicks;
    DateTime _lastRunningAt = DateTime.Now;
    bool _exiting;
    CancellationTokenSource? _exitCts;
    ShutdownWatcher? _shutdownWatcher;

    string? DefaultDistro => _snap?.Distros.FirstOrDefault(d => d.IsDefault)?.Name;

    void InitGuard()
    {
        // pede para ser avisado do desligamento do Windows antes dos outros apps
        Native.SetProcessShutdownParameters(0x3FF, 0);
        // depois de uma atualização: continua segurando a distro que a versão anterior segurava
        try { WslService.AdoptKeepAlives(); } catch (Exception ex) { Log.Warn("proteção", "assumir as distros da versão anterior", ex); }
        LoadKeepAlive();
        _shutdownWatcher = new ShutdownWatcher(NeedsShutdownProtection, ShutdownSequenceAsync,
            // espera + parar o Syncthing (~45 s) + sync e terminate (até 120 s), com folga
            () => _settings.ShutdownMaxWaitSeconds + 300);
        _shutdownWatcher.SessionEnded += () => { WslService.Release(); ExitThread(); };
    }

    string? _guardText;
    void SetGuardText(string? text) { _guardText = text; _popup.SetGuard(text); }

    /// <summary>A distro monitorada vai mudar: solta a atual e recomeça a proteção do zero na nova.</summary>
    void ResetGuardForDistroChange()
    {
        CancelVerify();
        if (_snap?.Target?.Name is { } old) WslService.Release(old);
        _guard = GuardState.Off;
        _idleTicks = 0;
        SetGuardText(null);
        _collector.InvalidateCredentials();
    }

    // ---- Manter ligada ----

    KeepAliveRequest? _keepAliveRequest;

    /// <summary>"Manter ligada" vale agora para a distro?</summary>
    bool IsKeptAlive(string distro) =>
        _keepAliveRequest is { } k && k.Distro.Equals(distro, StringComparison.OrdinalIgnoreCase) && k.IsActive(DateTime.Now);

    string? KeepAliveText => _keepAliveRequest is { } k && k.IsActive(DateTime.Now) ? k.Describe() : null;

    /// <summary>Pede para manter a distro ligada (null = até desligar) ou cancela o pedido (distro null).</summary>
    void SetKeepAlive(string? distro, TimeSpan? duration)
    {
        _keepAliveRequest = distro == null ? null
            : duration is { } d ? KeepAliveRequest.For(distro, d, DateTime.Now)
            : KeepAliveRequest.UntilStopped(distro);
        _settings.KeepAliveDistro = _keepAliveRequest is { Persist: true } r ? r.Distro : "";
        SaveSettings();
        if (distro != null && Distro(distro) is { Running: false }) _ = StartDistroAsync(distro);
        RefreshNow();
    }

    bool _launchChecked;

    /// <summary>Na primeira leitura do estado: "Manter ligada (até você desligar)" liga a distro se estiver desligada.</summary>
    void StartKeptAliveAtLaunch(Snapshot snap)
    {
        if (_launchChecked) return;
        _launchChecked = true;
        if (_keepAliveRequest is not { } k || !k.StartAtLaunch(snap.Distros.FirstOrDefault(d =>
                d.Name.Equals(k.Distro, StringComparison.OrdinalIgnoreCase)))) return;
        Log.Info("proteção", $"{k.Distro}: ligando ao iniciar (Manter ligada até você desligar)");
        _ = StartDistroAsync(k.Distro);
    }

    void LoadKeepAlive()
    {
        if (!string.IsNullOrWhiteSpace(_settings.KeepAliveDistro))
            _keepAliveRequest = KeepAliveRequest.UntilStopped(_settings.KeepAliveDistro);
    }

    /// <summary>Executado após cada atualização (dados recentes do WSL). As regras estão em <see cref="GuardRules"/>.</summary>
    void GuardTick()
    {
        var d = _snap?.Target;
        if (d == null || _exiting || WslService.SessionEnding) return;
        if (d.Running) _lastRunningAt = DateTime.Now;

        // "Manter ligada" por tempo: quando expira, deixa de valer
        if (_keepAliveRequest is { Persist: false } k && !k.IsActive(DateTime.Now)) _keepAliveRequest = null;

        bool canCount = d.Running && !_busy && (_settings.ProtectSync || IsKeptAlive(d.Name));
        var input = new GuardInput(
            Running: d.Running,
            ProtectEnabled: _settings.ProtectSync,
            Busy: _busy,
            Sessions: canCount ? WslService.CountExternalSessions(d.Name, DefaultDistro) : 0,
            KeepAliveActive: IsKeptAlive(d.Name),
            IdleTicks: _idleTicks);
        var step = GuardRules.Step(_guard, input);

        if (step.Actions.HasFlag(GuardActions.CancelVerify)) CancelVerify();
        if (step.Actions.HasFlag(GuardActions.Release)) WslService.Release(d.Name);
        if (step.Actions.HasFlag(GuardActions.Hold)) Hold(d.Name);
        _guard = step.State;
        _idleTicks = step.IdleTicks;
        if (step.Actions.HasFlag(GuardActions.StartVerify)) StartVerify(d.Name);

        switch (_guard)
        {
            case GuardState.KeptAlive:
                SetGuardText($"{d.Name} mantida ligada pelo app {KeepAliveText}.");
                break;
            case GuardState.InUse:
                SetGuardText($"{d.Name} em uso por {input.Sessions} programa(s). Ao fechar, espera sincronizar antes de desligar.");
                break;
            case GuardState.Off:
                SetGuardText(null);
                if (!d.Running) PeriodicTick(d.Name);
                break;
        }
    }

    void Hold(string distro)
    {
        if (!WslService.IsHolding(distro)) WslService.StartDistro(distro);
    }

    void CancelVerify() { _verifyCts?.Cancel(); _verifyCts = null; }

    void StartVerify(string distro)
    {
        _guard = GuardState.Verifying;
        _idleTicks = 0;
        _verifyCts?.Cancel();
        _verifyCts = new CancellationTokenSource();
        _ = VerifyThenReleaseAsync(distro, _verifyCts.Token);
    }

    async Task VerifyThenReleaseAsync(string distro, CancellationToken ct)
    {
        SetGuardText($"{distro}: verificando a sincronização antes de liberar o desligamento…");
        Log.Info("proteção", $"{distro}: sem sessões; verificando a sincronização antes de liberar");
        bool ok = await WaitSyncedAsync(distro, TimeSpan.FromMinutes(_settings.MaxHoldMinutes), StartupGrace,
            t => SetGuardText($"{distro}: {Lower(t)}"), ct);
        if (ct.IsCancellationRequested || _guard != GuardState.Verifying) return;
        WslService.Release(distro);
        _guard = GuardState.Released;
        Log.Info("proteção", ok ? $"{distro}: sincronizado" : $"{distro}: limite de {_settings.MaxHoldMinutes} min atingido sem confirmar a sincronização");
        SetGuardText(ok ? $"{distro}: sincronizado, o WSL pode desligar."
                        : $"{distro}: limite de {_settings.MaxHoldMinutes} min atingido, o WSL pode desligar.");
    }

    /// <summary>
    /// Espera tudo sincronizar: força a verificação das pastas, espera pastas paradas e sem pendências e
    /// dispositivos conectados a 100%, estável por alguns segundos. Não usa a interface (pode rodar fora da thread de UI).
    /// Se a API não responde por <paramref name="syncthingGrace"/>, confere se o Syncthing existe; sem ele, não há o
    /// que esperar. Carência maior logo após ligar a distro (ele ainda está subindo); zero ao sair e ao desligar.
    /// </summary>
    async Task<bool> WaitSyncedAsync(string distro, TimeSpan max, TimeSpan syncthingGrace, Action<string>? progress, CancellationToken ct)
    {
        var end = DateTime.Now + max;
        bool scanned = false;
        DateTime? stableSince = null, failingSince = null;
        bool checkedProcess = false;
        while (DateTime.Now < end && !ct.IsCancellationRequested)
        {
            try
            {
                if (!scanned) { await _collector.ScanAllAsync(_settings); scanned = true; }
                var c = await _collector.CheckSyncedAsync(_settings);
                failingSince = null;
                checkedProcess = false;
                // logo após ligar, o Syncthing ainda está conectando e trocando índices
                bool warm = c.AllDevicesConnected || c.Uptime.TotalSeconds >= _settings.StartupGraceSeconds;
                if (c.Synced && warm)
                {
                    stableSince ??= DateTime.Now;
                    if ((DateTime.Now - stableSince.Value).TotalSeconds >= _settings.StableSeconds) return true;
                    progress?.Invoke("Confirmando a sincronização…");
                }
                else
                {
                    stableSince = null;
                    progress?.Invoke(!c.Synced ? $"Aguardando a sincronização ({c.Detail})…" : "Aguardando os dispositivos conectarem…");
                }
            }
            catch
            {
                // Syncthing ainda iniciando, ou não está rodando nesta distro
                stableSince = null;
                failingSince ??= DateTime.Now;
                progress?.Invoke("Aguardando o Syncthing…");
                // a distro foi desligada por fora: não roda nada nela (qualquer comando a religaria)
                if (Distro(distro)?.Running != true) return true;
                // uma vez por falha contínua (cada conferência executa um wsl.exe)
                if (!checkedProcess && DateTime.Now - failingSince.Value >= syncthingGrace)
                {
                    checkedProcess = true;
                    if (!await WslService.ProcessMayBeRunningAsync(distro, "syncthing")) return true; // nada a proteger
                }
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
        return false;
    }

    /// <summary>Logo após ligar a distro, o Syncthing ainda está subindo: quanto esperar a API antes de concluir que ele não existe.</summary>
    static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(60);

    static string Lower(string t) => PanelHeader.Lower(t);

    // ------------------------------------------------------------------ sincronização periódica

    void PeriodicTick(string distro)
    {
        if (_settings.PeriodicSyncMinutes <= 0 || !_settings.ProtectSync || _busy || _exiting) return;
        if (DateTime.Now - _lastRunningAt < TimeSpan.FromMinutes(_settings.PeriodicSyncMinutes)) return;
        _lastRunningAt = DateTime.Now;
        // liga a distro; o ciclo normal espera sincronizar e depois solta
        try
        {
            Log.Info("proteção", $"{distro}: ligando para a sincronização periódica");
            WslService.StartDistro(distro);
            SetGuardText("Sincronização periódica…");
        }
        catch (Exception ex) { Log.Warn("proteção", $"{distro}: ligar para a sincronização periódica", ex); }
    }

    // ------------------------------------------------------------------ saída do app

    /// <summary>Sair: se a distro está ligada só por causa do app, espera sincronizar antes de soltá-la.</summary>
    async void RequestExit()
    {
        if (_exiting) { _exitCts?.Cancel(); Log.Info("app", "sair agora (sem esperar)"); return; } // segundo clique
        _exiting = true;
        Log.Info("app", "saindo");
        _popup.HidePopup();
        var d = _snap?.Target;
        if (_settings.ProtectSync && d is { Running: true } && _guard != GuardState.Released &&
            WslService.CountExternalSessions(d.Name, DefaultDistro) == 0)
        {
            CancelVerify();
            _busy = true;
            _exitCts = new CancellationTokenSource();
            SetBusy("Aguardando a sincronização para sair…");
            await WaitSyncedAsync(d.Name, TimeSpan.FromMinutes(_settings.MaxHoldMinutes), TimeSpan.Zero,
                t => SetBusy("Para sair: " + Lower(t)), _exitCts.Token);
        }
        WslService.Release();
        ExitThread();
    }

    // ------------------------------------------------------------------ desligamento do Windows

    bool NeedsShutdownProtection() => _settings.ProtectSync && _snap?.Target is { Running: true };

    /// <summary>
    /// Roda numa thread de fundo enquanto o Windows espera (sem tocar na interface). Executa o mínimo de wsl.exe
    /// possível (com a sessão terminando, cada um pode falhar): espera e para o Syncthing pela API, e só usa o
    /// wsl.exe para o sinal de reserva e para gravar o disco e encerrar a distro. Cada passo vai para o log.
    /// </summary>
    async Task ShutdownSequenceAsync()
    {
        var d = _snap?.Target?.Name;
        if (d == null) return;
        var total = System.Diagnostics.Stopwatch.StartNew();
        _verifyCts?.Cancel();

        if (_snap?.St == StState.Running)
        {
            Log.Info("desligamento", $"{d}: esperando a sincronização (até {_settings.ShutdownMaxWaitSeconds} s)");
            // carência infinita: aqui não dá para perguntar à distro se o Syncthing existe (seria um wsl.exe novo)
            bool synced = await WaitSyncedAsync(d, TimeSpan.FromSeconds(_settings.ShutdownMaxWaitSeconds), TimeSpan.MaxValue,
                null, CancellationToken.None);
            Log.Info("desligamento", synced ? $"{d}: sincronizado ({total.Elapsed.TotalSeconds:0} s)" : $"{d}: limite de espera atingido, seguindo");
        }
        else Log.Info("desligamento", $"{d}: a API do Syncthing já não respondia ({_snap?.StStatusText}); sem espera");

        bool stopped = false;
        try
        {
            await _collector.ShutdownSyncthingAsync(_settings);
            stopped = await _collector.WaitApiDownAsync(_settings, TimeSpan.FromSeconds(30));
            Log.Info("desligamento", stopped ? "Syncthing parado pela API" : "Syncthing não parou em 30 s após o pedido");
        }
        catch (Exception ex) { Log.Info("desligamento", $"a API não atendeu ao pedido de parar ({ex.Message})"); }
        if (!stopped && await WslService.RequestInDistroAsync(d, "stop-syncthing", "stopped", TimeSpan.FromSeconds(40)))
        {
            stopped = true;
            Log.Info("desligamento", "Syncthing parado por sinal (pedido ao processo de manutenção)");
        }

        // grava os dados em disco; a distro é encerrada pelo próprio Windows ao desligar
        Log.Info("desligamento", await WslService.RequestInDistroAsync(d, "sync", "synced", TimeSpan.FromSeconds(30))
            ? $"{d}: dados gravados em disco"
            : $"{d}: sem processo de manutenção para gravar os dados (o Syncthing {(stopped ? "já fechou" : "não fechou")} o banco de dados)");
        WslService.Release();
        Log.Info("desligamento", $"sequência concluída em {total.Elapsed.TotalSeconds:0} s");
    }
}

/// <summary>
/// Janela oculta que recebe o aviso de desligamento/saída do Windows. Segura a resposta (o Windows mostra o motivo
/// "aguardando…" na tela de desligamento) até a sequência segura terminar; depois o desligamento continua sozinho.
/// O "Desligar mesmo assim" do Windows continua disponível.
/// </summary>
sealed class ShutdownWatcher : NativeWindow
{
    readonly Func<bool> _needed;
    readonly Func<Task> _sequence;
    readonly Func<int> _timeoutSeconds;
    bool _done;
    const long ENDSESSION_CLOSEAPP = 0x1;

    public event Action? SessionEnded;

    public ShutdownWatcher(Func<bool> needed, Func<Task> sequence, Func<int> timeoutSeconds)
    {
        _needed = needed; _sequence = sequence; _timeoutSeconds = timeoutSeconds;
        // janela de nível superior invisível (janelas "message-only" não recebem WM_QUERYENDSESSION). Enquanto segura
        // o desligamento, o Windows a lista na tela "Estes aplicativos estão impedindo o desligamento": com o nome
        // e o ícone do app, para ficar claro quem está esperando e por quê
        CreateHandle(new CreateParams { Caption = InstallPaths.AppName, Style = 0, ExStyle = Native.WS_EX_TOOLWINDOW });
        var icon = AppIcon.Get();
        if (icon != null)
        {
            Native.SendMessage(Handle, Native.WM_SETICON, (IntPtr)Native.ICON_BIG, icon.Handle);
            Native.SendMessage(Handle, Native.WM_SETICON, (IntPtr)Native.ICON_SMALL, icon.Handle);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_QUERYENDSESSION)
        {
            // ENDSESSION_CLOSEAPP: um instalador pede para fechar o app; a sessão continua
            bool sessionEnding = ((long)m.LParam & ENDSESSION_CLOSEAPP) == 0;
            if (sessionEnding && !WslService.SessionEnding)
            {
                // a partir daqui, wsl.exe novos falhariam (0xc0000142): as atualizações e a proteção param
                WslService.SessionEnding = true;
                Log.Info("desligamento", "o Windows está encerrando a sessão");
            }
            if (!_done && _needed())
            {
                Native.ShutdownBlockReasonCreate(Handle, "Aguardando o Syncthing terminar a sincronização e encerrando o WSL…");
                try
                {
                    if (!Task.Run(_sequence).Wait(TimeSpan.FromSeconds(_timeoutSeconds())))
                        Log.Warn("desligamento", $"sequência não terminou em {_timeoutSeconds()} s; liberando o desligamento");
                }
                catch (Exception ex) { Log.Error("desligamento", "sequência de desligamento", ex); }
                Native.ShutdownBlockReasonDestroy(Handle);
                _done = true;
            }
            m.Result = (IntPtr)1; // pode desligar
            return;
        }
        if (m.Msg == Native.WM_ENDSESSION)
        {
            if (m.WParam != IntPtr.Zero)
            {
                Log.Info("desligamento", "sessão encerrada; saindo");
                SessionEnded?.Invoke();
            }
            else if (WslService.SessionEnding || _done)
            {
                // desligamento cancelado (pelo usuário ou por outro app): volta ao normal
                _done = false;
                WslService.SessionEnding = false;
                Log.Info("desligamento", "cancelado; o monitoramento continua");
            }
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
}
