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

/// <summary>Quando vale sondar o Syncthing a cada segundo (ele está para responder) em vez de esperar a próxima leitura.</summary>
public class ProbeRulesTests
{
    static Snapshot Snap(bool distroRunning, StState st)
    {
        var s = new Snapshot { St = st, Target = new DistroInfo("Ubuntu", distroRunning, 2, true) };
        s.Distros.Add(s.Target);
        return s;
    }

    [Fact]
    public void Distro_acabou_de_ligar_e_o_Syncthing_ainda_nao_responde() =>
        Assert.True(RefreshRules.ShouldProbe(Snap(false, StState.DistroStopped), Snap(true, StState.NotRunning)));

    [Fact]
    public void Syncthing_parou_ou_esta_reiniciando() =>
        Assert.True(RefreshRules.ShouldProbe(Snap(true, StState.Running), Snap(true, StState.Unreachable)));

    [Fact]
    public void Ao_abrir_o_app_com_a_distro_ligada_e_o_Syncthing_subindo() =>
        Assert.True(RefreshRules.ShouldProbe(null, Snap(true, StState.NotRunning)));

    [Fact]
    public void Continua_sem_Syncthing_nao_sonda_de_novo() =>
        Assert.False(RefreshRules.ShouldProbe(Snap(true, StState.NotRunning), Snap(true, StState.NotRunning))); // ele não usa o Syncthing nesta distro

    [Theory]
    [InlineData(false, StState.DistroStopped)] // distro desligada: nada a esperar
    [InlineData(true, StState.Running)]
    [InlineData(true, StState.AuthError)]      // respondeu: o problema é a chave
    public void Nao_sonda(bool running, StState st) =>
        Assert.False(RefreshRules.ShouldProbe(Snap(false, StState.DistroStopped), Snap(running, st)));
}
