using System.Diagnostics;

namespace WslSyncMonitor;

static class Program
{
    public const string UninstallArg = "--uninstall";
    public const string AfterInstallArg = "--after-install";

    [STAThread]
    static void Main(string[] args)
    {
        ErrorLog.Install();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using var mutex = AppInstance.CreateMutex();
        if (args.Contains(UninstallArg)) { Uninstall(mutex); return; }

        // depois de atualizar ou instalar, a instância anterior ainda está fechando: espera ela liberar
        bool handover = args.Contains(Updater.AfterUpdateArg) || args.Contains(AfterInstallArg);
        if (!AppInstance.TryAcquire(mutex, handover ? TimeSpan.FromSeconds(30) : TimeSpan.Zero))
        {
            if (!InstallPaths.Current.IsInstalledExe(Environment.ProcessPath!))
                MessageBox.Show("O WSL Sync Monitor já está em execução.\n\nPara instalar ou atualizar com esta cópia, feche-o antes (menu → Sair).",
                    InstallPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        Updater.CleanupOld();

        try
        {
            if (!handover && OfferInstall()) return; // instalou: a cópia instalada já foi iniciada
            Application.Run(new TrayApp());
        }
        finally { mutex.ReleaseMutex(); }
    }

    /// <summary>Rodando fora da pasta de instalação: pergunta se instala. true = instalou e iniciou a cópia instalada.</summary>
    static bool OfferInstall()
    {
        var installer = Installer.ForCurrentUser();
        if (installer.Paths.IsInstalledExe(Environment.ProcessPath!)) return false;
        var settings = Settings.LoadOrDefault(out _);
        if (settings.InstallPromptDismissed) return false;

        using var form = new InstallForm(installer.Paths.Directory, offerDontAsk: true);
        if (form.ShowDialog() != DialogResult.OK)
        {
            if (form.DontAskAgain) { settings.InstallPromptDismissed = true; settings.Save(); }
            return false;
        }
        return InstallAndStart(installer, form.Options);
    }

    /// <summary>Instala e inicia a cópia instalada (que espera esta fechar). Erros viram aviso; false = não instalou.</summary>
    public static bool InstallAndStart(Installer installer, InstallOptions options)
    {
        try
        {
            installer.Install(Environment.ProcessPath!, options);
            Process.Start(new ProcessStartInfo(installer.Paths.Exe, AfterInstallArg) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Não foi possível instalar: " + ex.Message, InstallPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    static void Uninstall(Mutex mutex)
    {
        if (MessageBox.Show("Desinstalar o WSL Sync Monitor?\n\nOs atalhos, o início com o Windows e o programa são removidos.",
                InstallPaths.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        // a instância em execução fecha com a saída segura (espera a sincronização, até o limite configurado)
        if (!AppInstance.RequestExitAndWait(mutex, TimeSpan.FromMinutes(3)))
        {
            MessageBox.Show("O WSL Sync Monitor ainda está esperando a sincronização para fechar. Tente de novo em alguns minutos.",
                InstallPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
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
