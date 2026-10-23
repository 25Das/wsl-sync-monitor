using System.Diagnostics;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Roda <see cref="SyncthingScripts.ListConfigs"/> com o sh do Linux num HOME falso e confere quais
/// config.xml são listados, e em que ordem (a ordem define qual chave da API é testada primeiro).
/// </summary>
public sealed class SyncthingScriptsTests : IDisposable
{
    readonly string _root = Directory.CreateTempSubdirectory("wslsync-tests").FullName;
    string Home => Path.Combine(_root, "home");

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string WriteConfig(string relative)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<configuration><gui><apikey>KEY</apikey></gui></configuration>");
        return path;
    }

    List<string> ListedFiles(string script)
    {
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        psi.Environment["HOME"] = Home;
        psi.Environment.Remove("XDG_STATE_HOME");
        psi.Environment.Remove("XDG_CONFIG_HOME");
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && error.Length == 0, $"sh falhou ({p.ExitCode}): {error}");
        return output.Split('\n').Where(l => l.StartsWith("@@FILE ", StringComparison.Ordinal)).Select(l => l["@@FILE ".Length..]).ToList();
    }

    [ShFact]
    public void Lista_o_config_no_local_padrao()
    {
        var state = WriteConfig("home/.local/state/syncthing/config.xml");

        Assert.Equal([state], ListedFiles(SyncthingScripts.ListConfigs("")));
    }

    [ShFact]
    public void Lista_o_local_novo_antes_do_antigo()
    {
        var state = WriteConfig("home/.local/state/syncthing/config.xml");
        var legacy = WriteConfig("home/.config/syncthing/config.xml");

        Assert.Equal([state, legacy], ListedFiles(SyncthingScripts.ListConfigs("")));
    }

    [ShFact]
    public void Com_caminho_definido_lista_so_ele()
    {
        WriteConfig("home/.local/state/syncthing/config.xml");
        var custom = WriteConfig("custom/config.xml");

        Assert.Equal([custom], ListedFiles(SyncthingScripts.ListConfigs(custom)));
    }

    [ShFact]
    public void Caminho_definido_com_aspas_simples()
    {
        var custom = WriteConfig("it's here/config.xml");

        Assert.Equal([custom], ListedFiles(SyncthingScripts.ListConfigs(custom)));
    }

    [ShFact]
    public void Nao_repete_o_mesmo_arquivo_por_um_link()
    {
        var legacy = WriteConfig("home/.config/syncthing/config.xml");
        var link = Path.Combine(_root, "link.xml");
        File.CreateSymbolicLink(link, legacy);

        Assert.Equal([legacy], ListedFiles(SyncthingScripts.ListConfigs(link) + $"\nemit '{legacy}'"));
    }

    [ShFact]
    public void Sem_nenhum_config_nao_lista_nada_e_nao_falha()
    {
        Directory.CreateDirectory(Home);

        Assert.Empty(ListedFiles(SyncthingScripts.ListConfigs("")));
    }

    [ShFact]
    public void Nao_procura_pelo_processo_em_execucao_so_nos_locais_padrao()
    {
        var state = WriteConfig("home/.local/state/syncthing/config.xml");
        var procHome = Path.GetDirectoryName(WriteConfig("prochome/config.xml"))!;

        // um "syncthing" falso em execução com --home: o script não deve olhar o processo
        var fake = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName, "syncthing");
        File.WriteAllText(fake, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var psi = new ProcessStartInfo(fake);
        psi.ArgumentList.Add("serve");
        psi.ArgumentList.Add($"--home={procHome}");
        using var proc = Process.Start(psi)!;
        try
        {
            Thread.Sleep(300);
            Assert.Equal([state], ListedFiles(SyncthingScripts.ListConfigs("")));
        }
        finally { proc.Kill(true); proc.WaitForExit(); }
    }
}

