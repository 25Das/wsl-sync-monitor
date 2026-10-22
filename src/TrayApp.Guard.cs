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
        try { WslService.AdoptKeepAlives(); } catch { }
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

    void LoadKeepAlive()
    {
        if (!string.IsNullOrWhiteSpace(_settings.KeepAliveDistro))
            _keepAliveRequest = KeepAliveRequest.UntilStopped(_settings.KeepAliveDistro);
    }

    /// <summary>Executado após cada atualização (dados recentes do WSL). As regras estão em <see cref="GuardRules"/>.</summary>
    void GuardTick()
    {
        var d = _snap?.Target;
        if (d == null || _exiting) return;
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
        bool ok = await WaitSyncedAsync(distro, TimeSpan.FromMinutes(_settings.MaxHoldMinutes), ct, t => SetGuardText($"{distro}: {Lower(t)}"));
        if (ct.IsCancellationRequested || _guard != GuardState.Verifying) return;
        WslService.Release(distro);
        _guard = GuardState.Released;
        SetGuardText(ok ? $"{distro}: sincronizado, o WSL pode desligar."
                        : $"{distro}: limite de {_settings.MaxHoldMinutes} min atingido, o WSL pode desligar.");
    }

    /// <summary>
    /// Espera tudo sincronizar: força a verificação das pastas, espera pastas paradas e sem pendências e
    /// dispositivos conectados a 100%, estável por alguns segundos. Não usa a interface (pode rodar fora da thread de UI).
    /// </summary>
    async Task<bool> WaitSyncedAsync(string distro, TimeSpan max, CancellationToken ct, Action<string>? progress)
    {
        var end = DateTime.Now + max;
        bool scanned = false;
        DateTime? stableSince = null, failingSince = null;
        while (DateTime.Now < end && !ct.IsCancellationRequested)
        {
            try
            {
                if (!scanned) { await _collector.ScanAllAsync(_settings); scanned = true; }
                var c = await _collector.CheckSyncedAsync(_settings);
                failingSince = null;
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
                if ((DateTime.Now - failingSince.Value).TotalSeconds > 60)
                {
                    bool running = true;
                    try { running = await WslService.IsProcessRunningAsync(distro, "syncthing"); } catch { }
                    if (!running) return true; // nada a proteger
                }
            }
            try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { break; }
        }
        return false;
    }

    /// <summary>
    /// O Syncthing está rodando na distro (ligada)? Sem ele não há o que esperar: evita aguardar a API por mais
    /// de um minuto ao sair ou ao desligar o Windows. Na dúvida (falha ao perguntar), considera que está.
    /// </summary>
    static Task<bool> SyncthingMayBeRunningAsync(string distro) => WslService.ProcessMayBeRunningAsync(distro, "syncthing");

    static string Lower(string t) => t.Length > 0 ? char.ToLower(t[0]) + t[1..] : t;

    // ------------------------------------------------------------------ sincronização periódica

    void PeriodicTick(string distro)
    {
        if (_settings.PeriodicSyncMinutes <= 0 || !_settings.ProtectSync || _busy || _exiting) return;
        if (DateTime.Now - _lastRunningAt < TimeSpan.FromMinutes(_settings.PeriodicSyncMinutes)) return;
        _lastRunningAt = DateTime.Now;
        // liga a distro; o ciclo normal espera sincronizar e depois solta
        try { WslService.StartDistro(distro); SetGuardText("Sincronização periódica…"); } catch { }
    }

    // ------------------------------------------------------------------ saída do app

    /// <summary>Sair: se a distro está ligada só por causa do app, espera sincronizar antes de soltá-la.</summary>
    async void RequestExit()
    {
        if (_exiting) { _exitCts?.Cancel(); return; } // segundo clique: sair agora
        _exiting = true;
        _popup.HidePopup();
        var d = _snap?.Target;
        if (_settings.ProtectSync && d is { Running: true } && _guard != GuardState.Released &&
            WslService.CountExternalSessions(d.Name, DefaultDistro) == 0)
        {
            CancelVerify();
            _busy = true;
            _exitCts = new CancellationTokenSource();
            SetBusy("Aguardando a sincronização para sair…");
            if (await SyncthingMayBeRunningAsync(d.Name))
                await WaitSyncedAsync(d.Name, TimeSpan.FromMinutes(_settings.MaxHoldMinutes), _exitCts.Token,
                    t => SetBusy("Para sair: " + Lower(t)));
        }
        WslService.Release();
        ExitThread();
    }

    // ------------------------------------------------------------------ desligamento do Windows

    bool NeedsShutdownProtection() => _settings.ProtectSync && _snap?.Target is { Running: true };

    /// <summary>Roda numa thread de fundo enquanto o Windows espera (sem tocar na interface).</summary>
    async Task ShutdownSequenceAsync()
    {
        var d = _snap?.Target?.Name;
        if (d == null) return;
        _verifyCts?.Cancel();
        if (await SyncthingMayBeRunningAsync(d))
        {
            await WaitSyncedAsync(d, TimeSpan.FromSeconds(_settings.ShutdownMaxWaitSeconds), CancellationToken.None, null);
            try
            {
                try { await _collector.ShutdownSyncthingAsync(_settings); }
                catch { await WslService.SignalSyncthingAsync(d); }
                await WslService.WaitProcessExitAsync(d, "syncthing", TimeSpan.FromSeconds(30));
            }
            catch { }
        }
        try { await WslService.TerminateSafelyAsync(d); } catch { }
        WslService.Release();
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

    public event Action? SessionEnded;

    public ShutdownWatcher(Func<bool> needed, Func<Task> sequence, Func<int> timeoutSeconds)
    {
        _needed = needed; _sequence = sequence; _timeoutSeconds = timeoutSeconds;
        // janela de nível superior invisível (janelas "message-only" não recebem WM_QUERYENDSESSION)
        CreateHandle(new CreateParams { Caption = "WslSyncMonitor.Shutdown", Style = 0, ExStyle = Native.WS_EX_TOOLWINDOW });
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_QUERYENDSESSION)
        {
            if (!_done && _needed())
            {
                Native.ShutdownBlockReasonCreate(Handle, "Aguardando o Syncthing terminar a sincronização e encerrando o WSL…");
                try { Task.Run(_sequence).Wait(TimeSpan.FromSeconds(_timeoutSeconds())); } catch { }
                Native.ShutdownBlockReasonDestroy(Handle);
                _done = true;
            }
            m.Result = (IntPtr)1; // pode desligar
            return;
        }
        if (m.Msg == Native.WM_ENDSESSION)
        {
            if (m.WParam != IntPtr.Zero) SessionEnded?.Invoke();
            else _done = false; // desligamento cancelado
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
    }
}
