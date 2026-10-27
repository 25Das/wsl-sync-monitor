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
    /// Baixa o novo .exe, troca pelo atual (o anterior fica ao lado, em .old) e inicia a nova versão. O Windows
    /// permite renomear um .exe em execução. Quem chama espera com <see cref="WaitReady"/> a nova versão ficar pronta
    /// antes de fechar; senão, volta com <see cref="Rollback(string)"/>. Se ela nem inicia, já volta aqui.
    /// </summary>
    public async Task<Process> InstallAsync(UpdateInfo u)
    {
        var bytes = await _http.GetByteArrayAsync(u.DownloadUrl);
        if (bytes.Length != u.Size || bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z')
            throw new InvalidDataException("O arquivo baixado não é o executável esperado.");
        SwapIn(ExePath, bytes);
        try
        {
            // ShellExecute: a nova versão não herda handles deste processo (inclusive o do .exe antigo)
            return Process.Start(new ProcessStartInfo(ExePath, StartArguments(Environment.ProcessId)) { UseShellExecute = true })
                   ?? throw new InvalidOperationException("a versão nova não iniciou");
        }
        catch { Rollback(ExePath); throw; }
    }

    /// <summary>Argumentos da nova versão: é uma atualização, e esta (pelo número do processo) espera o aviso de pronta.</summary>
    public static string StartArguments(int previousPid) => $"{AfterUpdateArg} {FromArg}{previousPid}";

    const string FromArg = "--from=";

    /// <summary>Na nova versão: o processo da versão anterior que espera o aviso de pronta (null: ela não espera).</summary>
    public static int? PreviousPid(IEnumerable<string> args) =>
        args.FirstOrDefault(a => a.StartsWith(FromArg, StringComparison.Ordinal)) is { } a && int.TryParse(a[FromArg.Length..], out var pid)
            ? pid : null;

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
    /// Volta para a versão anterior (.old), guardando a que falhou em .failed (desfaz se a volta falhar no meio: o
    /// lugar do .exe nunca fica vazio). false: não há versão anterior.
    /// </summary>
    public static bool Rollback(string exe)
    {
        string old = exe + ".old", failed = exe + ".failed";
        if (!File.Exists(old)) return false;
        if (File.Exists(failed)) File.Delete(failed);
        bool moved = File.Exists(exe);
        if (moved) File.Move(exe, failed);
        try { File.Move(old, exe); }
        catch { if (moved) File.Move(failed, exe); throw; }
        return true;
    }

    // ---- aviso de que a nova versão está pronta (só então a anterior fecha: a proteção nunca fica sem app) ----

    static string ReadyEventName(int previousPid) => $@"Local\WslSyncMonitor.UpdateReady.{previousPid}";

    /// <summary>Na versão anterior, antes de iniciar a nova: o aviso que ela vai dar.</summary>
    public static EventWaitHandle CreateReadySignal(int previousPid) =>
        new(false, EventResetMode.ManualReset, ReadyEventName(previousPid));

    /// <summary>Na nova versão, depois da primeira leitura do estado: avisa a anterior. false: ela não espera mais.</summary>
    public static bool SignalReady(int previousPid)
    {
        if (!EventWaitHandle.TryOpenExisting(ReadyEventName(previousPid), out var e)) return false;
        using (e) return e.Set();
    }

    /// <summary>
    /// Quanto esperar a nova versão ficar pronta (antivírus e disco lento atrasam a primeira execução; a primeira
    /// leitura usa o wsl.exe). WSLSYNCMONITOR_UPDATE_WAIT_SECONDS troca o prazo (testes de ponta a ponta).
    /// </summary>
    public static TimeSpan StartTimeout =>
        int.TryParse(Environment.GetEnvironmentVariable("WSLSYNCMONITOR_UPDATE_WAIT_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s) : TimeSpan.FromSeconds(120);

    /// <summary>
    /// A nova versão avisou que está pronta, dentro do prazo, e continua rodando? Se ela fecha antes (falhou ao
    /// iniciar), a resposta é na hora.
    /// </summary>
    public static async Task<bool> WaitReady(EventWaitHandle ready, Process next, TimeSpan timeout)
    {
        // espera registrada no pool (sem prender uma thread) e cancelada ao terminar, qualquer que seja o resultado
        var signaled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var wait = ThreadPool.RegisterWaitForSingleObject(ready, (_, timedOut) => signaled.TrySetResult(!timedOut),
            null, timeout, executeOnlyOnce: true);
        using var stop = new CancellationTokenSource();
        try
        {
            var exited = next.WaitForExitAsync(stop.Token);
            await Task.WhenAny(signaled.Task, exited);
            if (exited.IsCompletedSuccessfully) return false; // fechou antes de ficar pronta
            return await signaled.Task && !next.HasExited;
        }
        finally
        {
            wait.Unregister(null);
            stop.Cancel();
        }
    }

    /// <summary>
    /// Apaga as sobras de uma atualização (.old, .new, .failed). Ao abrir normalmente, na hora; depois de uma
    /// atualização, só quando a versão anterior já fechou (até lá, o .old é o caminho de volta e está em uso).
    /// </summary>
    public static void CleanupOld() => CleanupOld(ExePath);

    public static void CleanupOld(string exe)
    {
        foreach (var f in new[] { exe + ".old", exe + ".new", exe + ".failed" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    /// <summary>Limpa as sobras depois que a versão anterior fechar (pid null: ela não esperava; dá um tempo).</summary>
    public static Task CleanupAfterPreviousExitsAsync(int? previousPid) => Task.Run(async () =>
    {
        try
        {
            if (previousPid is { } pid)
                using (var p = Process.GetProcessById(pid)) await p.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5));
        }
        catch { /* já fechou (ou não deu para esperar): limpa mesmo assim */ }
        static bool Leftovers() => new[] { ".old", ".new", ".failed" }.Any(ext => File.Exists(ExePath + ext));
        for (int i = 0; i < 20 && Leftovers(); i++)
        {
            CleanupOld();
            if (Leftovers()) await Task.Delay(3000); // a anterior ainda está fechando (o .old é o .exe dela)
        }
    });

    public void Dispose() => _http.Dispose();
}
