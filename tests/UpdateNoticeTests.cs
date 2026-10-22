using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>Notificação de versão nova só na busca automática; pelos botões o resultado aparece no painel e no menu.</summary>
public class UpdateNoticeTests
{
    static readonly Version V = new(1, 10, 2);

    [Fact]
    public void Busca_automatica_avisa_uma_vez_por_versao()
    {
        Assert.True(UpdateNotice.ShouldNotify(UpdateCheck.Automatic, V, alreadyNotified: null));
        Assert.False(UpdateNotice.ShouldNotify(UpdateCheck.Automatic, V, alreadyNotified: V));
        Assert.True(UpdateNotice.ShouldNotify(UpdateCheck.Automatic, V, alreadyNotified: new Version(1, 10, 1)));
    }

    [Fact]
    public void Botao_nao_abre_notificacao() =>
        Assert.False(UpdateNotice.ShouldNotify(UpdateCheck.Button, V, alreadyNotified: null));

    [Fact]
    public void Sem_versao_nova_nunca_avisa() =>
        Assert.False(UpdateNotice.ShouldNotify(UpdateCheck.Automatic, null, alreadyNotified: null));

    [Fact]
    public void Item_do_menu_mostra_o_resultado_da_ultima_busca()
    {
        var at = new DateTime(2026, 10, 22, 14, 5, 0);
        Assert.Equal("Verificar atualizações", UpdateNotice.MenuLabel(UpdateState.NotChecked));
        Assert.Equal("Verificando atualizações…", UpdateNotice.MenuLabel(UpdateState.Checking));
        Assert.Equal("Verificar atualizações (em dia às 14:05)", UpdateNotice.MenuLabel(UpdateState.UpToDate(at)));
        Assert.Equal("Verificar atualizações (a última falhou)", UpdateNotice.MenuLabel(UpdateState.Failed("x")));
        Assert.Equal("Verificar atualizações", UpdateNotice.MenuLabel(UpdateState.Found(V, at))); // "Atualizar para" já aparece no topo
    }
}
