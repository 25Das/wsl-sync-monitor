using System.Diagnostics;

namespace WslSyncMonitor;

/// <summary>Verifica a última release no GitHub e instala a nova versão (troca o .exe e reinicia).</summary>
public sealed class Updater : IDisposable
{
    const string GitHubLatestUrl = "https://api.github.com/repos/25Das/wsl-sync-monitor/releases/latest";

    /// <summary>Última release; WSLSYNCMONITOR_UPDATE_URL troca o endereço (servidor falso dos testes de ponta a ponta).</summary>
    static string LatestUrl => Environment.GetEnvironmentVariable("WSLSYNCMONITOR_UPDATE_URL") is { Length: > 0 } url
        ? url : GitHubLatestUrl;
    public const string AfterUpdateArg = "--after-update";

    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public Updater()
    {
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"WslSyncMonitor/{Current}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public static Version Current => typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0);
    static string ExePath => Environment.ProcessPath!;

    /// <summary>Atualização disponível na última verificação (null = nenhuma).</summary>
    public UpdateInfo? Available { get; private set; }

    public async Task<UpdateInfo?> CheckAsync()
    {
        var u = UpdateInfo.Parse(await _http.GetStringAsync(LatestUrl));
        Available = u != null && u.IsNewerThan(Current) ? u : null;
        return Available;
    }

    /// <summary>
    /// Baixa o novo .exe, troca pelo atual (o anterior fica ao lado, em .old) e inicia a nova versão, que espera
    /// esta fechar. O Windows permite renomear um .exe em execução. Quem chama confere com <see cref="WaitStarted"/>
    /// que a nova versão abriu antes de fechar esta; senão, volta com <see cref="Rollback(string)"/>.
    /// </summary>
    public async Task<Process?> InstallAsync(UpdateInfo u)
    {
        var bytes = await _http.GetByteArrayAsync(u.DownloadUrl);
        if (bytes.Length != u.Size || bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z')
            throw new InvalidDataException("O arquivo baixado não é o executável esperado.");
        SwapIn(ExePath, bytes);
        // ShellExecute: a nova versão não herda handles deste processo (inclusive o do .exe antigo)
        try { return Process.Start(new ProcessStartInfo(ExePath, AfterUpdateArg) { UseShellExecute = true }); }
        catch { Rollback(ExePath); throw; }
    }

    /// <summary>Põe o novo executável no lugar do atual, guardando o atual em .old (desfaz se a troca falhar no meio).</summary>
    public static void SwapIn(string exe, byte[] bytes)
    {
        string next = exe + ".new", old = exe + ".old";
        File.WriteAllBytes(next, bytes);
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(next, exe); }
        catch { File.Move(old, exe); throw; }
    }

    /// <summary>
    /// Volta para a versão anterior (.old), guardando a que falhou em .failed. false: não há versão anterior.
    /// </summary>
    public static bool Rollback(string exe)
    {
        string old = exe + ".old", failed = exe + ".failed";
        if (!File.Exists(old)) return false;
        if (File.Exists(failed)) File.Delete(failed);
        if (File.Exists(exe)) File.Move(exe, failed);
        File.Move(old, exe);
        return true;
    }

    // ---- aviso de que a nova versão abriu (antes de esta fechar: sem ele, a proteção ficaria sem app nenhum) ----

    const string StartedEventName = @"Local\WslSyncMonitor.UpdateStarted";

    /// <summary>Cria o aviso antes de iniciar a nova versão.</summary>
    public static EventWaitHandle CreateStartedSignal() => new(false, EventResetMode.ManualReset, StartedEventName);

    /// <summary>Na nova versão, assim que ela abre: avisa a anterior (que então pode fechar). false: ninguém esperava.</summary>
    public static bool SignalStarted()
    {
        if (!EventWaitHandle.TryOpenExisting(StartedEventName, out var e)) return false;
        using (e) return e.Set();
    }

    /// <summary>
    /// Quanto esperar a nova versão avisar que abriu (antivírus e disco lento atrasam a primeira execução).
    /// WSLSYNCMONITOR_UPDATE_WAIT_SECONDS troca o prazo (testes de ponta a ponta).
    /// </summary>
    public static TimeSpan StartTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable("WSLSYNCMONITOR_UPDATE_WAIT_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(60);

    /// <summary>A nova versão avisou que abriu dentro do prazo?</summary>
    public static Task<bool> WaitStarted(EventWaitHandle signal, TimeSpan timeout) => Task.Run(() => signal.WaitOne(timeout));

    /// <summary>
    /// Apaga as sobras de uma atualização (.old, .new, .failed). Só depois de a nova versão iniciar de fato: até lá,
    /// o .old é o caminho de volta. Pode falhar enquanto a versão anterior ainda fecha.
    /// </summary>
    public static void CleanupOld() => CleanupOld(ExePath);

    public static void CleanupOld(string exe)
    {
        foreach (var f in new[] { exe + ".old", exe + ".new", exe + ".failed" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    public void Dispose() => _http.Dispose();
}
