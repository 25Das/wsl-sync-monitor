using System.Diagnostics;

namespace WslSyncMonitor;

static class Program
{
    public const string UninstallArg = "--uninstall";
    public const string AfterInstallArg = "--after-install";

    static string ExePath => Environment.ProcessPath!;

    [STAThread]
    static void Main(string[] args)
    {
        Log.Install();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = AppInstance.CreateMutex();
        if (args.Contains(UninstallArg)) { Uninstall(mutex); return; }

        // depois de atualizar ou instalar, a instância anterior ainda está fechando: espera ela liberar
        bool handover = args.Contains(Updater.AfterUpdateArg) || args.Contains(AfterInstallArg);
        var installer = Installer.ForCurrentUser();
        bool isInstalledCopy = installer.Paths.IsInstalledExe(ExePath);
        if (!handover && !isInstalledCopy && OfferInstall(installer, mutex)) return;

        if (!AppInstance.TryAcquire(mutex, handover ? TimeSpan.FromSeconds(30) : TimeSpan.Zero))
        {
            if (!isInstalledCopy)
                MessageBox.Show("O WSL Sync Monitor já está em execução.", InstallPaths.AppName,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            Updater.CleanupOld();
            // a cópia instalada pode ter sido atualizada pelo próprio app: mantém "Aplicativos instalados" em dia
            if (isInstalledCopy) try { installer.RefreshRegistration(Updater.Current); } catch (Exception ex) { Log.Warn("instalação", "atualizar o registro em Aplicativos instalados", ex); }
            Application.Run(new TrayApp());
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>
    /// Rodando fora da pasta de instalação: oferece instalar, atualizar ou reinstalar conforme a versão já instalada.
    /// true = esta cópia não deve continuar (instalou e passou para a instalada, ou abriu a instalada).
    /// </summary>
    static bool OfferInstall(Installer installer, Mutex mutex)
    {
        var installed = installer.InstalledVersion();
        var kind = InstallDecision.Decide(Updater.Current, installed);
        if (kind == InstallKind.InstalledIsNewer) return OfferInstalledInstead(installer, installed!, mutex);

        var settings = Settings.LoadOrDefault(out _);
        if (kind == InstallKind.Fresh && settings.InstallPromptDismissed) return false;

        using var form = new InstallForm(InstallDecision.Title(kind, Updater.Current, installed), installer.Paths.Directory,
            offerDontAsk: kind == InstallKind.Fresh);
        if (form.ShowDialog() != DialogResult.OK)
        {
            if (form.DontAskAgain) { settings.InstallPromptDismissed = true; settings.Save(); }
            return false;
        }

        // a instância em execução (instalada ou não) usa o exe e o mutex: pede para ela fechar com a saída segura
        var exit = AppInstance.RequestExitAndWait(mutex, null, TimeSpan.FromMinutes(3));
        if (exit is ExitRequest.NotListening or ExitRequest.StillRunning)
        {
            MessageBox.Show(AppInstance.Explain(exit), InstallPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        try { return InstallAndStart(installer, form.Options); }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>A instalada é mais nova que esta cópia: não instala por cima, oferece abrir a instalada.</summary>
    static bool OfferInstalledInstead(Installer installer, Version installed, Mutex mutex)
    {
        if (!AppInstance.TryAcquire(mutex, TimeSpan.Zero))
        {
            MessageBox.Show($"A versão {installed.ToString(3)}, mais nova que esta, já está instalada e em execução.",
                InstallPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return true;
        }
        mutex.ReleaseMutex();
        if (MessageBox.Show($"A versão {installed.ToString(3)}, mais nova que esta ({Updater.Current.ToString(3)}), já está instalada.\n\n" +
                "Abrir a versão instalada? (Não = usar esta cópia sem instalar)",
                InstallPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return false;
        return StartInstalled(installer);
    }

    /// <summary>Instala e inicia a cópia instalada (que espera esta fechar). Erros viram aviso; false = não instalou.</summary>
    public static bool InstallAndStart(Installer installer, InstallOptions options)
    {
        try { installer.Install(ExePath, options); }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível instalar: " + ex.Message, InstallPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return StartInstalled(installer);
    }

    /// <summary>Inicia a cópia instalada; ela espera esta liberar o mutex.</summary>
    public static bool StartInstalled(Installer installer)
    {
        try
        {
            Process.Start(new ProcessStartInfo(installer.Paths.Exe, AfterInstallArg) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível abrir a versão instalada: " + ex.Message, InstallPaths.AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    static void Uninstall(Mutex mutex)
    {
        if (MessageBox.Show("Desinstalar o WSL Sync Monitor?\n\nOs atalhos, o início com o Windows e o programa são removidos.",
                InstallPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        // a instância em execução fecha com a saída segura (espera a sincronização, até o limite configurado)
        var exit = AppInstance.RequestExitAndWait(mutex, null, TimeSpan.FromMinutes(3));
        if (exit is ExitRequest.NotListening or ExitRequest.StillRunning)
        {
            MessageBox.Show(AppInstance.Explain(exit), InstallPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var installer = Installer.ForCurrentUser();
            installer.RemoveShortcutsAndRegistration();
            bool removeSettings = MessageBox.Show("Remover também as configurações (pasta WslSyncMonitor em %APPDATA%)?",
                InstallPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
            if (removeSettings) try { Directory.Delete(Settings.Dir, recursive: true); } catch { }
            installer.ScheduleDirectoryRemoval();
            MessageBox.Show("WSL Sync Monitor desinstalado.", InstallPaths.AppName);
        }
        finally { mutex.ReleaseMutex(); }
    }
}
