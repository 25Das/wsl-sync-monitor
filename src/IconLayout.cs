using System.Drawing;

namespace WslSyncMonitor;

/// <summary>Posicionamento do número no ícone da bandeja.</summary>
public static class IconLayout
{
    /// <summary>
    /// Deslocamento que põe o contorno real do texto (sem as margens internas da fonte) no centro do ícone,
    /// arredondado para pixel inteiro para o número não ficar borrado.
    /// </summary>
    public static PointF CenterOffset(RectangleF glyphBounds, int iconSize) => new(
        MathF.Round(iconSize / 2f - (glyphBounds.X + glyphBounds.Width / 2)),
        MathF.Round(iconSize / 2f - (glyphBounds.Y + glyphBounds.Height / 2)));
}
