using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class WslCommandLineTests
{
    const string Wsl = "\"C:\\Windows\\System32\\wsl.exe\"";

    [Theory]
    [InlineData(Wsl + " -d Ubuntu-24.04 --exec sleep infinity", "Ubuntu-24.04")]
    [InlineData("wsl.exe -d Ubuntu --exec sleep infinity", "Ubuntu")]
    public void Reconhece_o_processo_que_segura_a_distro(string cmd, string distro)
    {
        Assert.True(WslCommandLine.IsKeepAlive(cmd, out var d));
        Assert.Equal(distro, d);
    }

    [Theory]
    [InlineData(Wsl + " -d Ubuntu-24.04")]
    [InlineData(Wsl + " -d Ubuntu-24.04 --exec sleep 30")]
    [InlineData(Wsl + " -d Ubuntu-24.04 --exec sh -c \"sleep infinity\"")]
    [InlineData(Wsl + " --exec sleep infinity")] // sem distro explícita: não foi o app que criou
    public void Outros_comandos_nao_sao_o_processo_do_app(string cmd)
    {
        Assert.False(WslCommandLine.IsKeepAlive(cmd, out _));
    }

    [Theory]
    [InlineData(Wsl + " -d Ubuntu-24.04", "Ubuntu-24.04")]
    [InlineData(Wsl + " --distribution Ubuntu-24.04 -e bash", "Ubuntu-24.04")]
    [InlineData(Wsl, "Padrao")]                                   // sem -d: distro padrão
    [InlineData(Wsl + " -e sh -c \"echo -d Outra\"", "Padrao")]   // -d depois de -e é do comando Linux
    [InlineData(Wsl + " -l -v", "")]                              // só administração
    [InlineData(Wsl + " --shutdown", "")]
    public void Identifica_a_distro_de_uma_sessao(string cmd, string expected)
    {
        Assert.Equal(expected, WslCommandLine.SessionTarget(cmd, "Padrao"));
    }

    [Fact]
    public void Sem_linha_de_comando_ou_sem_distro_padrao_conta_como_sessao_desconhecida()
    {
        Assert.Null(WslCommandLine.SessionTarget(null, "Padrao"));
        Assert.Null(WslCommandLine.SessionTarget(Wsl, null));
    }

    [Fact]
    public void Reconhece_o_processo_de_manutencao_com_canal_de_pedidos()
    {
        var cmd = $"\"C:\\Windows\\System32\\wsl.exe\" -d Ubuntu-26.04 --exec sh -c \"{SyncthingScripts.KeepAlive}\" {SyncthingScripts.KeepAliveMarker}";
        Assert.True(WslCommandLine.IsKeepAlive(cmd, out var d));
        Assert.Equal("Ubuntu-26.04", d);
    }
}
