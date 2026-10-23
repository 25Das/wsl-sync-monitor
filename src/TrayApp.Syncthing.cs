using System.Diagnostics;
using System.Text;

namespace WslSyncMonitor;

/// <summary>Ações do Syncthing, das pastas e dos dispositivos (menus e linhas do painel).</summary>
public sealed partial class TrayApp
{
    FolderInfo? Folder(string id) => _snap?.AllFolders.FirstOrDefault(f => f.Id == id);
    DeviceInfo? Device(string id) => _snap?.AllDevices.FirstOrDefault(d => d.Id == id);

    /// <summary>Executa um comando na API do Syncthing e atualiza o painel; erros viram aviso.</summary>
    async Task SyncthingCommandAsync(Func<Task> command)
    {
        try { await command(); }
        catch (Exception ex) { ShowError("O Syncthing recusou o comando: " + ex.Message, ex); }
        await RefreshAsync();
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

    Task SetAllPausedAsync(bool paused) => SyncthingCommandAsync(() => _collector.SetAllPausedAsync(_settings, paused));

    Task ScanAllNowAsync() => SyncthingCommandAsync(() => _collector.ScanAllAsync(_settings));

    async Task RestartSyncthingAsync()
    {
        if (!Confirm("Reiniciar o Syncthing?\n\nAs transferências em andamento são interrompidas e retomadas em seguida.")) return;
        await SyncthingCommandAsync(() => _collector.RestartSyncthingAsync(_settings));
    }

    /// <summary>Arquivos recebidos, alterados aqui ou que falharam desde que o app abriu (de uma pasta ou de todas).</summary>
    void ShowActivity(FolderInfo? f = null) => ShowText(f == null ? "atividade recente" : $"atividade recente em {f.Label}", () =>
    {
        var history = _activity.History(f?.Id);
        var labels = (_snap?.AllFolders ?? []).ToDictionary(x => x.Id, x => x.Label);
        return Task.FromResult(history.Count == 0
            ? "Nenhum arquivo sincronizado desde que o app abriu (ou desde que o Syncthing ligou)."
            : ActivityEntry.Format(history, labels));
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

    void ToggleDeviceInPanel(DeviceInfo dev)
    {
        if (!_settings.HiddenDevices.Remove(dev.Id)) _settings.HiddenDevices.Add(dev.Id);
        SaveSettings(); RefreshNow();
    }
}
