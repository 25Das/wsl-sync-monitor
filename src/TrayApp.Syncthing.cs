using System.Diagnostics;
using System.Text;

namespace WslSyncMonitor;

/// <summary>Ações do Syncthing, das pastas e dos dispositivos (menus e linhas do painel).</summary>
public sealed partial class TrayApp
{
    // com o que os eventos trouxeram desde a última leitura, como o painel e o widget
    FolderInfo? Folder(string id) => Display?.AllFolders.FirstOrDefault(f => f.Id == id);
    DeviceInfo? Device(string id) => Display?.AllDevices.FirstOrDefault(d => d.Id == id);

    /// <summary>
    /// Executa um comando na API do Syncthing; erros viram aviso. Os eventos mostram o efeito na hora; a leitura
    /// completa vem em instantes, junto com a que a mudança de configuração (ConfigSaved) pede, numa só.
    /// </summary>
    async Task SyncthingCommandAsync(Func<Task> command)
    {
        try { await command(); }
        catch (Exception ex) { ShowError("O Syncthing recusou o comando: " + ex.Message, ex); }
        RequestRefreshSoon();
    }

    void ShowText(string title, Func<Task<string>> build)
    {
        var window = new TextWindow(title, build);
        window.Show();
        _ = window.LoadAsync();
    }

    static void CopyToClipboard(string text) => Try(() => Clipboard.SetText(text));

    // ------------------------------------------------------------------ Syncthing

