using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// O widget é um enfeite: se o Windows nega criá-lo dentro da barra de tarefas (erro 5), ele passa para o modo por
/// cima da barra até o app fechar; se nem isso, fica sem widget por um tempo e tenta de novo. O app nunca cai por
/// causa dele. O tempo é monotônico (desde a inicialização), imune a mudanças no relógio do Windows.
/// </summary>
public class WidgetFallbackTests
{
    static readonly TimeSpan T0 = TimeSpan.FromHours(3);

    [Fact]
    public void Normalmente_segue_a_configuracao()
    {
        var f = new WidgetFallback();
        Assert.True(f.WantInside(true));
        Assert.False(f.WantInside(false));
        Assert.True(f.CanCreate(T0));
    }

    [Fact]
    public void Negado_dentro_da_barra_passa_para_o_modo_por_cima()
    {
        var f = new WidgetFallback();
        f.Failed(inside: true, T0);
        Assert.False(f.WantInside(true));            // a configuração diz "dentro", mas o Windows nega
        Assert.True(f.CanCreate(T0));                // tenta já no outro modo
    }

    [Fact]
    public void Negado_dentro_da_barra_fica_por_cima_ate_o_app_fechar()
    {
        // tentar de novo recriaria a janela (piscando) e repetiria o erro no log; reabrir o app tenta de novo
        var f = new WidgetFallback();
        f.Failed(inside: true, T0);
        f.Created();
        Assert.False(f.WantInside(true));
    }

    [Fact]
    public void Falha_tambem_por_cima_da_barra_espera_um_pouco_para_tentar_de_novo()
    {
        var f = new WidgetFallback();
        f.Failed(inside: true, T0);
        f.Failed(inside: false, T0);
        Assert.False(f.CanCreate(T0 + TimeSpan.FromSeconds(5)));
        Assert.True(f.CanCreate(T0 + WidgetFallback.RetryAfter));
    }

    [Fact]
    public void Criou_com_sucesso_zera_as_falhas()
    {
        var f = new WidgetFallback();
        f.Failed(inside: false, T0);
        f.Created();
        Assert.True(f.CanCreate(T0));
    }
}

public class WidgetDenialMessageTests
{
    [Theory]
    [InlineData(0x0000u, "não confiável")]
    [InlineData(0x1000u, "baixa")]
    [InlineData(0x2000u, "média")]
    [InlineData(0x2100u, "média+")]
    [InlineData(0x3000u, "alta")]
    [InlineData(0x4000u, "sistema")]
    [InlineData(0x5000u, "0x5000")]
    public void Nivel_de_integridade_por_nome(uint rid, string nome) =>
        Assert.Equal(nome, WidgetDenial.IntegrityName(rid));

    [Fact]
    public void Mensagem_traz_o_que_explica_a_negacao()
    {
        // erro 5 entre processos costuma ser integridade diferente (um elevado e o outro não)
        var msg = WidgetDenial.Describe(inside: true, error: 5, barHwnd: 0x1A2B, barPid: 4321, barIntegrity: 0x2000,
            myPid: 99, myIntegrity: 0x3000, elevated: true);
        Assert.Equal("criar o widget dentro da barra (erro 5; barra 0x1A2B, processo 4321, integridade média; " +
            "este processo 99, integridade alta, como administrador)", msg);
    }

    [Fact]
    public void Processo_da_barra_desconhecido_nao_inventa_dados()
    {
        var msg = WidgetDenial.Describe(inside: false, error: 1400, barHwnd: 0x10, barPid: null, barIntegrity: null,
            myPid: 99, myIntegrity: null, elevated: false);
        Assert.Equal("criar o widget por cima da barra (erro 1400; barra 0x10, processo desconhecido; " +
            "este processo 99, integridade desconhecida, sem privilégios elevados)", msg);
    }
}
