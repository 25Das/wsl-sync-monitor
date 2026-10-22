using System.Text;

namespace WslSyncMonitor;

/// <summary>
/// Registro de erros não tratados em %APPDATA%\WslSyncMonitor\erros.log (aparece na ficha técnica).
/// Um estouro de pilha encerra o processo antes de qualquer registro; os demais erros ficam gravados.
/// </summary>
public static class ErrorLog
{
    public static string FilePath => Path.Combine(Settings.Dir, "erros.log");
    const long MaxSize = 512 * 1024;

    public static void Write(string context, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxSize) File.Delete(FilePath);
            File.AppendAllText(FilePath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] versão {Updater.Current.ToString(3)} · {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { /* sem onde gravar: não há o que fazer */ }
    }

    /// <summary>Últimas linhas do registro (para a ficha técnica), ou null se não houver erros.</summary>
    public static string? Tail(int maxChars = 2000)
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            var text = File.ReadAllText(FilePath);
            return text.Length <= maxChars ? text : "…" + text[^maxChars..];
        }
        catch { return null; }
    }

    /// <summary>Registra exceções não tratadas da interface, de outras threads e de tarefas esquecidas.</summary>
    public static void Install()
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Write("erro na interface", e.Exception);
            MessageBox.Show($"Ocorreu um erro inesperado e ele foi registrado em:\n{FilePath}\n\n{e.Exception.Message}",
                "WSL Sync Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Write("erro fatal", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Write("erro em tarefa em segundo plano", e.Exception);
            e.SetObserved();
        };
    }
}
