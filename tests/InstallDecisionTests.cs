using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>O que fazer ao rodar uma cópia fora da pasta de instalação, conforme o que já está instalado.</summary>
public class InstallDecisionTests
{
    static readonly Version V181 = new(1, 8, 1), V190 = new(1, 9, 0);

    [Fact] public void Nada_instalado_instala() => Assert.Equal(InstallKind.Fresh, InstallDecision.Decide(V190, installed: null));
    [Fact] public void Instalada_mais_velha_atualiza() => Assert.Equal(InstallKind.Upgrade, InstallDecision.Decide(V190, V181));
    [Fact] public void Mesma_versao_reinstala() => Assert.Equal(InstallKind.Reinstall, InstallDecision.Decide(V190, new Version(1, 9, 0, 0)));
    [Fact] public void Instalada_mais_nova_nao_volta_versao() => Assert.Equal(InstallKind.InstalledIsNewer, InstallDecision.Decide(V181, V190));

    [Fact]
    public void Titulo_da_janela_conforme_o_caso()
    {
        Assert.Equal("Instalar o WSL Sync Monitor", InstallDecision.Title(InstallKind.Fresh, V190, null));
        Assert.Equal("Atualizar a instalação (1.8.1 → 1.9.0)", InstallDecision.Title(InstallKind.Upgrade, V190, V181));
        Assert.Equal("Reinstalar o WSL Sync Monitor 1.9.0", InstallDecision.Title(InstallKind.Reinstall, V190, V190));
    }
}
