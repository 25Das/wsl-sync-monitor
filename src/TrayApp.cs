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
    readonly TaskbarWidget _widget;
    readonly ContextMenuStrip _menu;
    readonly Form _menuOwner;
    readonly System.Windows.Forms.Timer _refreshTimer = new();
    readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 100 };

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
        _menu.Closed += (_, _) => _widget?.BringAboveTaskbar();
        _popup.VisibleChanged += (_, _) => { if (!_popup.Visible) _widget?.BringAboveTaskbar(); };

        _tray = new NotifyIcon
        {
            Icon = _icons.Render(null),
            Text = "WSL Sync Monitor",
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

        _widget = new TaskbarWidget(_settings);
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
        _ = RefreshAsync();
    }

    // ------------------------------------------------------------------ atualização

    async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            ReloadSettingsIfChanged();
            // depois de soltar a distro, não executa nada dentro dela (deixa o WSL desligar por ociosidade)
            var snap = await _collector.RefreshAsync(_settings, allowDistroCommands: _guard != GuardState.Released);
            _snap = snap;
            _tray.Icon = _icons.Render(snap);
            _tray.Text = Trim(_busyText != null ? "WSL Sync Monitor · " + _busyText : snap.ShortSummary(), 127);
            _popup.SetSnapshot(snap);
            _widget.SetSnapshot(snap);
            GuardTick();
        }
        catch (Exception ex)
        {
            _tray.Text = Trim("Erro: " + ex.Message, 127);
        }
        finally { _refreshing = false; }
    }

    static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

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
        if (accessChanged) _collector.InvalidateCredentials();
        ApplySettings();
    }

    bool _settingsErrorShown;

    void WarnInvalidSettings(string? error, bool startup) =>
        _tray.ShowBalloonTip(8000, "WSL Sync Monitor",
            (startup ? "settings.json inválido; usando os padrões (cópia em settings.json.invalido). "
                     : "settings.json inválido; as configurações atuais foram mantidas. ") + error, ToolTipIcon.Warning);

    void SaveSettings()
    {
        _settings.Save();
        _settingsMtime = SettingsMtime();
    }

    void ApplySettings()
    {
        _refreshTimer.Interval = Math.Max(2, _settings.RefreshSeconds) * 1000;
        _refreshTimer.Start();
        if (!_settings.ShowTaskbarWidget && _widget.Visible) _widget.Hide();
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
            bool inMenu = (_distroMenu is { Visible: true } dm && dm.Bounds.Contains(cur)) || (_menu.Visible && _menu.Bounds.Contains(cur));
            if (Control.MouseButtons != MouseButtons.None && !_popup.Bounds.Contains(cur) && !inMenu) _popup.Apply(PanelEvent.ClickOutside);
            return;
        }

        bool overWidget = _widget.Visible && _widget.Bounds.Contains(cur);

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
        if (_snap != null) _popup.SetSnapshot(_snap);
        _popup.ShowAt(anchor, pin);
        if (_snap == null || (DateTime.Now - _snap.Time).TotalSeconds > 2) _ = RefreshAsync();
    }

    // ------------------------------------------------------------------ ações

    string? TargetDistro => _snap?.Target?.Name ?? (string.IsNullOrWhiteSpace(_settings.Distro) ? _snap?.Distros.FirstOrDefault(d => d.IsDefault)?.Name : _settings.Distro);

    public void OpenSyncthing()
    {
        var url = _collector.GuiUrl(_settings);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Não foi possível abrir " + url + "\n" + ex.Message, "WSL Sync Monitor"); }
        _popup.HidePopup();
    }

    public void RefreshNow() => _ = RefreshAsync();

    // ------------------------------------------------------------------ gerenciamento de distros

    public void StartWsl() { if (TargetDistro is { } d) _ = StartDistroAsync(d); }
    public void StopWsl() { if (TargetDistro is { } d) _ = StopDistroAsync(d); }

    bool IsMonitored(string distro) => distro.Equals(TargetDistro, StringComparison.OrdinalIgnoreCase);
    DistroInfo? Distro(string name) => _snap?.Distros.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Executa uma operação demorada com indicação de andamento (uma por vez).</summary>
    async Task RunBusyAsync(Func<Task> op)
    {
        if (_busy) return;
        _busy = true;
        try { await op(); }
        catch (Exception ex) { MessageBox.Show(ex.Message, "WSL Sync Monitor", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        finally
        {
            _busy = false;
            SetBusy(null);
            await RefreshAsync();
        }
    }

    Task StartDistroAsync(string d) => RunBusyAsync(async () =>
    {
        if (Distro(d)?.Running == true) return;
        SetBusy($"Iniciando {d}…");
        WslService.StartDistro(d);
        var end = DateTime.Now.AddSeconds(90);
        while (Distro(d)?.Running != true && DateTime.Now < end) { await Task.Delay(1000); await RefreshAsync(); }
        if (Distro(d)?.Running != true) throw new InvalidOperationException($"A distro \"{d}\" não iniciou em 90 s.");

        // a distro monitorada subiu: espera o Syncthing (se ele inicia junto)
        if (IsMonitored(d))
        {
            SetBusy("Aguardando Syncthing…");
            end = DateTime.Now.AddSeconds(45);
            while (_snap?.St != StState.Running && DateTime.Now < end) { await Task.Delay(1500); await RefreshAsync(); }
        }
    });

    /// <summary>
    /// Para o Syncthing da distro de forma limpa: pela API se for a distro monitorada, senão por SIGTERM.
    /// Se ele não parar, lança exceção e nada é encerrado.
    /// </summary>
    async Task StopSyncthingInAsync(string d)
    {
        if (!await WslService.IsProcessRunningAsync(d, "syncthing")) return;
        SetBusy($"Parando Syncthing em {d}…");
        bool viaApi = false;
        if (IsMonitored(d) && _snap?.St == StState.Running)
        {
            try { await _collector.ShutdownSyncthingAsync(_settings); viaApi = true; } catch { }
        }
        if (!viaApi) await WslService.SignalSyncthingAsync(d);
        if (!await WslService.WaitProcessExitAsync(d, "syncthing", TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException($"O Syncthing em \"{d}\" não encerrou em 30 s (talvez um serviço o reinicie ou ele rode com outro usuário). Nada foi encerrado.");
    }

    async Task StopDistroAsync(string d)
    {
        if (Distro(d)?.Running != true || _busy) return;
        _popup.HidePopup();
        if (MessageBox.Show($"Encerrar a distro \"{d}\"?\n\nSe o Syncthing estiver rodando nela, ele será parado primeiro e os dados serão gravados em disco antes de encerrar.",
                "WSL Sync Monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (IsMonitored(d)) { CancelVerify(); _guard = GuardState.Off; }
        await RunBusyAsync(async () =>
        {
            await StopSyncthingInAsync(d);
            SetBusy($"Encerrando {d}…");
            await WslService.TerminateSafelyAsync(d);
        });
    }

    async Task ShutdownAllAsync()
    {
        var running = _snap?.Distros.Where(x => x.Running).Select(x => x.Name).ToList() ?? new();
        if (running.Count == 0 || _busy) return;
        _popup.HidePopup();
        if (MessageBox.Show($"Encerrar todo o WSL ({string.Join(", ", running)})?\n\nO Syncthing será parado primeiro onde estiver rodando, os dados serão gravados em disco e então todas as distros e a máquina virtual do WSL serão encerradas.",
                "WSL Sync Monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        CancelVerify(); _guard = GuardState.Off;
        await RunBusyAsync(async () =>
        {
            foreach (var d in running) await StopSyncthingInAsync(d);
            SetBusy("Encerrando o WSL…");
            await WslService.ShutdownAllSafelyAsync(running);
        });
    }

    Task SetDefaultDistroAsync(string d) => RunBusyAsync(async () =>
    {
        SetBusy($"Definindo {d} como padrão…");
        await WslService.SetDefaultAsync(d);
    });

    void OpenTerminal(string d)
    {
        try { WslService.OpenTerminal(d); } catch (Exception ex) { MessageBox.Show(ex.Message, "WSL Sync Monitor"); }
    }

    /// <summary>Ações de uma distro (usadas no menu principal e ao clicar numa distro no painel).</summary>
    ToolStripItem[] DistroActions(DistroInfo d)
    {
        var name = d.Name;
        return new ToolStripItem[]
        {
            new ToolStripMenuItem("Abrir terminal", null, (_, _) => OpenTerminal(name)),
            d.Running
                ? new ToolStripMenuItem("Encerrar…", null, (_, _) => _ = StopDistroAsync(name)) { Enabled = !_busy }
                : new ToolStripMenuItem("Iniciar", null, (_, _) => _ = StartDistroAsync(name)) { Enabled = !_busy },
            new ToolStripSeparator(),
            new ToolStripMenuItem("Monitorar o Syncthing desta distro", null, (_, _) => SelectDistro(name)) { Checked = IsMonitored(name) },
            new ToolStripMenuItem("Definir como padrão do WSL", null, (_, _) => _ = SetDefaultDistroAsync(name)) { Checked = d.IsDefault, Enabled = !d.IsDefault && !_busy },
        };
    }

    static readonly Dictionary<Level, Bitmap> _dots = new();
    static Bitmap DotImage(Level l)
    {
        if (_dots.TryGetValue(l, out var b)) return b;
        b = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(b))
        using (var br = new SolidBrush(Theme.For(l)))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(br, 4, 4, 8, 8);
        }
        return _dots[l] = b;
    }

    static string DistroLabel(DistroInfo d) => d.Name + (d.IsDefault ? " (padrão)" : "");

    /// <summary>Clique numa distro no painel detalhado: mostra as ações dela.</summary>
    public void ShowDistroMenu(string name, Point screen)
    {
        if (Distro(name) is not { } d) return;
        _distroMenu?.Dispose();
        _distroMenu = new ContextMenuStrip();
        _distroMenu.Items.AddRange(DistroActions(d));
        _distroMenu.Show(_popup, _popup.PointToClient(screen));
    }
    ContextMenuStrip? _distroMenu;

    void SetBusy(string? text)
    {
        _busyText = text;
        _popup.SetBusy(text);
        _widget.Busy = text != null;
        _tray.Text = Trim(text != null ? "WSL Sync Monitor · " + text : _snap?.ShortSummary() ?? "WSL Sync Monitor", 127);
    }

    // ------------------------------------------------------------------ menu

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        var open = new ToolStripMenuItem("Abrir Syncthing no navegador", null, (_, _) => OpenSyncthing()) { Font = new Font(m.Font, FontStyle.Bold) };
        var refresh = new ToolStripMenuItem("Atualizar agora", null, (_, _) => RefreshNow());
        var distrosHeader = new ToolStripMenuItem("Distros do WSL") { Enabled = false };
        var distrosEnd = new ToolStripSeparator();
        var shutdownAll = new ToolStripMenuItem("Encerrar todo o WSL…", null, (_, _) => _ = ShutdownAllAsync());
        var folders = new ToolStripMenuItem("Pastas exibidas");
        var widget = new ToolStripMenuItem("Widget na barra de tarefas", null, (_, _) => { _settings.ShowTaskbarWidget = !_settings.ShowTaskbarWidget; SaveSettings(); ApplySettings(); });
        var resetPos = new ToolStripMenuItem("Restaurar posição do widget", null, (_, _) => { _settings.WidgetOffset = 0; SaveSettings(); });
        var hover = new ToolStripMenuItem("Detalhes ao passar o mouse no widget", null, (_, _) => { _settings.HoverPopup = !_settings.HoverPopup; SaveSettings(); RefreshNow(); });
        var autostart = new ToolStripMenuItem("Iniciar com o Windows", null, (_, _) =>
        {
            try { Settings.StartWithWindows = !Settings.StartWithWindows; } catch (Exception ex) { MessageBox.Show(ex.Message); }
        });
        var techInfo = new ToolStripMenuItem("Informações técnicas…", null, (_, _) => ShowTechInfo());
        var edit = new ToolStripMenuItem("Editar configurações…", null, (_, _) =>
        {
            SaveSettings();
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true });
        });
        var protect = new ToolStripMenuItem("Segurar o WSL até sincronizar", null, (_, _) => { _settings.ProtectSync = !_settings.ProtectSync; SaveSettings(); RefreshNow(); });
        var periodic = new ToolStripMenuItem("Sincronização periódica", null, (_, _) =>
        {
            _settings.PeriodicSyncMinutes = _settings.PeriodicSyncMinutes > 0 ? 0 : 60;
            SaveSettings();
        });
        var exit = new ToolStripMenuItem("Sair", null, (_, _) => RequestExit());

        m.Items.AddRange(new ToolStripItem[]
        {
            open, refresh, new ToolStripSeparator(),
            distrosHeader, shutdownAll, distrosEnd,
            folders, new ToolStripSeparator(),
            protect, periodic, new ToolStripSeparator(),
            widget, resetPos, hover, autostart, edit, techInfo, new ToolStripSeparator(),
            exit,
        });

        m.Opening += (_, _) =>
        {
            _popup.HidePopup();
            open.Enabled = _snap?.St == StState.Running;
            widget.Checked = _settings.ShowTaskbarWidget;
            hover.Checked = _settings.HoverPopup;
            protect.Checked = _settings.ProtectSync;
            periodic.Checked = _settings.PeriodicSyncMinutes > 0;
            periodic.Enabled = _settings.ProtectSync;
            periodic.Text = _settings.PeriodicSyncMinutes > 0 ? $"Sincronização periódica (a cada {_settings.PeriodicSyncMinutes} min)" : "Sincronização periódica";
            exit.Text = _exiting ? "Sair agora (sem esperar)" : "Sair";
            try { autostart.Checked = Settings.StartWithWindows; } catch { }

            // uma entrada por distro (entre o cabeçalho e "Encerrar todo o WSL"), cada uma com suas ações
            int at = m.Items.IndexOf(distrosHeader) + 1;
            while (m.Items[at] != shutdownAll) m.Items.RemoveAt(at);
            foreach (var d in _snap?.Distros ?? new())
            {
                var item = new ToolStripMenuItem(DistroLabel(d) + (IsMonitored(d.Name) ? " · Syncthing" : ""), DotImage(d.Running ? Level.Ok : Level.Off));
                item.DropDownItems.AddRange(DistroActions(d));
                m.Items.Insert(at++, item);
            }
            shutdownAll.Enabled = !_busy && _snap?.Distros.Any(d => d.Running) == true;

            BuildFoldersMenu(folders);
        };
        return m;
    }

    void BuildFoldersMenu(ToolStripMenuItem menu)
    {
        menu.DropDownItems.Clear();
        var all = _snap?.AllFolders ?? new();
        foreach (var f in all.OrderBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase))
        {
            var id = f.Id;
            bool manual = _settings.HiddenFolders.Contains(id);
            bool byRule = !manual && _settings.IsFolderHidden(f);
            var note = byRule ? (f.SharedWith == 0 && _settings.HideUnsharedFolders ? " (não compartilhada)" : " (pausada)") : "";
            menu.DropDownItems.Add(new ToolStripMenuItem(f.Label + note, null, (_, _) =>
            {
                if (!_settings.HiddenFolders.Remove(id)) _settings.HiddenFolders.Add(id);
                SaveSettings(); RefreshNow();
            })
            { Checked = !manual && !byRule, Enabled = !byRule });
        }
        if (all.Count == 0) menu.DropDownItems.Add(new ToolStripMenuItem("Nenhuma pasta encontrada") { Enabled = false });

        menu.DropDownItems.Add(new ToolStripSeparator());
        menu.DropDownItems.Add(new ToolStripMenuItem("Ocultar pastas não compartilhadas", null, (_, _) =>
        { _settings.HideUnsharedFolders = !_settings.HideUnsharedFolders; SaveSettings(); RefreshNow(); })
        { Checked = _settings.HideUnsharedFolders });
        menu.DropDownItems.Add(new ToolStripMenuItem("Ocultar pastas pausadas", null, (_, _) =>
        { _settings.HidePausedFolders = !_settings.HidePausedFolders; SaveSettings(); RefreshNow(); })
        { Checked = _settings.HidePausedFolders });
        if (_settings.HiddenFolders.Count > 0)
            menu.DropDownItems.Add(new ToolStripMenuItem("Mostrar todas as ocultadas manualmente", null, (_, _) =>
            { _settings.HiddenFolders.Clear(); SaveSettings(); RefreshNow(); }));
    }

    // ------------------------------------------------------------------ informações técnicas

    TechInfoForm? _techInfo;
    string? _wslVersion;

    async void ShowTechInfo()
    {
        if (_techInfo is { IsDisposed: false }) { _techInfo.Activate(); await _techInfo.LoadAsync(); return; }
        _techInfo = new TechInfoForm(BuildTechInfoAsync);
        _techInfo.Show();
        await _techInfo.LoadAsync();
    }

    async Task<string> BuildTechInfoAsync()
    {
        _wslVersion ??= await WslService.GetVersionAsync();
        int? sessions = null;
        if (_snap?.Target is { Running: true } t)
            try { sessions = WslService.CountExternalSessions(t.Name, DefaultDistro); } catch { }
        return new Diagnostics
        {
            AppVersion = typeof(TrayApp).Assembly.GetName().Version?.ToString(3),
            WslVersion = _wslVersion,
            Distro = _snap?.Target is { } d ? $"{d.Name} ({(d.Running ? "em execução" : "parada")})" : null,
            DefaultDistro = DefaultDistro,
            Guard = $"{_guard}" + (_guardText != null ? $" · {_guardText}" : "") + (_settings.ProtectSync ? "" : " (desativada)"),
            ExternalSessions = sessions,
            GuiUrl = _collector.GuiUrl(_settings),
            ApiKeySource = _collector.ApiKeySource,
            ConfigFiles = _collector.ConfigFiles,
            ConfigFileUsed = _collector.ConfigFileUsed,
            LastScriptExitCode = _collector.LastScriptExitCode,
            LastScriptError = _collector.LastScriptError,
            LastApiError = _collector.LastApiError,
            SettingsPath = Settings.FilePath,
            Updated = _snap?.Time,
        }.ToText();
    }

    void SelectDistro(string name)
    {
        ResetGuardForDistroChange();
        _settings.Distro = name;
        SaveSettings();
        _collector.InvalidateCredentials();
        RefreshNow();
    }

    protected override void ExitThreadCore()
    {
        WslService.Release();
        _shutdownWatcher?.DestroyHandle();
        _tray.Visible = false;
        _refreshTimer.Stop();
        _hoverTimer.Stop();
        _tray.Dispose();
        _popup.Dispose();
        _menuOwner.Dispose();
        _widget.Dispose();
        _icons.Dispose();
        _collector.Dispose();
        base.ExitThreadCore();
    }
}
