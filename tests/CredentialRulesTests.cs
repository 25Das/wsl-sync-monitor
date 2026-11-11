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

    // o usuário trocou a porta (ou ligou o HTTPS) da interface do Syncthing: ele roda, mas não responde no endereço
    // lido antes. Antes, o app ficava no endereço antigo até trocar de distro


    [Fact]
    public void Syncthing_rodando_sem_responder_rele_o_endereco()
    {
        Assert.True(CredentialRules.RereadAfterUnreachable(processRunning: true, lastRead: Now.AddMinutes(-5), Now, allowDistroCommands: true));
    }

    [Fact]
    public void Releitura_do_endereco_no_maximo_uma_vez_por_minuto()
    {
        // ele pode só estar subindo: cada releitura executa um comando na distro
        Assert.False(CredentialRules.RereadAfterUnreachable(true, Now.AddSeconds(-20), Now, true));
    }

    [Fact]
    public void Sem_o_processo_ou_com_a_distro_liberada_nao_rele()
    {
        Assert.False(CredentialRules.RereadAfterUnreachable(processRunning: false, Now.AddMinutes(-5), Now, true));
        Assert.False(CredentialRules.RereadAfterUnreachable(processRunning: true, Now.AddMinutes(-5), Now, allowDistroCommands: false));
    }
}
