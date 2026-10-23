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

    /// <summary>
    /// Conta as sessões abertas na distro por outros programas (VS Code, dev containers, terminais):
    /// processos wsl.exe do Windows que não foram criados por este app e cuja linha de comando aponta para a distro.
    /// </summary>
    public static int CountExternalSessions(string distro, string? defaultDistro) => ExternalSessions(distro, defaultDistro).Count;

    /// <summary>Os processos das sessões contadas por <see cref="CountExternalSessions"/> (para saber na hora quando fecham).</summary>
    public static List<int> ExternalSessions(string distro, string? defaultDistro)
    {
        var sessions = new List<int>();
        foreach (var p in Process.GetProcessesByName("wsl"))
        {
            using (p)
            {
                bool own;
                lock (OwnPids) own = OwnPids.Contains(p.Id) || _keepAlive.Values.Any(k => k.Id == p.Id && !k.HasExited);
                if (own) continue;
                var cmd = Native.GetCommandLine(p.Id);
                if (WslCommandLine.IsKeepAlive(cmd, out _)) continue; // de outra instância do app (ver AdoptKeepAlives)
                var target = WslCommandLine.SessionTarget(cmd, defaultDistro);
                if (target == null || target.Equals(distro, StringComparison.OrdinalIgnoreCase)) sessions.Add(p.Id);
            }
        }
        return sessions;
    }

    public static bool IsInstalled => File.Exists(WslExe) || WslExe == "wsl.exe";

    // ---- Windows encerrando a sessão ----

    static volatile bool _sessionEnding;

    /// <summary>
    /// O Windows está encerrando a sessão (desligar, reiniciar, sair). A partir daí nenhum processo novo consegue
    /// iniciar: o wsl.exe falha com "0xc0000142", e a janela de erro que o Windows mostra BLOQUEIA o desligamento
    /// até alguém fechá-la. Por isso nada mais executa o wsl.exe; a sequência de desligamento usa só a API do
    /// Syncthing e os pedidos ao processo de manutenção (<see cref="RequestInDistroAsync"/>), que já está rodando.
    /// </summary>
    public static bool SessionEnding
    {
        get => _sessionEnding;
        set => _sessionEnding = value;
    }

    static bool Blocked => _sessionEnding;

    public sealed class SessionEndingException() : InvalidOperationException("O Windows está encerrando a sessão.");

    public static async Task<Result> RunAsync(IEnumerable<string> args, int timeoutMs = 10000)
    {
        var argList = args.ToList();
        if (Blocked) throw new SessionEndingException();
        var described = WslExitCodes.DescribeArgs(argList);
        var result = await RunCoreAsync(argList, timeoutMs, described);
        // códigos diferentes de zero às vezes são normais (ex.: nenhuma distro ligada): registra quando muda
        if (WslExitCodes.Describe(result.ExitCode) is { } code)
            Log.Failure("wsl", "wsl " + described, $"wsl.exe {described} → {code}" +
                (result.Error.Length > 0 ? ": " + result.Error.Split('\n')[0].Trim() : ""));
        else Log.Recovered("wsl", "wsl " + described, $"wsl.exe {described} voltou a funcionar");
        return result;
    }

    static async Task<Result> RunCoreAsync(List<string> args, int timeoutMs, string described)
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
        try { p.Start(); }
        catch (Exception ex)
        {
            Log.Warn("wsl", $"wsl.exe {described} não iniciou", ex);
            throw;
        }
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
            try { p.Kill(true); } catch { /* já saiu */ }
            Log.Warn("wsl", $"wsl.exe {described} não respondeu em {timeoutMs / 1000} s");
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
            if (!int.TryParse(parts[^1], out var ver)) ver = 0; // versão desconhecida
            var name = parts[0];
            list.Add(new DistroInfo(name, runningSet.Contains(name), ver, isDefault));
        }
        return list;
    }

    /// <summary>Executa um script sh na distro. Atenção: inicia a distro se estiver parada.</summary>
    public static Task<Result> ShAsync(string distro, string script, int timeoutMs = 10000) =>
        RunAsync(new[] { "-d", distro, "--exec", "sh", "-c", SyncthingScripts.ForSh(script) }, timeoutMs);

    public static async Task<bool> IsProcessRunningAsync(string distro, string name) =>
        (await ShAsync(distro, ProcessCheck(name))).Output.Trim() == "yes";

    /// <summary>Como <see cref="IsProcessRunningAsync"/>, mas se não der para saber (falha, sem resposta), considera rodando.</summary>
    public static async Task<bool> ProcessMayBeRunningAsync(string distro, string name)
    {
        try { return SyncthingParsers.ProcessMayBeRunning((await ShAsync(distro, ProcessCheck(name))).Output); }
        catch { return true; }
    }

    static string ProcessCheck(string name) => $"pgrep -x {name} >/dev/null 2>&1 && echo yes || echo no";

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

    // processos de manutenção (SyncthingScripts.KeepAlive) que mantêm ligada cada distro segurada pelo app
    static readonly Dictionary<string, Native.DetachedProcess> _keepAlive = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Inicia a distro e mantém um processo oculto vivo para que ela não desligue por inatividade.</summary>
    public static void StartDistro(string distro)
    {
        if (Blocked) return; // Windows encerrando a sessão: não liga nada (e o wsl.exe falharia)
        if (_keepAlive.TryGetValue(distro, out var p))
        {
            if (!p.HasExited) return;
            p.Dispose();
        }
        // Process.Start herda os handles deste processo (inclusive o do próprio .exe); como este wsl.exe
        // vive enquanto o WSL estiver ligado, o executável ficaria travado mesmo após fechar o app.
        // Por isso é criado via CreateProcess sem herança de handles.
        // mantém a distro ligada e atende pedidos (sync, parar o Syncthing) mesmo durante o desligamento do Windows
        _keepAlive[distro] = Native.DetachedProcess.Start(
            $"\"{WslExe}\" -d {distro} --exec sh -c \"{SyncthingScripts.ForSh(SyncthingScripts.KeepAlive)}\" {SyncthingScripts.KeepAliveMarker}");
        Log.Info("proteção", $"{distro} mantida ligada pelo app");
    }

    /// <summary>
    /// Assume os processos que seguram distros deixados por uma instância anterior do app (por exemplo, a que
    /// acabou de se atualizar), para continuar protegendo a sincronização sem soltar a distro no meio.
    /// </summary>
    public static void AdoptKeepAlives()
    {
        foreach (var p in Process.GetProcessesByName("wsl"))
        {
            using (p)
            {
                if (!WslCommandLine.IsKeepAlive(Native.GetCommandLine(p.Id), out var distro)) continue;
                if (IsHolding(distro)) continue;
                var adopted = Native.DetachedProcess.Open(p.Id);
                if (adopted != null) _keepAlive[distro] = adopted;
            }
        }
    }

    /// <summary>
    /// Pede algo ao processo de manutenção da distro criando um arquivo-sinal pela rede do WSL (\\wsl.localhost),
    /// sem iniciar processo nenhum (funciona durante o desligamento do Windows). Espera o arquivo de resposta.
    /// false = sem processo de manutenção com pedidos (distro liberada, ou versão antiga) ou sem resposta no prazo.
    /// </summary>
    public static async Task<bool> RequestInDistroAsync(string distro, string request, string reply, TimeSpan timeout)
    {
        if (!IsHolding(distro)) return false;
        var work = Task.Run(async () =>
        {
            foreach (var root in new[] { @"\\wsl.localhost\", @"\\wsl$\" })
            {
                var dir = root + distro + SyncthingScripts.KeepAliveDir.Replace('/', '\\');
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    File.Delete(Path.Combine(dir, reply));
                    await File.WriteAllTextAsync(Path.Combine(dir, request), "");
                    var end = DateTime.Now + timeout;
                    while (DateTime.Now < end)
                    {
                        if (File.Exists(Path.Combine(dir, reply))) return true;
                        await Task.Delay(250);
                    }
                    return false;
                }
                catch (Exception ex) { Log.Warn("wsl", $"pedido \"{request}\" a {distro} por {root}", ex); }
            }
            return false;
        });
        // o acesso à rede do WSL pode travar: nunca espera além do prazo
        try { return await work.WaitAsync(timeout + TimeSpan.FromSeconds(5)); }
        catch (TimeoutException) { return false; }
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
            try { _keepAlive[name].Kill(); } catch { /* já tinha saído */ }
            _keepAlive[name].Dispose();
            _keepAlive.Remove(name);
            Log.Info("proteção", $"{name} liberada (o WSL pode desligar por ociosidade)");
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

    // ---- informações e operações (menus de WSL e pastas) ----

    static readonly Dictionary<string, string> _homes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pasta home do usuário padrão da distro (lida uma vez; executa um comando na distro).</summary>
    public static async Task<string> GetHomeAsync(string distro)
    {
        if (_homes.TryGetValue(distro, out var home)) return home;
        var r = await ShAsync(distro, SyncthingScripts.Home);
        home = r.ExitCode == 0 && r.Output.StartsWith('/') ? r.Output.Trim() : "/root";
        return _homes[distro] = home;
    }

    /// <summary>Espaço total e livre do disco da distro (executa "df" nela).</summary>
    public static async Task<(long Total, long Free)?> GetDiskSpaceAsync(string distro) =>
        SyncthingParsers.Df((await ShAsync(distro, SyncthingScripts.DiskUsage)).Output);

    /// <summary>Arquivo de disco (ext4.vhdx) da distro no Windows, pelo registro do WSL; não executa nada na distro.</summary>
    public static string? GetVhdPath(string distro)
    {
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            foreach (var id in lxss?.GetSubKeyNames() ?? [])
            {
                using var k = lxss!.OpenSubKey(id);
                if (!string.Equals(k?.GetValue("DistributionName") as string, distro, StringComparison.OrdinalIgnoreCase)) continue;
                var vhd = Path.Combine(WslPaths.NormalizeWindowsPath(k!.GetValue("BasePath") as string ?? ""), "ext4.vhdx");
                return File.Exists(vhd) ? vhd : null;
            }
        }
        catch { }
        return null;
    }

    public static long? GetVhdSize(string? vhdPath) => vhdPath != null && File.Exists(vhdPath) ? new FileInfo(vhdPath).Length : null;

    /// <summary>Espaço livre na unidade do Windows onde fica o disco da distro (o limite real de crescimento dele).</summary>
    public static (long Free, string Drive)? GetHostFreeSpace(string? vhdPath)
    {
        try
        {
            if (vhdPath == null) return null;
            var drive = new DriveInfo(Path.GetPathRoot(vhdPath)!);
            return (drive.AvailableFreeSpace, drive.Name.TrimEnd('\\'));
        }
        catch { return null; }
    }

    /// <summary>Memória ocupada pela máquina virtual do WSL (processo vmmemWSL/vmmem), se o Windows deixar ler.</summary>
    public static long? GetVmMemory()
    {
        foreach (var name in new[] { "vmmemWSL", "vmmem" })
            foreach (var p in Process.GetProcessesByName(name))
                using (p)
                    try { return p.WorkingSet64; } catch { }
        return null;
    }

    /// <summary>Copia a distro para um arquivo .tar.gz ("wsl --export").</summary>
    public static async Task ExportAsync(string distro, string file)
    {
        var r = await RunAsync(new[] { "--export", distro, file, "--format", "tar.gz" }, (int)TimeSpan.FromHours(2).TotalMilliseconds);
        if (r.ExitCode != 0) throw new InvalidOperationException(FirstLine(r.Output, r.Error, "Falha ao exportar a distro."));
    }

    /// <summary>"wsl --update" (com o WSL desligado). Retorna a mensagem do wsl.exe.</summary>
    public static async Task<string> UpdateWslAsync()
    {
        var r = await RunAsync(new[] { "--update" }, (int)TimeSpan.FromMinutes(20).TotalMilliseconds);
        if (r.ExitCode != 0) throw new InvalidOperationException(FirstLine(r.Output, r.Error, "Falha ao atualizar o WSL."));
        return r.Output.Trim();
    }

    static string FirstLine(string output, string error, string fallback) =>
        (output + "\n" + error).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? fallback;

    /// <summary>Abre um terminal na distro, numa pasta (padrão: home).</summary>
    public static void OpenTerminal(string distro, string linuxPath = "~") =>
        Process.Start(new ProcessStartInfo(WslExe, $"-d {distro} --cd \"{linuxPath}\"") { UseShellExecute = true });

    /// <summary>Abre um caminho no Explorer (pasta ou arquivo).</summary>
    public static void OpenInExplorer(string windowsPath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{windowsPath}\"") { UseShellExecute = true });


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
