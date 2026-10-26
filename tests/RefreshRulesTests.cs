using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>De quanto em quanto tempo o estado completo é lido (WSL e Syncthing).</summary>
public class RefreshRulesTests
{
    static readonly TimeSpan Base = TimeSpan.FromSeconds(5);

    [Fact]
    public void Sem_o_fluxo_de_eventos_le_no_intervalo_configurado() =>
        Assert.Equal(Base, RefreshRules.Interval(5, eventsLive: false, panelOpen: false, guardWatching: false));

    [Fact]
    public void Com_o_fluxo_conectado_e_o_painel_fechado_le_bem_menos()
    {
        // as mudanças do Syncthing chegam pelos eventos; se ele parar, o fluxo cai e a leitura é feita na hora
        Assert.Equal(TimeSpan.FromSeconds(15), RefreshRules.Interval(5, eventsLive: true, panelOpen: false, guardWatching: false));
        Assert.Equal(TimeSpan.FromSeconds(30), RefreshRules.Interval(10, eventsLive: true, panelOpen: false, guardWatching: false));
    }

    [Fact]
    public void Painel_aberto_mostra_velocidades_e_distros_no_ritmo_normal() =>
        Assert.Equal(Base, RefreshRules.Interval(5, eventsLive: true, panelOpen: true, guardWatching: false));

    [Fact]
    public void Proteção_esperando_algo_que_so_a_leitura_ve_nao_atrasa() =>
        Assert.Equal(Base, RefreshRules.Interval(5, eventsLive: true, panelOpen: false, guardWatching: true));

    [Theory]
    [InlineData(GuardState.Off, 0, true, false, false)]
    [InlineData(GuardState.InUse, 0, true, false, false)]   // sessões observadas: o fechamento avisa na hora
    [InlineData(GuardState.InUse, 0, false, false, true)]   // alguma sessão não pôde ser observada (ex.: terminal como admin)
    [InlineData(GuardState.InUse, 1, true, false, true)]    // contando leituras seguidas sem sessões
    [InlineData(GuardState.KeptAlive, 0, true, true, true)] // "Manter ligada" por tempo: o fim só a leitura vê
    [InlineData(GuardState.KeptAlive, 0, true, false, false)]
    public void Quando_a_protecao_depende_da_leitura(GuardState state, int idleTicks, bool sessionsWatched, bool timedKeepAlive, bool expected) =>
        Assert.Equal(expected, RefreshRules.GuardWatching(state, idleTicks, sessionsWatched, timedKeepAlive));

    [Fact]
    public void Processo_que_segura_a_distro_terminou_depois_de_um_tempo_le_na_hora() =>
        Assert.True(RefreshRules.RefreshOnKeepAliveExit(lived: TimeSpan.FromMinutes(3)));

    [Fact]
    public void Processo_que_segura_a_distro_terminou_logo_ao_iniciar_espera_a_leitura_periodica()
    {
        // a distro não sobe (quebrada, sendo removida): ler na hora religaria e cairia de novo, sem parar
        Assert.False(RefreshRules.RefreshOnKeepAliveExit(lived: TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Intervalo_minimo_de_2_s() =>
        Assert.Equal(TimeSpan.FromSeconds(2), RefreshRules.Interval(0, eventsLive: false, panelOpen: false, guardWatching: false));
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
    public void Sem_resposta_e_sem_saber_se_o_processo_roda_tambem_sonda() =>
        // distro liberada para desligar: o app não executa comandos nela e o estado fica desconhecido
        Assert.True(RefreshRules.ShouldProbe(Snap(true, StState.Running), Snap(true, StState.Unknown)));

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
