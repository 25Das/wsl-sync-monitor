using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>Quando ler o config.xml dentro da distro e o que fazer com a chave quando a API a recusa.</summary>
public class CredentialRulesTests
{
    static readonly DateTime Now = new(2026, 10, 22, 12, 0, 0);

    [Fact]
    public void Le_o_config_quando_falta_a_chave_e_a_distro_esta_em_uso()
    {
        Assert.True(CredentialRules.ShouldReadConfig(haveKey: false, lastRead: default, Now, allowDistroCommands: true));
    }

    [Fact]
    public void Nao_le_nada_na_distro_liberada_mesmo_sem_chave()
    {
        Assert.False(CredentialRules.ShouldReadConfig(haveKey: false, lastRead: default, Now, allowDistroCommands: false));
    }

    [Fact]
    public void Sem_chave_rele_no_maximo_uma_vez_por_minuto()
    {
        Assert.False(CredentialRules.ShouldReadConfig(false, Now.AddSeconds(-30), Now, true));
        Assert.True(CredentialRules.ShouldReadConfig(false, Now.AddSeconds(-61), Now, true));
    }

    [Fact]
    public void Com_chave_nao_le_de_novo()
    {
        Assert.False(CredentialRules.ShouldReadConfig(haveKey: true, lastRead: default, Now, allowDistroCommands: true));
    }

    [Fact]
    public void Recusa_da_API_com_a_distro_em_uso_descarta_a_chave_para_reler()
    {
        Assert.True(CredentialRules.DiscardKeyOnAuthError(allowDistroCommands: true));
    }

    [Fact]
    public void Recusa_da_API_com_a_distro_liberada_mantem_a_chave()
    {
        // sem poder reler (nada roda na distro liberada), apagar a chave deixaria o app sem nenhuma
        Assert.False(CredentialRules.DiscardKeyOnAuthError(allowDistroCommands: false));
    }
}
