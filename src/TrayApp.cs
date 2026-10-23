using System.Diagnostics;

namespace WslSyncMonitor;

public sealed partial class TrayApp : ApplicationContext, IPopupActions
{
    Settings _settings;
    DateTime _settingsMtime;
    readonly StatusCollector _collector = new();
    readonly IconRenderer _icons = new();
    readonly NotifyIcon _tray;
    readonly DetailPopup _popup;
    readonly WidgetHost _widget;
    readonly ContextMenuStrip _menu;
    readonly Form _menuOwner;
    readonly System.Windows.Forms.Timer _refreshTimer = new();
    readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 100 };
    readonly Updater _updater = new();
    readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 24 * 60 * 60 * 1000 };
    bool _handingOver; // fechando para outra instância (atualização ou instalação) assumir: não solta a distro
    EventWaitHandle? _exitEvent;
    RegisteredWaitHandle? _exitWait;
    Version? _notifiedVersion;

    Snapshot? _snap;
    bool _refreshing;

    // hover (apenas no widget; o ícone da bandeja só responde a cliques)
    bool _widgetHover;
    DateTime _hoverStart, _outsideSince = DateTime.MaxValue;
    bool _busy;
    string? _busyText;

    public TrayApp()
    {
        _settings = Settings.LoadOrDefault(out var settingsError);
        _settingsMtime = SettingsMtime();

        _popup = new DetailPopup(this); // cria o SynchronizationContext do WinForms
        _menu = BuildMenu();

        // janela invisível dona do menu: sem ela o menu ganha botão próprio na barra de tarefas
        // e não fecha ao clicar fora
        _menuOwner = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            Size = new Size(1, 1),
            Opacity = 0,
            Text = "",
        };
        _menuOwner.Show();
        _menuOwner.Hide();
        // com um menu aberto, o widget (no modo por cima da barra) não se recoloca acima dele
        _menu.Opened += (_, _) => { if (_widget != null) _widget.MenuOpen = true; };
        _menu.Closed += (_, _) => { if (_widget == null) return; _widget.MenuOpen = false; _widget.BringAboveTaskbar(); };
        _menu.Closing += KeepMenuOpenIfAsked;
        _popup.VisibleChanged += (_, _) =>
        {
            if (!_popup.Visible) _widget?.BringAboveTaskbar();
            else if (_snap == null || DateTime.Now - _snap.Time > TimeSpan.FromSeconds(Math.Max(2, _settings.RefreshSeconds))) RequestRefresh();
            UpdateRefreshInterval();
        };

        _tray = new NotifyIcon
        {
            Icon = _icons.Render(TrayIconModel.From(null, false, false)),
            Text = AppName,
            Visible = true,
        };
        // menu e painel são tratados manualmente: esquerdo = painel, direito = menu (nunca os dois)
        _tray.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right) ShowMenu(Cursor.Position);
            else if (e.Button == MouseButtons.Left) { CloseMenu(); TogglePinned(Cursor.Position); }
        };
        if (settingsError != null) WarnInvalidSettings(settingsError, startup: true);
        _tray.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left && _snap?.St == StState.Running) OpenSyncthing(); };

        _widget = new WidgetHost(_settings);
        _widget.Clicked += () => { CloseMenu(); TogglePinned(_widget.PopupAnchor); };
        _widget.RightClicked += () => ShowMenu(Cursor.Position);
        _widget.HoverStarted += () =>
        {
            if (!_settings.HoverPopup || _popup.Visible || _menu.Visible) return;
            _widgetHover = true; _hoverStart = DateTime.Now;
        };
        _widget.OffsetChanged += SaveSettings;

        _hoverTimer.Tick += (_, _) => HoverTick();
        _hoverTimer.Start();

        _refreshTimer.Tick += async (_, _) => await RefreshAsync();
        InitGuard();
        ApplySettings();
        _updateTimer.Tick += async (_, _) => await CheckUpdateAsync();
        _updateTimer.Start();
        _tray.BalloonTipClicked += (_, _) =>
        {
            // cada notificação leva para onde se resolve o que ela avisa
            if (_balloon == BalloonKind.Update && _updater.Available != null) InstallUpdate();
            else if (_balloon == BalloonKind.Health) ShowPopup(_widget.Visible ? _widget.PopupAnchor : Cursor.Position, pin: true);
        };
        StartRealTime();
        _ = RefreshAsync();
        _ = CheckUpdateAsync(retryOnFail: true); // procura atualização logo ao iniciar (e depois a cada 24 h)
        ListenForExitRequests();
    }

    // ------------------------------------------------------------------ estado (atualização periódica)

    async Task RefreshAsync()
    {
        if (_refreshing || WslService.SessionEnding) return; // sessão terminando: nada de wsl.exe novos
        _refreshing = true;
        try
        {
            ReloadSettingsIfChanged();
            // depois de soltar a distro, não executa nada dentro dela (deixa o WSL desligar por ociosidade)
            var snap = await _collector.RefreshAsync(_settings, allowDistroCommands: _guard != GuardState.Released);
            if (_snap == null)
                Log.Info("atualização", $"primeira leitura do estado em {(DateTime.Now - System.Diagnostics.Process.GetCurrentProcess().StartTime).TotalSeconds:0.0} s " +
                    $"(WSL: {(snap.WslError ?? (snap.WslRunning ? "ligado" : "desligado"))}, Syncthing: {snap.StStatusText})");
            bool probe = RefreshRules.ShouldProbe(_snap, snap);
            _snap = snap;
            SetSyncthingUp(snap.St == StState.Running);
            if (probe) _ = ProbeSyncthingAsync();
            StartupHealthCheck(snap);
            StartKeptAliveAtLaunch(snap);
            GuardTick();
            UpdateRefreshInterval();
            ShowState();
            _ = RefreshDiskFreeAsync();
        }
        catch (WslService.SessionEndingException) { /* o Windows começou a encerrar a sessão no meio da atualização */ }
        catch (Exception ex)
        {
            Log.Failure("atualização", "refresh", "falha ao atualizar o estado: " + ex.Message);
            _tray.Text = Trim("Erro: " + ex.Message, 127);
        }
        finally
        {
            _refreshing = false;
            if (_refreshAgain) { _refreshAgain = false; _ = RefreshAsync(); }
        }
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Ícone e texto da bandeja a partir do estado (regras em <see cref="TrayIconModel"/>).</summary>
    void UpdateTrayIcon() => UpdateTrayIcon(Display);

    void UpdateTrayIcon(Snapshot? shown)
    {
        var kept = shown?.Target is { } t && IsKeptAlive(t.Name);
        _tray.Icon = _icons.Render(TrayIconModel.From(shown, kept, _updater.Available != null));
        _tray.Text = _busyText != null
            ? Trim($"{AppName} · {_busyText}", 127)
            : TrayIconModel.Tooltip(shown, kept ? KeepAliveText : null, _updater.Available?.Version);
    }

    static DateTime SettingsMtime() { try { return File.GetLastWriteTimeUtc(Settings.FilePath); } catch { return default; } }

    void ReloadSettingsIfChanged()
    {
        var m = SettingsMtime();
        if (m == _settingsMtime) return;
        var fresh = Settings.TryLoad(out var error);
        if (fresh == null)
        {
            // inválido ou ainda sendo gravado: mantém as configurações atuais e tenta de novo na próxima atualização
            if (!_settingsErrorShown) WarnInvalidSettings(error, startup: false);
            _settingsErrorShown = true;
            return;
        }
        _settingsMtime = m;
        _settingsErrorShown = false;
        if (!string.Equals(fresh.Distro, _settings.Distro, StringComparison.OrdinalIgnoreCase)) ResetGuardForDistroChange();
        // só descarta a chave/URL descobertas se a configuração de acesso mudou
        bool accessChanged = fresh.ApiKey != _settings.ApiKey || fresh.SyncthingUrl != _settings.SyncthingUrl ||
                             fresh.SyncthingConfigPath != _settings.SyncthingConfigPath;
        // copia para a instância compartilhada com o widget
        foreach (var p in typeof(Settings).GetProperties().Where(p => p.CanWrite)) p.SetValue(_settings, p.GetValue(fresh));
        if (accessChanged) { _collector.InvalidateCredentials(); RestartEvents(); }
        ApplySettings();
    }

    bool _settingsErrorShown;

    enum BalloonKind { Other, Update, Health }
    BalloonKind _balloon;

    void ShowBalloon(BalloonKind kind, int ms, string title, string text, ToolTipIcon icon)
    {
        _balloon = kind;
        _tray.ShowBalloonTip(ms, title, text, icon);
    }

    // ------------------------------------------------------------------ verificação de pastas e dispositivos

    bool _healthChecked;

    /// <summary>
    /// Ao iniciar (como a busca de atualização): na primeira vez que o Syncthing estiver pronto nesta sessão,
    /// procura problemas em pastas e dispositivos e avisa só se houver algo e com o painel e os menus fechados.
    /// Com a distro desligada ao iniciar, acontece quando o Syncthing responder pela primeira vez.
    /// </summary>
    void StartupHealthCheck(Snapshot snap)
    {
        if (_healthChecked || !HealthCheck.Ready(snap, _settings.StartupGraceSeconds)) return;
        _healthChecked = true;
        var issues = HealthCheck.Find(snap, DateTime.Now, _settings.DeviceStaleDays);
        Log.Info("verificação", issues.Count == 0 ? "pastas e dispositivos: tudo certo"
            : $"pastas e dispositivos: {string.Join("; ", issues.Select(i => i.Text))}");
        if (issues.Count > 0 && !UiOpen)
            ShowBalloon(BalloonKind.Health, 10000, "Pastas e dispositivos", HealthCheck.Summary(issues),
                issues.Any(i => i.Level == Level.Error) ? ToolTipIcon.Error : ToolTipIcon.Warning);
    }

    /// <summary>Syncthing → Verificar pastas e dispositivos: o resultado numa janela.</summary>
    void ShowHealthCheck()
    {
        if (_snap == null) return;
        var issues = HealthCheck.Find(_snap, DateTime.Now, _settings.DeviceStaleDays);
        ShowText("Pastas e dispositivos", () => Task.FromResult(issues.Count == 0
            ? "Nenhum problema encontrado nas pastas e nos dispositivos mostrados no painel."
            : string.Join(Environment.NewLine, issues.Select(i => (i.Level == Level.Error ? "✕ " : "! ") + i.Text))));
    }

    void WarnInvalidSettings(string? error, bool startup) =>
        ShowBalloon(BalloonKind.Other, 8000, AppName,
            (startup ? "settings.json inválido; usando os padrões (cópia em settings.json.invalido). "
                     : "settings.json inválido; as configurações atuais foram mantidas. ") + error, ToolTipIcon.Warning);

    void SaveSettings()
    {
        _settings.Save();
        _settingsMtime = SettingsMtime();
    }

    void ApplySettings()
    {
        UpdateRefreshInterval();
        _refreshTimer.Start();
    }

    /// <summary>Lê menos vezes quando nada depende da leitura periódica (regras em <see cref="RefreshRules"/>).</summary>
    void UpdateRefreshInterval()
    {
        int ms = (int)RefreshRules.Interval(_settings.RefreshSeconds, _eventsLive, _popup.Visible, guardCounting: _idleTicks > 0).TotalMilliseconds;
        if (_refreshTimer.Interval != ms) _refreshTimer.Interval = ms; // trocar reinicia a contagem: só quando muda
    }

    bool _refreshAgain;

    /// <summary>Lê o estado completo agora (algo mudou); se uma leitura já está em andamento, faz outra logo depois dela.</summary>
    void RequestRefresh()
    {
        if (_refreshing) _refreshAgain = true;
        else _ = RefreshAsync();
    }

    // ------------------------------------------------------------------ hover / clique

    void HoverTick()
    {
        var cur = Cursor.Position;

        // aberto por clique (ou fixado): clique fora fecha, conforme PanelBehavior
        // (reforço caso a janela não tenha recebido o foco)
        var state = _popup.State;
        if (state.Visible && (state.Clicked || state.Locked))
        {
            _widgetHover = false;
            bool inMenu = (_itemMenu is { Visible: true } im && im.Bounds.Contains(cur)) || (_menu.Visible && _menu.Bounds.Contains(cur));
            if (Control.MouseButtons != MouseButtons.None && !_popup.Bounds.Contains(cur) && !inMenu) _popup.Apply(PanelEvent.ClickOutside);
            return;
        }

        bool overWidget = _widget.Visible && _widget.ScreenBounds.Contains(cur);

        if (!_popup.Visible)
        {
            if (!_widgetHover) return;
            if (!overWidget || _menu.Visible) { _widgetHover = false; return; }
            if ((DateTime.Now - _hoverStart).TotalMilliseconds >= _settings.HoverDelayMs)
            {
                ShowPopup(_widget.PopupAnchor, pin: false);
                _outsideSince = DateTime.MaxValue;
            }
            return;
        }

        var popupArea = _popup.Bounds; popupArea.Inflate(8, 8);
        if (overWidget || popupArea.Contains(cur)) { _outsideSince = DateTime.MaxValue; return; }
        if (_outsideSince == DateTime.MaxValue) _outsideSince = DateTime.Now;
        else if ((DateTime.Now - _outsideSince).TotalMilliseconds > 400)
        {
            _popup.Apply(PanelEvent.MouseLeft);
            _widgetHover = false;
            _outsideSince = DateTime.MaxValue;
        }
    }

    void ShowMenu(Point pt)
    {
        _popup.HidePopup();
        _widgetHover = false;
        var dir = TaskbarInfo.Get().Edge switch
        {
            TaskbarEdge.Top => ToolStripDropDownDirection.BelowLeft,
            TaskbarEdge.Left => ToolStripDropDownDirection.AboveRight,
            _ => ToolStripDropDownDirection.AboveLeft,
        };
        _menuOwner.Show();
        Native.SetForegroundWindow(_menuOwner.Handle); // sem o foco, o menu não fecha ao clicar fora
        _menu.Show(_menuOwner, _menuOwner.PointToClient(pt), dir);
    }

    void CloseMenu() { if (_menu.Visible) _menu.Close(ToolStripDropDownCloseReason.AppClicked); }

    void TogglePinned(Point anchor)
    {
        // o clique que fechou o painel (perda de foco) não deve reabri‑lo
        if (!_popup.Visible && (DateTime.Now - _popup.LastHiddenAt).TotalMilliseconds < 300) return;
        if (_popup.Apply(PanelEvent.SourceClicked) == PanelAction.ShowClicked) ShowPopup(anchor, pin: true);
    }

    void ShowPopup(Point anchor, bool pin)
    {
        if (Display is { } shown) _popup.SetSnapshot(shown);
        _popup.ShowAt(anchor, pin);
        if (_snap == null || (DateTime.Now - _snap.Time).TotalSeconds > 2) _ = RefreshAsync();
    }

    // ------------------------------------------------------------------ ações

    public void RefreshNow() => _ = RefreshAsync();

    /// <summary>Botões do rodapé do painel (definidos em <see cref="PanelFooter"/>).</summary>
    public void RunFooterAction(FooterAction action)
    {
        switch (action)
        {
            case FooterAction.OpenSyncthing: OpenSyncthing(); break;
            case FooterAction.StartDistro when TargetDistro is { } d: _ = StartDistroAsync(d); break;
            case FooterAction.StopDistro when TargetDistro is { } d: _ = StopDistroAsync(d); break;
        }
    }

    void SetBusy(string? text)
    {
        _busyText = text;
        _popup.SetBusy(text);
        _renderUpdateMenuItem?.Invoke(); // "Atualizar para…" fica desativado durante a operação
        _widget.Busy = text != null;
        UpdateTrayIcon();
    }

    /// <summary>Outra instância (instalador, desinstalação) pede para esta fechar: sai com a saída segura.</summary>
    void ListenForExitRequests()
    {
        try
        {
            _exitEvent = AppInstance.CreateExitEvent();
            var ui = SynchronizationContext.Current!;
            _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => ui.Post(_ => { if (!_exiting) RequestExit(); }, null), // já saindo: não vira "sair agora"
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex) { Log.Warn("app", "escutar pedidos de saída", ex); }
    }

    /// <summary>Preferências → Instalar…: instala esta cópia (ou abre a instalada, se for mais nova) e passa para ela.</summary>
    void InstallFromMenu()
    {
        var installer = Installer.ForCurrentUser();
        var installed = installer.InstalledVersion();
        var kind = InstallDecision.Decide(Updater.Current, installed);
        bool started;
        if (kind == InstallKind.InstalledIsNewer)
        {
            if (MessageBox.Show($"A versão {installed!.ToString(3)}, mais nova que esta, já está instalada. Abrir a versão instalada?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            started = Program.StartInstalled(installer);
        }
        else
        {
            using var form = new InstallForm(InstallDecision.Title(kind, Updater.Current, installed), installer.Paths.Directory,
                offerDontAsk: false);
            if (form.ShowDialog() != DialogResult.OK) return;
            started = Program.InstallAndStart(installer, form.Options);
        }
        if (!started) return;
        _handingOver = true;
        ExitThread();
    }

    static bool IsInstalled => InstallPaths.Current.IsInstalledExe(Environment.ProcessPath!);

    static void Uninstall() => Try(() =>
        Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Program.UninstallArg) { UseShellExecute = true }));

    // ------------------------------------------------------------------ atualização do app (GitHub)

    UpdateState _updateState = UpdateState.NotChecked;

    /// <summary>Painel ou algum menu aberto: o resultado de uma busca aparece neles, sem notificação.</summary>
    bool UiOpen => _popup.Visible || _menu.Visible || _itemMenu is { Visible: true };

    void SetUpdateState(UpdateState state)
    {
        _updateState = state;
        _popup.SetUpdate(state);
        _renderUpdateMenuItem?.Invoke(); // menu aberto: o item mostra o andamento e o resultado na hora
        UpdateTrayIcon(); // marca de atualização pendente
    }

    /// <summary>Painel (recarregar, clique na versão) e menu: o resultado aparece no painel e no menu, sem notificação.</summary>
    public void CheckForUpdate() => _ = CheckUpdateAsync();

    async Task CheckUpdateAsync(bool retryOnFail = false)
    {
        if (_updateState.Status == UpdateStatus.Checking) return;
        var previous = _updateState;
        SetUpdateState(UpdateState.Checking);
        try
        {
            var u = await _updater.CheckAsync();
            SetUpdateState(u != null ? UpdateState.Found(u.Version, DateTime.Now) : UpdateState.UpToDate(DateTime.Now));
            Log.Recovered("atualização do app", "update", "verificação de atualizações voltou a funcionar");
            if (u != null && u.Version != _notifiedVersion) Log.Info("atualização do app", $"versão {u.Version.ToString(3)} disponível");
            if (UpdateNotice.ShouldNotify(u?.Version, _notifiedVersion, UiOpen))
                ShowBalloon(BalloonKind.Update, 10000, AppName,
                    $"Versão {u!.Version.ToString(3)} disponível. Clique aqui, no painel ou no menu para atualizar.", ToolTipIcon.Info);
            if (u != null) _notifiedVersion = u.Version; // vista no painel ou no menu também conta: não repete
        }
        catch (Exception ex)
        {
            Log.Failure("atualização do app", "update", "verificar atualizações: " + ex.Message);
            // falhou mas já se sabia de uma versão nova: continua oferecendo
            SetUpdateState(previous.Status == UpdateStatus.Available ? previous : UpdateState.Failed(ex.Message));
            if (retryOnFail)
            {
                // ao iniciar com o Windows a rede costuma ainda não estar pronta: tenta mais uma vez depois
                await Task.Delay(TimeSpan.FromMinutes(5));
                if (!_exiting) await CheckUpdateAsync();
            }
        }
    }

    public async void InstallUpdate()
    {
        if (_updater.Available is not { } u || _busy) return;
        _busy = true;
        SetBusy($"Baixando a versão {u.Version.ToString(3)}…");
        try
        {
            Log.Info("atualização do app", $"instalando a versão {u.Version.ToString(3)}");
            await _updater.InstallAsync(u);
            // a nova versão assume a distro (AdoptKeepAlives): não solta nada ao sair
            _handingOver = true;
            ExitThread();
        }
        catch (Exception ex)
        {
            _busy = false;
            SetBusy(null);
            Log.Error("atualização do app", $"instalar a versão {u.Version.ToString(3)}", ex);
            if (MessageBox.Show($"Não foi possível atualizar: {ex.Message}\n\nAbrir a página da versão para baixar manualmente?",
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                try { Process.Start(new ProcessStartInfo(u.PageUrl) { UseShellExecute = true }); } catch { }
        }
    }

    protected override void ExitThreadCore()
    {
        Log.Info("app", _handingOver ? "fechado para a nova versão assumir" : "fechado");
        StopRealTime();
        if (!_handingOver) WslService.Release();
        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        _updateTimer.Stop();
        _updater.Dispose();
        _shutdownWatcher?.DestroyHandle();
        _tray.Visible = false;
        _refreshTimer.Stop();
        _hoverTimer.Stop();
        _sessionWatch.Dispose();
        _tray.Dispose();
        _popup.Dispose();
        _menuOwner.Dispose();
        _widget.Dispose();
        _icons.Dispose();
        _collector.Dispose();
        base.ExitThreadCore();
    }
}
