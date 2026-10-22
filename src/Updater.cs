using System.Diagnostics;

namespace WslSyncMonitor;

/// <summary>Verifica a última release no GitHub e instala a nova versão (troca o .exe e reinicia).</summary>
public sealed class Updater : IDisposable
{
    const string LatestUrl = "https://api.github.com/repos/25Das/wsl-sync-monitor/releases/latest";
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
    /// Baixa o novo .exe, troca pelo atual e inicia a nova versão (que espera esta fechar).
    /// Quem chama deve encerrar o app logo depois. O Windows permite renomear um .exe em execução.
    /// </summary>
    public async Task InstallAsync(UpdateInfo u)
    {
        var exe = ExePath;
        string next = exe + ".new", old = exe + ".old";
        var bytes = await _http.GetByteArrayAsync(u.DownloadUrl);
        if (bytes.Length != u.Size || bytes.Length < 2 || bytes[0] != 'M' || bytes[1] != 'Z')
            throw new InvalidDataException("O arquivo baixado não é o executável esperado.");
        await File.WriteAllBytesAsync(next, bytes);

        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Move(next, exe); }
        catch { File.Move(old, exe); throw; }

        // ShellExecute: a nova versão não herda handles deste processo (inclusive o do .exe antigo)
        Process.Start(new ProcessStartInfo(exe, AfterUpdateArg) { UseShellExecute = true });
    }

    /// <summary>Apaga o .exe antigo deixado por uma atualização (pode falhar enquanto ele ainda fecha).</summary>
    public static void CleanupOld()
    {
        foreach (var f in new[] { ExePath + ".old", ExePath + ".new" })
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    public void Dispose() => _http.Dispose();
}
