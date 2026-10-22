using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>Notificação só na busca automática com painel e menu fechados; quem buscou pelo painel ou pelo menu vê o resultado (e atualiza) ali mesmo.</summary>
public class UpdateNoticeTests
{
    static readonly Version V = new(1, 10, 2);

    [Fact]
    public void Busca_automatica_avisa_uma_vez_por_versao()
    {
        Assert.True(UpdateNotice.ShouldNotify(V, alreadyNotified: null, uiOpen: false));
        Assert.False(UpdateNotice.ShouldNotify(V, alreadyNotified: V, uiOpen: false));
        Assert.True(UpdateNotice.ShouldNotify(V, alreadyNotified: new Version(1, 10, 1), uiOpen: false));
    }

    [Fact]
    public void Botao_com_painel_ou_menu_aberto_nao_abre_notificacao() =>
        Assert.False(UpdateNotice.ShouldNotify(V, alreadyNotified: null, uiOpen: true));

    [Fact]
    public void Botao_cujo_resultado_chega_com_tudo_fechado_avisa_senao_ninguem_ve() =>
        Assert.True(UpdateNotice.ShouldNotify(V, alreadyNotified: null, uiOpen: false));

    [Fact]
    public void Busca_automatica_com_painel_ou_menu_aberto_nao_notifica_quem_mostra_e_ele() =>
        Assert.False(UpdateNotice.ShouldNotify(V, alreadyNotified: null, uiOpen: true));

    [Fact]
    public void Sem_versao_nova_nunca_avisa() =>
        Assert.False(UpdateNotice.ShouldNotify(null, alreadyNotified: null, uiOpen: false));

    [Fact]
    public void Item_do_menu_mostra_o_resultado_da_ultima_busca()
    {
        var at = new DateTime(2026, 10, 22, 14, 5, 0);
        Assert.Equal("Verificar atualizações", UpdateNotice.MenuLabel(UpdateState.NotChecked));
        Assert.Equal("Verificando atualizações…", UpdateNotice.MenuLabel(UpdateState.Checking));
        Assert.Equal("Verificar atualizações (em dia às 14:05)", UpdateNotice.MenuLabel(UpdateState.UpToDate(at)));
        Assert.Equal("Verificar atualizações (a última falhou)", UpdateNotice.MenuLabel(UpdateState.Failed("x")));
        // achou com o menu aberto: o próprio item vira o botão de atualizar
        Assert.Equal("Atualizar para a versão 1.10.2", UpdateNotice.MenuLabel(UpdateState.Found(V, at)));
    }
}
