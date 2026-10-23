using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>De quanto em quanto tempo o estado completo é lido (WSL e Syncthing).</summary>
public class RefreshRulesTests
{
    static readonly TimeSpan Base = TimeSpan.FromSeconds(5);

    [Fact]
    public void Sem_o_fluxo_de_eventos_le_no_intervalo_configurado() =>
        Assert.Equal(Base, RefreshRules.Interval(5, eventsLive: false, panelOpen: false, guardCounting: false));

    [Fact]
    public void Com_o_fluxo_conectado_e_o_painel_fechado_le_bem_menos()
    {
        // as mudanças do Syncthing chegam pelos eventos; se ele parar, o fluxo cai e a leitura é feita na hora
        Assert.Equal(TimeSpan.FromSeconds(15), RefreshRules.Interval(5, eventsLive: true, panelOpen: false, guardCounting: false));
        Assert.Equal(TimeSpan.FromSeconds(30), RefreshRules.Interval(10, eventsLive: true, panelOpen: false, guardCounting: false));
    }

    [Fact]
    public void Painel_aberto_mostra_velocidades_e_distros_no_ritmo_normal() =>
        Assert.Equal(Base, RefreshRules.Interval(5, eventsLive: true, panelOpen: true, guardCounting: false));

    [Fact]
    public void Proteção_contando_atualizacoes_sem_sessoes_nao_atrasa() =>
        Assert.Equal(Base, RefreshRules.Interval(5, eventsLive: true, panelOpen: false, guardCounting: true));

    [Fact]
    public void Intervalo_minimo_de_2_s() =>
        Assert.Equal(TimeSpan.FromSeconds(2), RefreshRules.Interval(0, eventsLive: false, panelOpen: false, guardCounting: false));
}
