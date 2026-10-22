using System.Text;

namespace WslSyncMonitor;

/// <summary>
/// Registro do app (log) em %APPDATA%\WslSyncMonitor\logs\app-AAAAMMDD.log: um arquivo por dia, mantidos por
/// <see cref="KeepDays"/> dias. Regras (formato, segredos ocultos, limite) em <see cref="LogRules"/>.
///
/// Convenções de tratamento de erros do app:
/// <list type="bullet">
/// <item>Ação do usuário que falha: <c>ShowError</c> mostra a mensagem e registra como erro.</item>
/// <item>Falha que se repete num loop (API sem resposta a cada atualização): <see cref="Failure"/> registra quando
/// aparece ou muda, e <see cref="Recovered"/> uma vez quando volta ao normal.</item>
/// <item>Eventos importantes (proteção, desligamento, atualização): <see cref="Info"/>.</item>
/// <item>Falha esperada e sem consequência (recurso ausente no Windows 10, por exemplo): ignorada com comentário.</item>
/// <item>Erros não tratados: registrados por <see cref="Install"/> (interface, outras threads, tarefas).</item>
/// </list>
/// Segredos (a chave da API) são registrados com <see cref="AddSecret"/> e nunca aparecem no arquivo.
/// Escrever no log nunca lança exceção.
/// </summary>
public static class Log
{
    public const int KeepDays = 14;
    const long MaxSize = 2 * 1024 * 1024;

    public static string Dir => Path.Combine(Settings.Dir, "logs");
    public static string CurrentFile => Path.Combine(Dir, LogRules.FileName(DateTime.Now));

    static readonly object _lock = new();
    static readonly HashSet<string> _secrets = new();
    static readonly LogDedup _dedup = new();
    static DateTime _cleanedOn;

    public static void AddSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (_lock) _secrets.Add(secret);
    }

    public static void Info(string area, string message) => Write(LogLevel.Info, area, message, null);
    public static void Warn(string area, string message, Exception? ex = null) => Write(LogLevel.Warn, area, message, ex);
    public static void Error(string area, string message, Exception? ex = null) => Write(LogLevel.Error, area, message, ex);

    /// <summary>Falha recorrente identificada por <paramref name="key"/>: registra só quando aparece ou muda.</summary>
    public static void Failure(string area, string key, string message)
    {
        if (_dedup.ShouldLog(key, message)) Warn(area, message);
    }

    /// <summary>A falha <paramref name="key"/> deixou de acontecer: registra uma vez.</summary>
    public static void Recovered(string area, string key, string message)
    {
        if (_dedup.Recovered(key)) Info(area, message);
    }

    static void Write(LogLevel level, string area, string message, Exception? ex)
    {
        try
        {
            lock (_lock)
            {
                var now = DateTime.Now;
                Directory.CreateDirectory(Dir);
                if (_cleanedOn != now.Date) Cleanup(now);
                var file = Path.Combine(Dir, LogRules.FileName(now));
                long size = File.Exists(file) ? new FileInfo(file).Length : 0;
                if (!LogRules.ShouldWrite(level, size, MaxSize)) return;
                var text = LogRules.Redact(LogRules.Format(now, level, area, message, ex), _secrets);
                File.AppendAllText(file, text.Replace("\n", Environment.NewLine) + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch { /* sem onde gravar: não há o que fazer, e o log não pode derrubar o app */ }
    }

    static void Cleanup(DateTime now)
    {
        _cleanedOn = now.Date;
        var names = Directory.EnumerateFiles(Dir).Select(Path.GetFileName).OfType<string>();
        foreach (var name in LogRules.ToDelete(names, now, KeepDays))
            try { File.Delete(Path.Combine(Dir, name)); } catch { /* em uso: fica para o próximo dia */ }
    }

    /// <summary>Final do log mais recente (para a ficha técnica), ou null se não houver.</summary>
    public static string? Tail(int maxChars = 3000)
    {
        try
        {
            var file = Directory.Exists(Dir) ? Directory.GetFiles(Dir, "app-*.log").Max() : null;
            if (file == null) return null;
            string text;
            lock (_lock) text = File.ReadAllText(file);
            return text.Length <= maxChars ? text : "…" + text[^maxChars..];
        }
        catch { return null; }
    }

    /// <summary>Registra o início do app e as exceções não tratadas da interface, de outras threads e de tarefas.</summary>
    public static void Install()
    {
        Info("app", $"iniciado · versão {Updater.Current.ToString(3)} · {Environment.OSVersion.VersionString} · {Environment.ProcessPath}");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
        {
            Error("app", "erro na interface", e.Exception);
            MessageBox.Show($"Ocorreu um erro inesperado e ele foi registrado em:\n{CurrentFile}\n\n{e.Exception.Message}",
                InstallPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Error("app", "erro fatal", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Error("app", "erro em tarefa em segundo plano", e.Exception);
            e.SetObserved();
        };
    }
}
