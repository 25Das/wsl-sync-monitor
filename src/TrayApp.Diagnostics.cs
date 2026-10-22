namespace WslSyncMonitor;

/// <summary>Diagnóstico: informações rápidas no menu e a ficha técnica completa.</summary>
public sealed partial class TrayApp
{
    string? _wslVersion;
    long? _diskFree;
    DateTime _diskFreeAt;

    string ProtectionSummary => _guardText ?? (_settings.ProtectSync ? "ativada" : "desativada");

    /// <summary>Espaço livre na distro, relido no máximo a cada 10 min e só com ela em uso (executa "df" nela).</summary>
    async Task RefreshDiskFreeAsync()
    {
        if (_snap?.Target is not { Running: true } t || _guard == GuardState.Released) return;
        if (DateTime.Now - _diskFreeAt < TimeSpan.FromMinutes(10)) return;
        _diskFreeAt = DateTime.Now;
        try { _diskFree = (await WslService.GetDiskSpaceAsync(t.Name))?.Free; } catch { }
    }

    /// <summary>Espaço livre real: o menor entre o livre na distro e o livre na unidade do Windows do .vhdx.</summary>
    DiskSpace? DiskOf(DistroInfo? t)
    {
        if (t == null) return null;
        var host = WslService.GetHostFreeSpace(t.Name);
        return DiskSpace.Effective(_diskFree, host?.Free, host?.Drive);
    }

    IEnumerable<ToolStripItem> DiagnosticsMenu()
    {
        var t = _snap?.Target;
        yield return Info($"Versão {Updater.Current.ToString(3)}" + (_wslVersion != null ? $" · {_wslVersion}" : ""));
        yield return Info($"Proteção: {ProtectionSummary}");
        yield return Info($"Chave da API: {_collector.ApiKeySource ?? "—"}");
        var memory = WslService.GetVmMemory();
        var vhd = t != null ? WslService.GetVhdSize(t.Name) : null;
        yield return Info($"Memória do WSL: {(memory is { } m ? Fmt.Bytes(m) : "—")} · Disco da distro: {(vhd is { } v ? Fmt.Bytes(v) : "—")}");
        if (DiskOf(t) is { Free: not null } disk) yield return Info($"Espaço: {disk.Describe()}");
        var error = _snap?.StError ?? _snap?.WslError ?? _collector.LastApiError;
        yield return Info("Último erro: " + (error == null ? "nenhum" : error.Length > 80 ? error[..80] + "…" : error));
        yield return Sep();
        yield return Item("Ficha técnica completa…", ShowTechInfo);
        yield return Item("Copiar ficha técnica", async () => CopyToClipboard(await BuildTechInfoAsync()));
    }

    TextWindow? _techInfo;

    public void ShowTechInfo()
    {
        if (_techInfo is { IsDisposed: false }) { _techInfo.Activate(); _ = _techInfo.LoadAsync(); return; }
        _techInfo = new TextWindow("ficha técnica", BuildTechInfoAsync);
        _techInfo.Show();
        _ = _techInfo.LoadAsync();
    }

    async Task<string> BuildTechInfoAsync()
    {
        _wslVersion ??= await WslService.GetVersionAsync();
        await RefreshDiskFreeAsync();
        var t = _snap?.Target;
        int? sessions = null;
        if (t is { Running: true })
            try { sessions = WslService.CountExternalSessions(t.Name, DefaultDistro); } catch { }
        return new Diagnostics
        {
            AppVersion = Updater.Current.ToString(3) + (_updater.Available is { } u ? $" (versão {u.Version.ToString(3)} disponível)" : ""),
            WslVersion = _wslVersion,
            Distro = t != null ? $"{t.Name} ({(t.Running ? "em execução" : "parada")})" : null,
            DefaultDistro = DefaultDistro,
            Guard = $"{_guard} · {ProtectionSummary}",
            ExternalSessions = sessions,
            VmMemory = WslService.GetVmMemory(),
            VhdSize = t != null ? WslService.GetVhdSize(t.Name) : null,
            Disk = DiskOf(t),
            GuiUrl = _collector.GuiUrl(_settings),
            ApiKeySource = _collector.ApiKeySource,
            ConfigFiles = _collector.ConfigFiles,
            ConfigFileUsed = _collector.ConfigFileUsed,
            LastScriptExitCode = _collector.LastScriptExitCode,
            LastScriptError = _collector.LastScriptError,
            LastApiError = _collector.LastApiError,
            SettingsPath = Settings.FilePath,
            ErrorLogTail = ErrorLog.Tail(),
            Updated = _snap?.Time,
        }.ToText();
    }
}
