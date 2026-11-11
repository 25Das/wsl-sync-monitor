using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// O Windows pede para encerrar a sessão (WM_QUERYENDSESSION), o app segura enquanto a sequência segura roda, e o
/// desligamento pode ser cancelado no meio (WM_ENDSESSION sem encerrar, entregue enquanto o app ainda espera).
/// </summary>
public class ShutdownProgressTests
{
    [Fact]
    public void Roda_a_sequencia_uma_vez_por_desligamento()
    {
        var p = new ShutdownProgress();
        Assert.True(p.ShouldRun(needed: true));
        p.Started();
        Assert.False(p.Finished());
        Assert.False(p.ShouldRun(needed: true)); // o Windows pergunta de novo: já foi feita
    }

    [Fact]
    public void Sem_necessidade_nao_roda()
    {
        Assert.False(new ShutdownProgress().ShouldRun(needed: false));
    }

    [Fact]
    public void Pedido_repetido_enquanto_roda_nao_inicia_outra()
    {
        var p = new ShutdownProgress();
        p.Started();
        Assert.False(p.ShouldRun(needed: true));
    }

    [Fact]
    public void Cancelado_depois_de_concluir_protege_o_proximo_desligamento()
    {
        var p = new ShutdownProgress();
        p.Started();
        p.Finished();
        Assert.False(p.Cancel()); // nada rodando para interromper
        Assert.True(p.ShouldRun(needed: true));
    }

    [Fact]
    public void Cancelado_no_meio_interrompe_e_protege_o_proximo_desligamento()
    {
        // caso real: cancelado 3 s depois de começar; ao terminar, a sequência marcava "feita" e o desligamento
        // seguinte (uma hora depois) saiu sem esperar a sincronização
        var p = new ShutdownProgress();
        p.Started();
        Assert.True(p.Cancel());     // interrompe a sequência em andamento
        Assert.True(p.Finished());   // terminou cancelada: quem chama desfaz o que precisar
        Assert.True(p.ShouldRun(needed: true));
    }

    [Fact]
    public void Novo_desligamento_depois_de_um_cancelado_comeca_do_zero()
    {
        var p = new ShutdownProgress();
        p.Started();
        p.Cancel();
        p.Finished();
        p.Started();
        Assert.False(p.Finished());
        Assert.False(p.ShouldRun(needed: true));
    }
}
