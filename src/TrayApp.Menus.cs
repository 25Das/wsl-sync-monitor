using System.Diagnostics;

namespace WslSyncMonitor;

/// <summary>
/// Menus. Montados na hora em que abrem (refletem o estado atual). As mesmas funções servem ao menu principal
/// e ao clique numa linha do painel (<see cref="ShowItemMenu"/>).
/// </summary>
public sealed partial class TrayApp
{
    // ------------------------------------------------------------------ peças

    internal static ToolStripMenuItem Item(string text, Action onClick, bool enabled = true, bool check = false, bool bold = false, Image? image = null)
    {
        var item = new ToolStripMenuItem(text, image, (_, _) => onClick()) { Enabled = enabled, Checked = check };
        if (bold) item.Font = new Font(item.Font, FontStyle.Bold);
        return item;
    }

    // Atenção: "() => _ = onClick()" seria uma lambda que devolve Task e o C# escolheria esta mesma sobrecarga,
    // chamando-a sem fim (estouro de pilha na 1.8.0). O corpo entre chaves não devolve nada: só serve à versão Action.
    internal static ToolStripMenuItem Item(string text, Func<Task> onClick, bool enabled = true, bool check = false) =>
        Item(text, () => { _ = onClick(); }, enabled, check);

    /// <summary>Linha só de leitura (informação).</summary>
    static ToolStripMenuItem Info(string text) => new(text) { Enabled = false };

    static ToolStripMenuItem Sub(string text, IEnumerable<ToolStripItem> items, Image? image = null, bool enabled = true)
    {
        var sub = new ToolStripMenuItem(text, image) { Enabled = enabled };
        sub.DropDownItems.AddRange(items.ToArray());
        return sub;
    }

    static ToolStripSeparator Sep() => new();

    static readonly Dictionary<Level, Bitmap> _dots = new();

