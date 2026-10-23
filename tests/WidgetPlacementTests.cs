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
