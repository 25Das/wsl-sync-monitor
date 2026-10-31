using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// O widget é um enfeite: se o Windows nega criá-lo dentro da barra de tarefas (erro 5), ele passa para o modo por
/// cima da barra; se nem isso, fica sem widget por um tempo e tenta de novo. O app nunca cai por causa dele.
/// </summary>
public class WidgetFallbackTests
{
    static readonly DateTime T0 = new(2026, 10, 31, 15, 0, 0);

    [Fact]
    public void Normalmente_segue_a_configuracao()
    {
        var f = new WidgetFallback();
        Assert.True(f.WantInside(true, T0));
        Assert.False(f.WantInside(false, T0));
        Assert.True(f.CanCreate(T0));
    }

    [Fact]
    public void Negado_dentro_da_barra_passa_para_o_modo_por_cima()
    {
        var f = new WidgetFallback();
        f.Failed(inside: true, T0);
        Assert.False(f.WantInside(true, T0));        // a configuração diz "dentro", mas o Windows nega
        Assert.True(f.CanCreate(T0));                // tenta já no outro modo
    }

    [Fact]
    public void Falha_tambem_por_cima_da_barra_espera_um_pouco_para_tentar_de_novo()
    {
        var f = new WidgetFallback();
        f.Failed(inside: true, T0);
        f.Failed(inside: false, T0);
        Assert.False(f.CanCreate(T0.AddSeconds(5)));
        Assert.True(f.CanCreate(T0 + WidgetFallback.RetryAfter));
    }

    [Fact]
    public void Depois_de_uma_espera_o_modo_dentro_da_barra_e_tentado_de_novo()
    {
        // a negação pode ser do momento (Explorer ainda subindo): na próxima chance, tenta o modo preferido
        var f = new WidgetFallback();
        f.Failed(inside: true, T0);
        Assert.False(f.WantInside(true, T0 + WidgetFallback.InsideRetryAfter - TimeSpan.FromSeconds(1)));
        Assert.True(f.WantInside(true, T0 + WidgetFallback.InsideRetryAfter));
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
