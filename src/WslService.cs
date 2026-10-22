using System.Diagnostics;
using System.Text;

namespace WslSyncMonitor;

/// <summary>Consulta o WSL através do wsl.exe.</summary>
public static class WslService
{
    public sealed record Result(int ExitCode, string Output, string Error = "");

    static string WslExe
    {
        get
        {
            // Processo 32 bits precisa usar Sysnative para alcançar o wsl.exe de 64 bits.
            var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var sysnative = Path.Combine(win, "Sysnative", "wsl.exe");
            if (!Environment.Is64BitProcess && File.Exists(sysnative)) return sysnative;
            var sys32 = Path.Combine(win, "System32", "wsl.exe");
            return File.Exists(sys32) ? sys32 : "wsl.exe";
        }
    }

    /// <summary>PIDs de wsl.exe criados por este app (não contam como "alguém usando o WSL").</summary>
    static readonly HashSet<int> OwnPids = new();

    // opções do wsl.exe que só administram o WSL (não abrem sessão na distro)
    static readonly HashSet<string> AdminFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-l", "--list", "--shutdown", "-t", "--terminate", "--status", "--version", "-v", "--update",
        "-s", "--set-default", "--set-version", "--set-default-version", "--install", "--unregister",
        "--export", "--import", "--import-in-place", "--mount", "--unmount", "--manage", "--help", "--debug-shell",
    };

    /// <summary>
    /// Conta as sessões abertas na distro por outros programas (VS Code, dev containers, terminais):
    /// processos wsl.exe do Windows que não foram criados por este app e cuja linha de comando aponta para a distro.
    /// </summary>
    public static int CountExternalSessions(string distro, string? defaultDistro)
    {
        int n = 0;
        foreach (var p in Process.GetProcessesByName("wsl"))
        {
            using (p)
            {
                bool own;
                lock (OwnPids) own = OwnPids.Contains(p.Id) || _keepAlive.Values.Any(k => k.Id == p.Id && !k.HasExited);
                if (own) continue;
                var target = SessionTarget(Native.GetCommandLine(p.Id), defaultDistro);
                if (target == null || target.Equals(distro, StringComparison.OrdinalIgnoreCase)) n++;
            }
        }
        return n;
    }

    /// <summary>Distro de uma linha de comando do wsl.exe; "" se for só administração; null se não der para saber.</summary>
    static string? SessionTarget(string? cmd, string? defaultDistro)
    {
        if (cmd == null) return null; // sem acesso: por segurança, conta como sessão
        var tokens = cmd.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(t => t.Trim('"')).ToList();
        string? target = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t is "--" or "-e" or "--exec") break;          // daqui em diante é o comando Linux
            if (AdminFlags.Contains(t)) return "";
            if ((t is "-d" or "--distribution") && i + 1 < tokens.Count) target = tokens[++i];
        }
        return target ?? defaultDistro; // null = distro desconhecida: conta como sessão
    }

    public static bool IsInstalled => File.Exists(WslExe) || WslExe == "wsl.exe";

    public static async Task<Result> RunAsync(IEnumerable<string> args, int timeoutMs = 10000)
    {
        var psi = new ProcessStartInfo(WslExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["WSL_UTF8"] = "1";

        using var p = new Process { StartInfo = psi };
        p.Start();
        int pid = p.Id;
        lock (OwnPids) OwnPids.Add(pid);
        using var ms = new MemoryStream();
        var copyOut = p.StandardOutput.BaseStream.CopyToAsync(ms);
        using var err = new MemoryStream();
        var drainErr = p.StandardError.BaseStream.CopyToAsync(err);
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            await Task.WhenAll(copyOut, drainErr);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            throw new TimeoutException("wsl.exe não respondeu a tempo");
        }
        finally { lock (OwnPids) OwnPids.Remove(pid); }
        return new Result(p.ExitCode, Decode(ms.ToArray()), Decode(err.ToArray()).Trim());
    }

    /// <summary>wsl.exe antigo escreve UTF‑16LE; com WSL_UTF8=1 ou comandos Linux, UTF‑8.</summary>
    static string Decode(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        int zeros = 0;
        for (int i = 1; i < Math.Min(b.Length, 200); i += 2) if (b[i] == 0) zeros++;
        bool utf16 = b.Length >= 4 && zeros > Math.Min(b.Length, 200) / 4;
        return (utf16 ? Encoding.Unicode.GetString(b) : Encoding.UTF8.GetString(b)).Replace("\0", "");
    }

    static IEnumerable<string> Lines(string s) =>
        s.Split('\n').Select(l => l.Trim().Trim('\r')).Where(l => l.Length > 0);

    /// <summary>Lista as distros. Usa "-l --running -q" para o estado (independe do idioma do Windows).</summary>
    public static async Task<List<DistroInfo>> GetDistrosAsync()
    {
        var verbose = await RunAsync(new[] { "--list", "--verbose" });
        if (verbose.ExitCode != 0) return new();
        var running = await RunAsync(new[] { "--list", "--running", "--quiet" });
        var runningSet = running.ExitCode == 0
            ? Lines(running.Output).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>();

        var list = new List<DistroInfo>();
        foreach (var raw in Lines(verbose.Output).Skip(1)) // pula cabeçalho (NAME STATE VERSION / NOME ESTADO VERSÃO)
        {
            var line = raw;
            bool isDefault = line.StartsWith('*');
            if (isDefault) line = line[1..].Trim();
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;
            int.TryParse(parts[^1], out var ver);
            var name = parts[0];
            list.Add(new DistroInfo(name, runningSet.Contains(name), ver, isDefault));
        }
        return list;
    }

    /// <summary>Executa um script sh na distro. Atenção: inicia a distro se estiver parada.</summary>
    public static Task<Result> ShAsync(string distro, string script, int timeoutMs = 10000) =>
        RunAsync(new[] { "-d", distro, "--exec", "sh", "-c", SyncthingScripts.ForSh(script) }, timeoutMs);

    public static async Task<bool> IsProcessRunningAsync(string distro, string name)
    {
        var r = await ShAsync(distro, $"pgrep -x {name} >/dev/null 2>&1 && echo yes || echo no");
        return r.Output.Trim() == "yes";
    }

    /// <summary>
    /// Lê os config.xml candidatos do Syncthing, na ordem de preferência: o que o processo em execução usa
    /// (--home/--config ou STHOMEDIR), o que `syncthing paths` informa e os locais padrão. Sem repetições.
    /// </summary>
    public static async Task<(List<(string Path, string Xml)> Files, int ExitCode, string Error)> ReadSyncthingConfigsAsync(
        string distro, string customPath)
    {
        var r = await ShAsync(distro, SyncthingScripts.ListConfigs(customPath));
        return (SyncthingScripts.ParseConfigs(r.Output), r.ExitCode, r.Error);
    }

    /// <summary>Primeira linha de "wsl --version" (ex.: "Versão do WSL: 2.7.3.0"), ou null.</summary>
    public static async Task<string?> GetVersionAsync()
    {
        try
        {
            var r = await RunAsync(new[] { "--version" });
            return r.Output.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        }
        catch { return null; }
    }

    public static async Task<string?> GetIpAsync(string distro)
    {
        var r = await ShAsync(distro, "hostname -I");
        return r.ExitCode == 0 ? r.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() : null;
    }

    // processos "sleep infinity" que mantêm cada distro iniciada pelo app ligada
    static readonly Dictionary<string, Native.DetachedProcess> _keepAlive = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Inicia a distro e mantém um processo oculto vivo para que ela não desligue por inatividade.</summary>
    public static void StartDistro(string distro)
    {
        if (_keepAlive.TryGetValue(distro, out var p))
        {
            if (!p.HasExited) return;
            p.Dispose();
        }
        // Process.Start herda os handles deste processo (inclusive o do próprio .exe); como este wsl.exe
        // vive enquanto o WSL estiver ligado, o executável ficaria travado mesmo após fechar o app.
        // Por isso é criado via CreateProcess sem herança de handles.
        _keepAlive[distro] = Native.DetachedProcess.Start($"\"{WslExe}\" -d {distro} --exec sleep infinity");
    }

    public static bool IsHolding(string distro) =>
        _keepAlive.TryGetValue(distro, out var p) && p is { HasExited: false };

    /// <summary>Solta a distro (encerra o processo que a mantém ligada); o WSL pode então desligar por ociosidade.</summary>
    public static void Release(string? distro = null) => ReleaseKeepAlive(distro);

    static void ReleaseKeepAlive(string? distro = null)
    {
        foreach (var name in _keepAlive.Keys.ToList())
        {
            if (distro != null && !name.Equals(distro, StringComparison.OrdinalIgnoreCase)) continue;
            try { _keepAlive[name].Kill(); } catch { }
            _keepAlive[name].Dispose();
            _keepAlive.Remove(name);
        }
    }

    /// <summary>Pede ao Syncthing para encerrar via SIGTERM (ele fecha o banco de dados de forma limpa).</summary>
    public static Task SignalSyncthingAsync(string distro) => ShAsync(distro, "pkill -TERM -x syncthing; true");

    /// <summary>
    /// Encerramento seguro da distro: grava os buffers em disco (sync) e só então faz "wsl --terminate".
    /// O Syncthing deve ter sido parado antes (ver TrayApp.StopSyncthingInAsync).
    /// </summary>
    public static async Task TerminateSafelyAsync(string distro)
    {
        try { await ShAsync(distro, "sync", 60000); } catch { }
        ReleaseKeepAlive(distro);
        var r = await RunAsync(new[] { "--terminate", distro }, 60000);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.Output.Trim());
    }

    /// <summary>Encerra todas as distros e a VM do WSL ("wsl --shutdown"), após gravar os dados em disco.</summary>
    public static async Task ShutdownAllSafelyAsync(IEnumerable<string> runningDistros)
    {
        foreach (var d in runningDistros)
            try { await ShAsync(d, "sync", 60000); } catch { }
        ReleaseKeepAlive();
        var r = await RunAsync(new[] { "--shutdown" }, 120000);
        if (r.ExitCode != 0) throw new InvalidOperationException(r.Output.Trim());
    }

    public static async Task SetDefaultAsync(string distro)
    {
        var r = await RunAsync(new[] { "--set-default", distro });
        if (r.ExitCode != 0) throw new InvalidOperationException(r.Output.Trim());
    }

    /// <summary>Abre um terminal na distro (usa o terminal padrão do Windows).</summary>
    public static void OpenTerminal(string distro) =>
        Process.Start(new ProcessStartInfo(WslExe, $"-d {distro} --cd ~") { UseShellExecute = true });

    /// <summary>Espera um processo sumir da distro. Retorna false se ainda existir após o timeout.</summary>
    public static async Task<bool> WaitProcessExitAsync(string distro, string name, TimeSpan timeout)
    {
        var end = DateTime.Now + timeout;
        while (DateTime.Now < end)
        {
            if (!await IsProcessRunningAsync(distro, name)) return true;
            await Task.Delay(1000);
        }
        return false;
    }
}