    public void OpenSyncthing()
    {
        var url = _collector.GuiUrl(_settings);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError($"Não foi possível abrir {url}\n{ex.Message}", ex); }
        _popup.HidePopup();
    }

    /// <summary>Todos os dispositivos estão pausados? (decide entre "Pausar tudo" e "Retomar tudo")</summary>
    bool AllDevicesPaused => _snap?.AllDevices is { Count: > 0 } all && all.All(d => d.Paused);

    /// <summary>
    /// Pausa ou retoma todos os dispositivos. Com algum ignorado, um a um só os monitorados (o comando do Syncthing
    /// pegaria também os ignorados, e nada se faz com eles sem antes deixar de ignorar).
    /// </summary>
    Task SetAllPausedAsync(bool paused) => SyncthingCommandAsync(async () =>
    {
        if (_settings.IgnoredDevices.Count == 0) { await _collector.SetAllPausedAsync(_settings, paused); return; }
        foreach (var d in _snap?.AllDevices ?? []) await _collector.SetDevicePausedAsync(_settings, d.Id, paused);
    });

    Task ScanAllNowAsync() => SyncthingCommandAsync(() => _collector.ScanAllAsync(_settings));

    async Task RestartSyncthingAsync()
    {
        if (!Confirm("Reiniciar o Syncthing?\n\nAs transferências em andamento são interrompidas e retomadas em seguida.")) return;
        await SyncthingCommandAsync(() => _collector.RestartSyncthingAsync(_settings));
    }

    /// <summary>Arquivos recebidos, alterados aqui ou que falharam desde que o app abriu (de uma pasta ou de todas).</summary>
    void ShowActivity(FolderInfo? f = null) => ShowText(f == null ? "atividade recente" : $"atividade recente em {f.Label}", () =>
    {
        var ignored = _activity.Ignored.Folders; // o que entrou antes de a pasta ser ignorada
        var history = _activity.History(f?.Id).Where(e => !ignored.Contains(e.Folder)).ToList();
        var labels = (_snap?.AllFolders ?? []).ToDictionary(x => x.Id, x => x.Label);
        return Task.FromResult(history.Count == 0
            ? "Nenhum arquivo sincronizado desde que o app abriu (ou desde que o Syncthing ligou)."
            : ActivityEntry.Format(history, labels, DateTime.Now));
    });

    void CopyMyId() { if (_snap?.MyId is { } id) CopyToClipboard(id); }

    void ShowSyncthingErrors() => ShowText("erros recentes do Syncthing", async () =>
    {
        var errors = await _collector.GetErrorsAsync(_settings);
        if (errors.Count == 0) return "Nenhum erro recente.";
        var sb = new StringBuilder();
        foreach (var (when, message) in errors.OrderByDescending(e => e.When))
            sb.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{when.LocalDateTime:dd/MM HH:mm:ss}  {message}");
        return sb.ToString();
    });

    // ------------------------------------------------------------------ pastas

    string? SyncthingDistro => _snap?.Target?.Name;

    Task OpenFolderInExplorerAsync(FolderInfo f, string subPath = "") =>
        SyncthingDistro is { } d ? OpenInExplorerAsync(d, f.Path.TrimEnd('/') + subPath) : Task.CompletedTask;

    /// <summary>Versões antigas guardadas pelo controle de versões do Syncthing (pasta .stversions).</summary>
    async Task OpenFolderVersionsAsync(FolderInfo f)
    {
        if (SyncthingDistro is not { } d) return;
        try
        {
            var unc = WslPaths.ToUnc(d, f.Path.TrimEnd('/') + "/.stversions", await WslService.GetHomeAsync(d));
            if (Directory.Exists(unc)) WslService.OpenInExplorer(unc);
            else MessageBox.Show($"{f.Label} ainda não tem versões antigas guardadas (o controle de versões está desligado ou nada foi substituído).", AppName);
        }
        catch (Exception ex) { ShowError(ex.Message, ex); }
    }

    /// <summary>Terminal na pasta (o wsl.exe --cd só entende "~" sozinho, então o caminho vira absoluto).</summary>
    async Task OpenTerminalInFolderAsync(FolderInfo f)
    {
        if (SyncthingDistro is not { } d) return;
        try { OpenTerminal(d, WslPaths.ToAbsolute(f.Path, await WslService.GetHomeAsync(d))); }
        catch (Exception ex) { ShowError(ex.Message, ex); }
    }

    Task ScanFolderAsync(FolderInfo f) => SyncthingCommandAsync(() => _collector.ScanFolderAsync(_settings, f.Id));

    Task SetFolderPausedAsync(FolderInfo f, bool paused) =>
        SyncthingCommandAsync(() => _collector.SetFolderPausedAsync(_settings, f.Id, paused));

    void ShowConflicts(FolderInfo f) => ShowText($"arquivos em conflito em {f.Label}", async () =>
    {
        if (SyncthingDistro is not { } d) return "A distro do Syncthing não está ligada.";
        var r = await WslService.ShAsync(d, SyncthingScripts.ListConflicts(f.Path), 30000);
        var files = r.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return files.Length == 0
            ? $"Nenhum arquivo em conflito em {f.Path}."
            : $"{files.Length} arquivo(s) em conflito (o Syncthing guarda as duas versões; apague a que não quiser):\n\n" + string.Join("\n", files);
    });

    void IgnoreFolder(FolderInfo f)
    {
        if (!Confirm($"Ignorar a pasta {f.Label}?\n\nO app deixa de monitorá-la: ela sai do painel, dos contadores e dos avisos, " +
                     "e a proteção da sincronização não espera por ela ao liberar o WSL, sair ou desligar o Windows.\n\n" +
                     "O Syncthing continua sincronizando normalmente. Para voltar: Pastas → Ignoradas → Deixar de ignorar.")) return;
        _settings.HiddenFolders.Remove(f.Id); // ignorar substitui ocultar: ao deixar de ignorar, volta ao normal
        if (!_settings.IgnoredFolders.Contains(f.Id)) _settings.IgnoredFolders.Add(f.Id);
        SaveSettings(); RefreshNow();
    }

    void UnignoreFolder(string id) { _settings.IgnoredFolders.Remove(id); SaveSettings(); RefreshNow(); }

    void ToggleFolderInPanel(FolderInfo f)
    {
        if (!_settings.HiddenFolders.Remove(f.Id)) _settings.HiddenFolders.Add(f.Id);
        SaveSettings(); RefreshNow();
    }

    // ------------------------------------------------------------------ dispositivos

    Task SetDevicePausedAsync(DeviceInfo dev, bool paused) =>
        SyncthingCommandAsync(() => _collector.SetDevicePausedAsync(_settings, dev.Id, paused));

    void ShowSharedFolders(DeviceInfo dev) => ShowText($"pastas compartilhadas com {dev.Name}", () =>
    {
        var folders = _snap?.AllFolders.Where(f => f.DeviceIds.Contains(dev.Id)).ToList() ?? [];
        return Task.FromResult(folders.Count == 0
            ? $"Nenhuma pasta compartilhada com {dev.Name}."
            : string.Join("\n", folders.Select(f => $"{f.Label}  ({f.Path}) · {f.Status.Text}")));
    });

    void IgnoreDevice(DeviceInfo dev)
    {
        if (!Confirm($"Ignorar o dispositivo {dev.Name}?\n\nO app deixa de monitorá-lo: ele sai do painel, dos contadores e dos avisos, " +
                     "e a proteção da sincronização não espera o envio para ele ao liberar o WSL, sair ou desligar o Windows.\n\n" +
                     "O Syncthing continua sincronizando normalmente. Para voltar: Dispositivos → Ignorados → Deixar de ignorar.")) return;
        _settings.HiddenDevices.Remove(dev.Id);
        if (!_settings.IgnoredDevices.Contains(dev.Id)) _settings.IgnoredDevices.Add(dev.Id);
        SaveSettings(); RefreshNow();
    }

    void UnignoreDevice(string id) { _settings.IgnoredDevices.Remove(id); SaveSettings(); RefreshNow(); }

    void ToggleDeviceInPanel(DeviceInfo dev)
    {
        if (!_settings.HiddenDevices.Remove(dev.Id)) _settings.HiddenDevices.Add(dev.Id);
        SaveSettings(); RefreshNow();
    }
}
