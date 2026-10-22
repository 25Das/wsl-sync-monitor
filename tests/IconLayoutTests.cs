using System.Drawing;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class IconLayoutTests
{
    [Fact]
    public void Centraliza_pelo_contorno_real_do_numero()
    {
        // contorno do "3" desenhado na origem: começa em (2, 5) - a fonte deixa espaço acima do número
        var glyph = new RectangleF(2, 5, 10, 14);

        var offset = IconLayout.CenterOffset(glyph, iconSize: 32);

        // o centro do contorno deslocado tem de cair no centro do ícone
        Assert.Equal(16f, glyph.X + offset.X + glyph.Width / 2);
        Assert.Equal(16f, glyph.Y + offset.Y + glyph.Height / 2);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(24)]
    public void Arredonda_para_pixel_inteiro_para_nao_borrar(int size)
    {
        var offset = IconLayout.CenterOffset(new RectangleF(1.3f, 4.6f, 7.1f, 9.4f), size);

        Assert.Equal(MathF.Round(offset.X), offset.X);
        Assert.Equal(MathF.Round(offset.Y), offset.Y);
    }
}
