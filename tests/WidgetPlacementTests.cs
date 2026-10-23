using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Onde o widget fica na barra horizontal: perto da bandeja, como sempre; sem espaço ali (ícones demais), no
/// primeiro espaço livre do outro lado; sem espaço em lugar nenhum, no lugar de sempre.
/// </summary>
public class WidgetPlacementTests
{
    // barra de 0 a 1920; bandeja começa em 1700; widget de 150 px; folga 6
    static int Place(params (int, int)[] icons) => WidgetPlacement.ChooseX(0, 1700, icons, width: 150, gap: 6, offset: 0);

    [Fact]
    public void Com_espaco_fica_junto_da_bandeja() =>
        Assert.Equal(1700 - 150 - 6, Place((700, 1200)));

    [Fact]
    public void Sem_espaco_junto_da_bandeja_vai_para_o_outro_lado()
    {
        // ícones centralizados se estendem quase até a bandeja; à esquerda há o widget do clima (0–200)
        Assert.Equal(206, Place((0, 200), (400, 1650)));
    }

    [Fact]
    public void Sem_espaco_em_lugar_nenhum_fica_no_lugar_de_sempre() =>
        Assert.Equal(1700 - 150 - 6, Place((0, 100), (200, 1650)));

    [Fact]
    public void Sem_informacao_dos_icones_fica_no_lugar_de_sempre() =>
        Assert.Equal(1700 - 150 - 6, Place());

    [Fact]
    public void Arrastado_pelo_usuario_respeita_o_deslocamento_quando_cabe() =>
        Assert.Equal(1700 - 150 - 6 - 100, WidgetPlacement.ChooseX(0, 1700, [(700, 1200)], 150, 6, offset: 100));

    [Fact]
    public void Deslocamento_que_cairia_sobre_os_icones_nao_conta() =>
        Assert.Equal(1700 - 150 - 6, WidgetPlacement.ChooseX(0, 1700, [(700, 1500)], 150, 6, offset: 300));
}

public class TaskbarCornerTests
{
    // Windows 11: TaskbarAl 0 = ícones à esquerda, 1 (ou ausente) = centralizados; TaskbarDa 0 = sem Widgets
    [Theory]
    [InlineData(22631, null, null, true)]  // padrão do Windows 11: centralizado e com Widgets no canto
    [InlineData(26100, 1, 1, true)]
    [InlineData(26100, 1, 0, false)]       // Widgets desligado
    [InlineData(26100, 0, 1, false)]       // ícones à esquerda: o botão Widgets fica junto deles
    [InlineData(19045, null, null, false)] // Windows 10: não há esse canto
    public void Canto_do_botao_Widgets(int build, int? alignment, int? widgets, bool reserved) =>
        Assert.Equal(reserved, WidgetPlacement.WidgetsCornerReserved(build, alignment, widgets));
}

/// <summary>Quando o widget aparece e, quando não, o motivo (vai para o log).</summary>
public class WidgetVisibilityTests
{
    static (bool, string?) V(bool enabled = true, bool inside = true, bool taskbar = true, int thickness = 48,
        bool fullyShown = true, bool fullscreen = false) =>
        WidgetPlacement.Visibility(enabled, inside, taskbar, thickness, fullyShown, fullscreen, notificationState: 2);

    [Fact]
    public void Dentro_da_barra_aparece_com_ela_mesmo_recolhida_ou_com_tela_cheia() =>
        Assert.Equal((true, (string?)null), V(fullyShown: false, fullscreen: true));

    [Fact]
    public void Desligado_nas_preferencias_nao_aparece_e_nao_e_motivo_de_log() =>
        Assert.Equal((false, (string?)null), V(enabled: false));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Sem_barra_ainda_nao_aparece_em_nenhum_modo(bool inside) =>
        Assert.Equal((false, "a barra de tarefas ainda não existe (Windows iniciando?)"), V(inside: inside, taskbar: false));

    [Fact]
    public void Barra_sem_tamanho() =>
        Assert.Equal((false, "a barra de tarefas não tem tamanho ainda"), V(thickness: 0));

