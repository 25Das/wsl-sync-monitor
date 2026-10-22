namespace WslSyncMonitor;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // depois de uma atualização, a versão anterior ainda está fechando: espera ela liberar o mutex
        bool afterUpdate = args.Contains(Updater.AfterUpdateArg);
        using var mutex = new Mutex(false, @"Local\WslSyncMonitor.SingleInstance");
        try { if (!mutex.WaitOne(afterUpdate ? TimeSpan.FromSeconds(30) : TimeSpan.Zero)) return; }
        catch (AbandonedMutexException) { /* a instância anterior fechou sem liberar: assume */ }
        Updater.CleanupOld();

        ErrorLog.Install();
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp());
        mutex.ReleaseMutex();
    }
}
