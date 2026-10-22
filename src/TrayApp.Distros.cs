using System.Diagnostics;

namespace WslSyncMonitor;

/// <summary>Operações nas distros e no WSL (iniciar, encerrar, reiniciar, exportar, atualizar…).</summary>
public sealed partial class TrayApp
{
    const string AppName = "WSL Sync Monitor";

    string? TargetDistro => _snap?.Target?.Name ?? (string.IsNullOrWhiteSpace(_settings.Distro) ? DefaultDistro : _settings.Distro);
    bool IsMonitored(string distro) => distro.Equals(TargetDistro, StringComparison.OrdinalIgnoreCase);
    DistroInfo? Distro(string name) => _snap?.Distros.FirstOrDefault(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    List<string> RunningDistros => _snap?.Distros.Where(d => d.Running).Select(d => d.Name).ToList() ?? [];

    static bool Confirm(string text) =>
        MessageBox.Show(text, AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    static void ShowError(string text) => MessageBox.Show(text, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);

    /// <summary>Executa uma operação demorada com indicação de andamento (uma por vez); erros viram aviso.</summary>
    async Task RunBusyAsync(Func<Task> op)
    {
        if (_busy) return;
        _busy = true;
        try { await op(); }
        catch (Exception ex) { ShowError(ex.Message); }
        finally
        {
            _busy = false;
            SetBusy(null);
            await RefreshAsync();
        }
    }

    /// <summary>Executa uma ação rápida; erros viram aviso.</summary>
    static void Try(Action action)
    {
        try { action(); } catch (Exception ex) { ShowError(ex.Message); }
    }

    // ------------------------------------------------------------------ iniciar / encerrar / reiniciar

    Task StartDistroAsync(string d) => RunBusyAsync(() => StartCoreAsync(d));

    async Task StartCoreAsync(string d)
    {
        if (Distro(d)?.Running == true) return;
        SetBusy($"Iniciando {d}…");
        WslService.StartDistro(d);
        var end = DateTime.Now.AddSeconds(90);
        while (Distro(d)?.Running != true && DateTime.Now < end) { await Task.Delay(1000); await RefreshAsync(); }
        if (Distro(d)?.Running != true) throw new InvalidOperationException($"A distro \"{d}\" não iniciou em 90 s.");

        // a distro do Syncthing subiu: espera o Syncthing (se ele inicia junto)
        if (IsMonitored(d))
        {
            SetBusy("Aguardando o Syncthing…");
            end = DateTime.Now.AddSeconds(45);
            while (_snap?.St != StState.Running && DateTime.Now < end) { await Task.Delay(1500); await RefreshAsync(); }
        }
    }

    /// <summary>
    /// Para o Syncthing da distro de forma limpa: pela API se for a distro do Syncthing, senão por SIGTERM.
    /// Se ele não parar, lança exceção e nada é encerrado.
    /// </summary>
    async Task StopSyncthingInAsync(string d)
    {
        if (!await WslService.IsProcessRunningAsync(d, "syncthing")) return;
        SetBusy($"Parando o Syncthing em {d}…");
        bool viaApi = false;
        if (IsMonitored(d) && _snap?.St == StState.Running)
        {
            try { await _collector.ShutdownSyncthingAsync(_settings); viaApi = true; } catch { }
        }
        if (!viaApi) await WslService.SignalSyncthingAsync(d);
        if (!await WslService.WaitProcessExitAsync(d, "syncthing", TimeSpan.FromSeconds(30)))
            throw new InvalidOperationException($"O Syncthing em \"{d}\" não encerrou em 30 s (talvez um serviço o reinicie ou ele rode com outro usuário). Nada foi encerrado.");
    }

    /// <summary>Encerramento seguro de uma distro: para o Syncthing, grava em disco e encerra.</summary>
    async Task StopCoreAsync(string d)
    {
        if (IsMonitored(d)) { CancelVerify(); _guard = GuardState.Off; }
        if (_keepAliveRequest?.Distro.Equals(d, StringComparison.OrdinalIgnoreCase) == true) SetKeepAlive(null, null);
        await StopSyncthingInAsync(d);
        SetBusy($"Encerrando {d}…");
        await WslService.TerminateSafelyAsync(d);
    }

    async Task StopDistroAsync(string d)
    {
        if (Distro(d)?.Running != true || _busy) return;
        if (!Confirm($"Encerrar {d}?\n\nSe o Syncthing estiver rodando nela, ele é parado primeiro e os dados são gravados em disco antes de encerrar.")) return;
        await RunBusyAsync(() => StopCoreAsync(d));
    }

    async Task RestartDistroAsync(string d)
    {
        if (Distro(d)?.Running != true || _busy) return;
        if (!Confirm($"Reiniciar {d}?\n\nEla é encerrada com segurança (o Syncthing é parado primeiro) e iniciada de novo.")) return;
        await RunBusyAsync(async () =>
        {
            await StopCoreAsync(d);
            await RefreshAsync();
            await StartCoreAsync(d);
        });
    }

    /// <summary>Encerramento seguro de todas as distros e da máquina virtual do WSL.</summary>
    async Task ShutdownAllCoreAsync(List<string> running)
    {
        CancelVerify(); _guard = GuardState.Off;
        if (_keepAliveRequest != null) SetKeepAlive(null, null);
        foreach (var d in running) await StopSyncthingInAsync(d);
        SetBusy("Encerrando o WSL…");
        await WslService.ShutdownAllSafelyAsync(running);
    }

    async Task ShutdownAllAsync()
    {
        var running = RunningDistros;
        if (running.Count == 0 || _busy) return;
        if (!Confirm($"Encerrar todo o WSL ({string.Join(", ", running)})?\n\nO Syncthing é parado primeiro onde estiver rodando, os dados são gravados em disco e então todas as distros e a máquina virtual do WSL são encerradas.")) return;
        await RunBusyAsync(() => ShutdownAllCoreAsync(running));
    }

    // ------------------------------------------------------------------ outras operações

    async Task UpdateWslAsync()
    {
        if (_busy) return;
        var running = RunningDistros;
        if (!Confirm("Atualizar o WSL?\n\n" + (running.Count > 0
                ? $"Antes, todo o WSL ({string.Join(", ", running)}) é encerrado com segurança. "
                : "") + "Pode levar alguns minutos.")) return;
        await RunBusyAsync(async () =>
        {
            if (running.Count > 0) await ShutdownAllCoreAsync(running);
            SetBusy("Atualizando o WSL…");
            var result = await WslService.UpdateWslAsync();
            _wslVersion = null; // relê na ficha técnica
            MessageBox.Show(string.IsNullOrWhiteSpace(result) ? "WSL atualizado." : result, AppName);
        });
    }

    async Task ExportDistroAsync(string d)
    {
        if (_busy) return;
        using var dialog = new SaveFileDialog
        {
            Title = $"Exportar backup de {d}",
            FileName = $"{d}-{DateTime.Now:yyyy-MM-dd}.tar.gz",
            Filter = "Backup do WSL (*.tar.gz)|*.tar.gz",
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        var file = dialog.FileName;
        await RunBusyAsync(async () =>
        {
            SetBusy($"Exportando {d}… (pode levar vários minutos)");
            await WslService.ExportAsync(d, file);
            if (Confirm($"Backup salvo em:\n{file}\n\nAbrir a pasta?")) WslService.OpenInExplorer(Path.GetDirectoryName(file)!);
        });
    }

    Task SetDefaultDistroAsync(string d) => RunBusyAsync(async () =>
    {
        SetBusy($"Definindo {d} como padrão…");
        await WslService.SetDefaultAsync(d);
    });

    /// <summary>Troca a distro onde o Syncthing é acompanhado.</summary>
    void UseForSyncthing(string name)
    {
        ResetGuardForDistroChange();
        _settings.Distro = name;
        SaveSettings();
        _collector.InvalidateCredentials();
        RefreshNow();
    }

    void OpenTerminal(string d, string linuxPath = "~") => Try(() => WslService.OpenTerminal(d, linuxPath));

    /// <summary>Abre um caminho da distro no Explorer (lê o home na primeira vez; a distro precisa estar ligada).</summary>
    async Task OpenInExplorerAsync(string d, string linuxPath)
    {
        try
        {
            var home = await WslService.GetHomeAsync(d);
            WslService.OpenInExplorer(WslPaths.ToUnc(d, linuxPath, home));
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    static void EditWslConfig() => Try(() =>
    {
        var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");
        if (!File.Exists(file)) File.WriteAllText(file, "[wsl2]\n");
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{file}\"") { UseShellExecute = true });
    });
}