    [Fact]
    public void Por_cima_da_barra_some_com_ela_recolhida_ou_com_tela_cheia()
    {
        Assert.Equal((false, "a barra de tarefas está oculta ou fora da tela"), V(inside: false, fullyShown: false));
        Assert.Equal((false, "o Windows informa app em tela cheia ou ocupado (estado 2)"), V(inside: false, fullscreen: true));
        Assert.Equal((true, (string?)null), V(inside: false));
    }
}

public class LevelPrecedenceTests
{
    [Theory]
    [InlineData(new[] { Level.Ok, Level.Warn, Level.Busy }, Level.Busy)]
    [InlineData(new[] { Level.Ok, Level.Error, Level.Busy }, Level.Error)]
    [InlineData(new[] { Level.Ok, Level.Warn }, Level.Warn)]
    [InlineData(new[] { Level.Off, Level.Ok }, Level.Ok)]
    [InlineData(new[] { Level.Off }, Level.Off)]
    [InlineData(new Level[0], Level.Off)]
    public void Erro_ocupado_atencao_ok(Level[] levels, Level worst) => Assert.Equal(worst, Levels.Worst(levels));
}

/// <summary>
/// O widget acompanha a barra pelos avisos do Windows (barra movida ou redimensionada, janelas abrindo e fechando,
/// tema, monitores); a conferência periódica fica só como rede de segurança.
/// </summary>
public class WidgetFollowScheduleTests
{
    static readonly DateTime T0 = new(2026, 10, 23, 12, 0, 0);

    [Fact]
    public void Rede_de_seguranca_espaçada_com_os_avisos_do_Windows() =>
        Assert.Equal(5000, WidgetPlacement.FollowIntervalMs(autoHide: false, inside: true));

    [Fact]
    public void Por_cima_da_barra_que_se_oculta_acompanha_a_animacao() =>
        Assert.Equal(150, WidgetPlacement.FollowIntervalMs(autoHide: true, inside: false));

    [Fact]
    public void Dentro_da_barra_que_se_oculta_desliza_junto_sozinho() =>
        Assert.Equal(5000, WidgetPlacement.FollowIntervalMs(autoHide: true, inside: true));

    [Fact]
    public void Icones_relidos_logo_depois_de_janelas_abrirem_ou_fecharem()
    {
        Assert.False(WidgetPlacement.IconsReadDue(T0.AddMilliseconds(300), lastRead: T0.AddSeconds(-2), changedAt: T0)); // ainda animando
        Assert.True(WidgetPlacement.IconsReadDue(T0.AddMilliseconds(500), lastRead: T0.AddSeconds(-2), changedAt: T0));
        Assert.False(WidgetPlacement.IconsReadDue(T0.AddSeconds(2), lastRead: T0.AddSeconds(1), changedAt: T0)); // já relidos depois
    }

    [Fact]
    public void Timer_do_tamanho_da_espera_nao_perde_a_releitura_por_arredondamento_do_relogio() =>
        Assert.True(WidgetPlacement.IconsReadDue(T0.AddMilliseconds(490), lastRead: T0.AddSeconds(-2), changedAt: T0));

    [Fact]
    public void Muitas_janelas_abrindo_e_fechando_releem_no_maximo_uma_vez_por_segundo()
    {
        // a espera conta da primeira mudança ainda não lida: uma rajada contínua não adia a releitura para sempre
        Assert.False(WidgetPlacement.IconsReadDue(T0.AddMilliseconds(600), lastRead: T0.AddMilliseconds(-200), changedAt: T0));
        Assert.True(WidgetPlacement.IconsReadDue(T0.AddMilliseconds(800), lastRead: T0.AddMilliseconds(-200), changedAt: T0));
    }

    [Fact]
    public void Icones_relidos_de_tempos_em_tempos_mesmo_sem_aviso()
    {
        Assert.False(WidgetPlacement.IconsReadDue(T0.AddSeconds(29), lastRead: T0, changedAt: null));
        Assert.True(WidgetPlacement.IconsReadDue(T0.AddSeconds(30), lastRead: T0, changedAt: null));
    }
}