    /// <summary>Bolinha colorida do estado, como no painel.</summary>
    static Bitmap Dot(Level level)
    {
        if (_dots.TryGetValue(level, out var b)) return b;
        b = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(b))
        using (var brush = new SolidBrush(Theme.For(level)))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.FillEllipse(brush, 4, 4, 8, 8);
        }
        return _dots[level] = b;
    }

    // ------------------------------------------------------------------ menu principal

    ContextMenuStrip BuildMenu()
    {
        var m = new ContextMenuStrip();
        m.Items.Add(Info("…")); // sem itens o menu não abre; o conteúdo real é montado em Opening
        m.Opening += (_, _) =>
        {
            _popup.HidePopup();
            foreach (var old in m.Items.Cast<ToolStripItem>().ToList()) old.Dispose(); // Dispose também remove do menu
            m.Items.AddRange(MainMenu().ToArray());
        };
        return m;
    }

    IEnumerable<ToolStripItem> MainMenu()
    {
        yield return Item("Abrir interface do Syncthing", OpenSyncthing, enabled: _snap?.St == StState.Running, bold: true);
        if (_updater.Available is { } u)
            yield return Item($"Atualizar para a versão {u.Version.ToString(3)}", InstallUpdate, enabled: !_busy, bold: true);
        yield return Sep();
        yield return Sub("WSL", WslMenu());
        yield return Sub("Syncthing", SyncthingMenu());
        yield return Sub("Pastas", FoldersMenu());
        yield return Sub("Dispositivos", DevicesMenu());
        yield return Sep();
        yield return Sub("Diagnóstico", DiagnosticsMenu());
        yield return Sub("Preferências", PreferencesMenu());
        yield return Sep();
        yield return Item(_exiting ? "Sair agora (sem esperar)" : "Sair", RequestExit);
    }

    // ------------------------------------------------------------------ WSL

    IEnumerable<ToolStripItem> WslMenu()
    {
        var distros = _snap?.Distros ?? [];
        if (distros.Count == 0) yield return Info("Nenhuma distro encontrada");
        foreach (var d in distros)
            yield return Sub(DistroLabel(d), DistroMenu(d), Dot(d.Running ? Level.Ok : Level.Off));
        yield return Sep();
        yield return Item("Encerrar todo o WSL…", ShutdownAllAsync, enabled: !_busy && RunningDistros.Count > 0);
        yield return Item("Atualizar o WSL…", UpdateWslAsync, enabled: !_busy);
        yield return Item("Editar .wslconfig…", EditWslConfig);
    }

    string DistroLabel(DistroInfo d) => d.Name + (d.IsDefault ? " (padrão)" : "") + (IsMonitored(d.Name) ? " · Syncthing" : "");

    IEnumerable<ToolStripItem> DistroMenu(DistroInfo d)
    {
        var name = d.Name;
        yield return Item("Abrir terminal", () => OpenTerminal(name));
        yield return Item("Abrir no Explorer", () => OpenInExplorerAsync(name, "~"));
        yield return Sep();
        if (d.Running)
        {
            yield return Item("Reiniciar…", () => RestartDistroAsync(name), enabled: !_busy);
            yield return Item("Encerrar…", () => StopDistroAsync(name), enabled: !_busy);
        }
        else yield return Item("Iniciar", () => StartDistroAsync(name), enabled: !_busy);
        yield return Sub("Manter ligada", KeepAliveMenu(name));
        yield return Sep();
        yield return Item("Usar para o Syncthing", () => UseForSyncthing(name), check: IsMonitored(name));
        yield return Item("Definir como padrão do WSL", () => SetDefaultDistroAsync(name), enabled: !d.IsDefault && !_busy, check: d.IsDefault);
        yield return Sep();
        yield return Item("Exportar backup…", () => ExportDistroAsync(name), enabled: !_busy);
    }

    IEnumerable<ToolStripItem> KeepAliveMenu(string distro)
    {
        var current = _keepAliveRequest is { } k && k.Distro.Equals(distro, StringComparison.OrdinalIgnoreCase) && k.IsActive(DateTime.Now) ? k : null;
        yield return Item("Até eu desligar", () => SetKeepAlive(distro, null), check: current is { Persist: true });
        yield return Item("Por 1 hora", () => SetKeepAlive(distro, TimeSpan.FromHours(1)));
        yield return Item("Por 4 horas", () => SetKeepAlive(distro, TimeSpan.FromHours(4)));
        if (current != null)
        {
            yield return Sep();
            yield return Info($"Mantida ligada {current.Describe()}");
            yield return Item("Parar de manter ligada", () => SetKeepAlive(null, null));
        }
    }

    // ------------------------------------------------------------------ Syncthing

    IEnumerable<ToolStripItem> SyncthingMenu()
    {
        bool on = _snap?.St == StState.Running;
        yield return AllDevicesPaused
            ? Item("Retomar tudo", () => SetAllPausedAsync(false), enabled: on)
            : Item("Pausar tudo", () => SetAllPausedAsync(true), enabled: on);
        yield return Item("Verificar todas as pastas agora", ScanAllNowAsync, enabled: on);
        yield return Item("Verificar pastas e dispositivos…", ShowHealthCheck, enabled: on);
        yield return Item("Reiniciar o Syncthing…", RestartSyncthingAsync, enabled: on);
        yield return Item("Copiar o ID deste dispositivo", CopyMyId, enabled: _snap?.MyId != null);
        yield return Item("Ver atividade recente…", () => ShowActivity());
        yield return Item("Ver erros recentes…", ShowSyncthingErrors, enabled: on);
        yield return Sep();
        yield return Item("Esperar a sincronização antes de desligar", () =>
        {
            _settings.ProtectSync = !_settings.ProtectSync;
            SaveSettings(); RefreshNow();
        }, check: _settings.ProtectSync);
        int minutes = _settings.PeriodicSyncMinutes > 0 ? _settings.PeriodicSyncMinutes : 60;
        yield return Item($"Ligar o WSL a cada {minutes} min para sincronizar", () =>
        {
            _settings.PeriodicSyncMinutes = _settings.PeriodicSyncMinutes > 0 ? 0 : minutes;
            SaveSettings();
        }, enabled: _settings.ProtectSync, check: _settings.PeriodicSyncMinutes > 0);
    }

    // ------------------------------------------------------------------ pastas

    IEnumerable<ToolStripItem> FoldersMenu()
    {
        var all = Display?.AllFolders ?? []; // com o que os eventos trouxeram (pausada, erros) desde a última leitura
        if (all.Count == 0) yield return Info(_snap?.St == StState.Running ? "Nenhuma pasta" : "O Syncthing não está em execução");
        foreach (var f in all.OrderBy(f => f.Label, StringComparer.CurrentCultureIgnoreCase))
            yield return Sub(f.Label, FolderMenu(f), Dot(f.Status.Level));
        yield return Sep();
        yield return Item("Ocultar pastas não compartilhadas", () =>
        {
            _settings.HideUnsharedFolders = !_settings.HideUnsharedFolders;
            SaveSettings(); RefreshNow();
        }, check: _settings.HideUnsharedFolders);
        yield return Item("Ocultar pastas pausadas", () =>
        {
            _settings.HidePausedFolders = !_settings.HidePausedFolders;
            SaveSettings(); RefreshNow();
        }, check: _settings.HidePausedFolders);
        if (_settings.HiddenFolders.Count > 0)
            yield return Item("Mostrar todas as ocultadas", () => { _settings.HiddenFolders.Clear(); SaveSettings(); RefreshNow(); });
    }

    IEnumerable<ToolStripItem> FolderMenu(FolderInfo f)
    {
        bool on = _snap?.St == StState.Running;
        yield return Info($"{f.Status.Text} · {f.Path}");
        yield return Sep();
        yield return Item("Abrir no Explorer", () => OpenFolderInExplorerAsync(f));
        yield return Item("Abrir terminal aqui", () => OpenTerminalInFolderAsync(f));
        yield return Sep();
        yield return Item("Verificar agora", () => ScanFolderAsync(f), enabled: on && !f.Paused);
        yield return f.Paused
            ? Item("Retomar", () => SetFolderPausedAsync(f, false), enabled: on)
            : Item("Pausar", () => SetFolderPausedAsync(f, true), enabled: on);
        yield return Item("Atividade recente…", () => ShowActivity(f));
        yield return Item("Arquivos em conflito…", () => ShowConflicts(f));
        yield return Item("Versões antigas", () => OpenFolderVersionsAsync(f));
        yield return Sep();
        yield return Item("Copiar caminho", () => CopyToClipboard(f.Path));
        bool hiddenByRule = !_settings.HiddenFolders.Contains(f.Id) && _settings.IsFolderHidden(f);
        yield return Item(hiddenByRule ? "Mostrar no painel (oculta pelas regras de Pastas)" : "Mostrar no painel",
            () => ToggleFolderInPanel(f), enabled: !hiddenByRule, check: !_settings.IsFolderHidden(f));
    }

    // ------------------------------------------------------------------ dispositivos

    IEnumerable<ToolStripItem> DevicesMenu()
    {
        var all = _snap?.AllDevices ?? [];
        if (all.Count == 0) yield return Info(_snap?.St == StState.Running ? "Nenhum dispositivo remoto" : "O Syncthing não está em execução");
        foreach (var d in all.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            yield return Sub(d.Name, DeviceMenu(d), Dot(d.Status.Level));
    }

    IEnumerable<ToolStripItem> DeviceMenu(DeviceInfo dev)
    {
        bool on = _snap?.St == StState.Running;
        yield return Info(dev.Status.Text);
        yield return Sep();
        yield return dev.Paused
            ? Item("Retomar", () => SetDevicePausedAsync(dev, false), enabled: on)
            : Item("Pausar", () => SetDevicePausedAsync(dev, true), enabled: on);
        yield return Item("Copiar ID", () => CopyToClipboard(dev.Id));
        yield return Item("Pastas compartilhadas…", () => ShowSharedFolders(dev));
        yield return Sep();
        yield return Item("Mostrar no painel", () => ToggleDeviceInPanel(dev), check: !_settings.IsDeviceHidden(dev));
    }

    // ------------------------------------------------------------------ preferências

    IEnumerable<ToolStripItem> PreferencesMenu()
    {
        bool autostart = false;
        try { autostart = Settings.StartWithWindows; } catch { }
        yield return Item("Mostrar widget na barra de tarefas", () =>
        {
            _settings.ShowTaskbarWidget = !_settings.ShowTaskbarWidget;
            SaveSettings(); ApplySettings();
        }, check: _settings.ShowTaskbarWidget);
        yield return Item("Restaurar posição do widget", () => { _settings.WidgetOffset = 0; SaveSettings(); });
        yield return Item("Abrir o painel ao passar o mouse no widget", () =>
        {
            _settings.HoverPopup = !_settings.HoverPopup;
            SaveSettings();
        }, check: _settings.HoverPopup);
        yield return Item("Iniciar com o Windows", () => Try(() => Settings.StartWithWindows = !Settings.StartWithWindows), check: autostart);
        yield return Sep();
        yield return UpdateMenuItem();
        yield return IsInstalled ? Item("Desinstalar…", Uninstall) : Item("Instalar…", InstallFromMenu);
        yield return Item("Configurações avançadas (settings.json)…", () =>
        {
            SaveSettings();
            Try(() => Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Settings.FilePath}\"") { UseShellExecute = true }));
        });
    }

    // ------------------------------------------------------------------ atualização pelo menu

    bool _keepMenuOpen;
    Action? _renderUpdateMenuItem;

    /// <summary>Cancela o fechamento do menu pelo clique num item que pediu para ele continuar aberto.</summary>
    void KeepMenuOpenIfAsked(object? sender, ToolStripDropDownClosingEventArgs e)
    {
        if (_keepMenuOpen && e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
    }

    /// <summary>
    /// Preferências → Verificar atualizações: busca sem fechar o menu; o item mostra "Verificando…" e, achando
    /// versão nova, vira "Atualizar para a versão X" ali mesmo (quem buscou pelo menu atualiza pelo menu).
    /// </summary>
    ToolStripMenuItem UpdateMenuItem()
    {
        var item = new ToolStripMenuItem();
        var regular = item.Font;
        var bold = new Font(regular, FontStyle.Bold);
        item.Disposed += (_, _) => bold.Dispose();
        void Render()
        {
            if (item.IsDisposed) return;
            bool available = _updateState.Status == UpdateStatus.Available;
            item.Text = UpdateNotice.MenuLabel(_updateState);
            item.Font = available ? bold : regular;
            item.Enabled = _updateState.Status != UpdateStatus.Checking && !(available && _busy);
        }
        item.Click += (_, _) =>
        {
            if (_updateState.Status == UpdateStatus.Available) { InstallUpdate(); return; } // o menu fecha e o app reinicia
            CheckForUpdate();
        };
        // O menu decide fechar (ItemClicked) antes do Click do item: o pedido de continuar aberto é feito ali.
        // Vale para o submenu (Preferências) e para o menu principal, que também fechariam com o clique.
        void OnItemClicked(object? sender, ToolStripItemClickedEventArgs e)
        {
            if (e.ClickedItem != item || _updateState.Status is UpdateStatus.Available or UpdateStatus.Checking) return;
            _keepMenuOpen = true;
            SynchronizationContext.Current?.Post(_ => _keepMenuOpen = false, null); // vale só para este clique
        }
        item.OwnerChanged += (_, _) =>
        {
            if (item.Owner is not ToolStripDropDown dd) return;
            dd.ItemClicked -= OnItemClicked; dd.ItemClicked += OnItemClicked;
            dd.Closing -= KeepMenuOpenIfAsked; dd.Closing += KeepMenuOpenIfAsked;
        };
        _renderUpdateMenuItem = Render;
        Render();
        return item;
    }

    // ------------------------------------------------------------------ menu de uma linha do painel

    ContextMenuStrip? _itemMenu;

    /// <summary>Clique numa linha do painel: mostra as ações daquele item.</summary>
    public void ShowItemMenu(PanelItem item, Point screen)
    {
        IEnumerable<ToolStripItem>? items = item.Kind switch
        {
            PanelItemKind.Distro when Distro(item.Id) is { } d => DistroMenu(d),
            PanelItemKind.Syncthing => SyncthingMenu(),
            PanelItemKind.Folder when Folder(item.Id) is { } f => FolderMenu(f),
            PanelItemKind.Device when Device(item.Id) is { } dev => DeviceMenu(dev),
            _ => null,
        };
        if (items == null) return;
        _itemMenu?.Dispose();
        _itemMenu = new ContextMenuStrip();
        _itemMenu.Opened += (_, _) => _widget.MenuOpen = true;
        _itemMenu.Closed += (_, _) => _widget.MenuOpen = false;
        _itemMenu.Items.AddRange(items.ToArray());
        _itemMenu.Show(_popup, _popup.PointToClient(screen));
    }
}
