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
        return output.Split('\n').Where(l => l.StartsWith("@@FILE ")).Select(l => l["@@FILE ".Length..]).ToList();
    }

    [Fact]
    public void Lista_o_config_no_local_padrao()
    {
        var state = WriteConfig("home/.local/state/syncthing/config.xml");

        Assert.Equal([state], ListedFiles(SyncthingScripts.ListConfigs("")));
    }

    [Fact]
    public void Lista_o_local_novo_antes_do_antigo()
    {
        var state = WriteConfig("home/.local/state/syncthing/config.xml");
        var legacy = WriteConfig("home/.config/syncthing/config.xml");

        Assert.Equal([state, legacy], ListedFiles(SyncthingScripts.ListConfigs("")));
    }

    [Fact]
    public void Com_caminho_definido_lista_so_ele()
    {
        WriteConfig("home/.local/state/syncthing/config.xml");
        var custom = WriteConfig("custom/config.xml");

        Assert.Equal([custom], ListedFiles(SyncthingScripts.ListConfigs(custom)));
    }

    [Fact]
    public void Caminho_definido_com_aspas_simples()
    {
        var custom = WriteConfig("it's here/config.xml");

        Assert.Equal([custom], ListedFiles(SyncthingScripts.ListConfigs(custom)));
    }

    [Fact]
    public void Nao_repete_o_mesmo_arquivo_por_um_link()
    {
        var legacy = WriteConfig("home/.config/syncthing/config.xml");
        var link = Path.Combine(_root, "link.xml");
        File.CreateSymbolicLink(link, legacy);

        Assert.Equal([legacy], ListedFiles(SyncthingScripts.ListConfigs(link) + $"\nemit '{legacy}'"));
    }

    [Fact]
    public void Sem_nenhum_config_nao_lista_nada_e_nao_falha()
    {
        Directory.CreateDirectory(Home);

        Assert.Empty(ListedFiles(SyncthingScripts.ListConfigs("")));
    }

    [Theory]
    [InlineData(false)] // --home <dir>
    [InlineData(true)]  // --home=<dir>
    public void Lista_primeiro_o_config_do_processo_em_execucao(bool withEquals)
    {
        var state = WriteConfig("home/.local/state/syncthing/config.xml");
        var procConfig = WriteConfig("prochome/config.xml");
        var procHome = Path.GetDirectoryName(procConfig)!;

        // um "syncthing" falso em execução (o script procura o processo pelo nome)
        var fake = Path.Combine(Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName, "syncthing");
        File.WriteAllText(fake, "#!/bin/sh\nsleep 30\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var psi = new ProcessStartInfo(fake);
        psi.ArgumentList.Add("serve");
        if (withEquals) psi.ArgumentList.Add($"--home={procHome}");
        else { psi.ArgumentList.Add("--home"); psi.ArgumentList.Add(procHome); }
        using var proc = Process.Start(psi)!;
        try
        {
            Thread.Sleep(300);
            Assert.Equal([procConfig, state], ListedFiles(SyncthingScripts.ListConfigs("")));
        }
        finally { proc.Kill(true); proc.WaitForExit(); }
    }
}