public sealed class LineEndingTests
{
    static (int Exit, string Output, string Error) RunSh(string script)
    {
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(script);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd();
        var error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output, error);
    }

    // Compilado no Windows (git com autocrlf), o texto dos scripts no código vem com CRLF.
    const string ScriptWithCrlf = "if true; then\r\n  echo ok\r\nfi\r\n";

    [ShFact]
    public void Script_com_CRLF_falha_no_sh() // documenta o defeito das versões 1.5.1 a 1.5.4
    {
        Assert.NotEqual("ok\n", RunSh(ScriptWithCrlf).Output);
    }

    [ShFact]
    public void ForSh_converte_CRLF_e_o_sh_executa()
    {
        var script = SyncthingScripts.ForSh(ScriptWithCrlf);

        Assert.DoesNotContain('\r', script);
        var (exit, output, error) = RunSh(script);
        Assert.Equal((0, "ok\n", ""), (exit, output, error));
    }

    [Fact]
    public void ListConfigs_nunca_tem_CR()
    {
        Assert.DoesNotContain('\r', SyncthingScripts.ListConfigs(""));
        Assert.DoesNotContain('\r', SyncthingScripts.ListConfigs("/x/config.xml"));
    }
}

/// <summary>
/// Processo que mantém a distro ligada e atende pedidos por arquivos-sinal (sync, parar o Syncthing): no
/// desligamento do Windows não dá para criar processos, mas dá para criar arquivos em \\wsl.localhost.
/// </summary>
public sealed class KeepAliveScriptTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "wsm-ka-" + Guid.NewGuid().ToString("N")[..8]);
    Process? _p;

    public void Dispose()
    {
        try { _p?.Kill(true); } catch { }
        try { Directory.Delete(_dir, true); } catch { }
    }

    // nome de processo só deste teste: o script nunca pode parar o Syncthing de quem roda os testes
    readonly string _target = "wsmt" + Random.Shared.Next(10000, 99999);

    void Start()
    {
        var psi = new ProcessStartInfo("sh");
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(SyncthingScripts.ForSh(SyncthingScripts.KeepAlive));
        psi.ArgumentList.Add(SyncthingScripts.KeepAliveMarker);
        psi.ArgumentList.Add(_dir);
        psi.ArgumentList.Add(_target);
        _p = Process.Start(psi)!;
        Assert.True(WaitFile(Path.Combine(_dir, "ready"))); // só depois da limpeza inicial
    }

    static bool WaitFile(string path)
    {
        for (int i = 0; i < 300; i++) { if (File.Exists(path)) return true; Thread.Sleep(100); } // sync no CI pode demorar
        return false;
    }

    [ShFact]
    public void Atende_o_pedido_de_sync_e_continua_rodando()
    {
        Start();
        File.WriteAllText(Path.Combine(_dir, "sync"), "");
        Assert.True(WaitFile(Path.Combine(_dir, "synced")));
        Assert.False(File.Exists(Path.Combine(_dir, "sync"))); // pedido consumido
        Assert.False(_p!.HasExited);
    }

    [ShFact]
    public void Pedido_de_parar_o_syncthing_para_so_o_processo_pedido()
    {
        // um processo com o nome pedido (cópia do sleep): é ele, e só ele, que o pedido para
        var bin = Path.Combine(Path.GetTempPath(), _target);
        File.Copy("/bin/sleep", bin, overwrite: true);
        File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
        using var target = Process.Start(bin, "60");
        try
        {
            Start();
            File.WriteAllText(Path.Combine(_dir, "stop-syncthing"), "");
            Assert.True(WaitFile(Path.Combine(_dir, "stopped")));
            Assert.True(target.WaitForExit(5000));
        }
        finally
        {
            try { target.Kill(); } catch { }
            File.Delete(bin);
        }
    }

    [ShFact]
    public void Pedido_de_parar_o_syncthing_sem_ele_rodando_responde_logo()
    {
        Start();
        File.WriteAllText(Path.Combine(_dir, "stop-syncthing"), "");
        Assert.True(WaitFile(Path.Combine(_dir, "stopped")));
    }

    [Fact]
    public void Script_cabe_numa_linha_de_comando_sem_aspas_duplas()
    {
        Assert.DoesNotContain('"', SyncthingScripts.KeepAlive);
        Assert.DoesNotContain('\n', SyncthingScripts.ForSh(SyncthingScripts.KeepAlive).Trim());
    }
}
