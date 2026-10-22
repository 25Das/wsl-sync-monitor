using System.Drawing.Drawing2D;

namespace WslSyncMonitor;

/// <summary>
/// Desenha o ícone da bandeja: círculo na cor do estado com um símbolo (energia, sincronizar, ok, atenção,
/// erro) e marcas nos cantos (mantida ligada, atualização pendente). O que mostrar vem de <see cref="TrayIconModel"/>.
/// </summary>
public sealed class IconRenderer : IDisposable
{
    IntPtr _handle;
    public Icon? Current { get; private set; }

    // símbolos da fonte Segoe MDL2 Assets (presente no Windows 10 e 11)
    static (string Text, string Font) GlyphOf(IconGlyph g) => g switch
    {
        IconGlyph.Off => ("", "Segoe MDL2 Assets"),   // PowerButton
        IconGlyph.Sync => ("", "Segoe MDL2 Assets"),  // Sync
        IconGlyph.Ok => ("", "Segoe MDL2 Assets"),    // CheckMark
        IconGlyph.Error => ("", "Segoe MDL2 Assets"), // Cancel
        IconGlyph.Warn => ("!", "Segoe UI"),
        _ => ("…", "Segoe UI"),
    };

    public Icon Render(TrayIconModel model)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float pad = size / 16f;
            using (var b = new SolidBrush(Theme.For(model.Level))) g.FillEllipse(b, pad, pad, size - 2 * pad, size - 2 * pad);

            // o símbolo é desenhado como contorno e centralizado pelos limites reais (IconLayout)
            var (text, fontName) = GlyphOf(model.Glyph);
            bool isText = fontName == "Segoe UI";
            using var path = new GraphicsPath();
            using (var family = new FontFamily(fontName))
                path.AddString(text, family, (int)(isText ? FontStyle.Bold : FontStyle.Regular),
                    size * (isText ? 0.66f : 0.52f), PointF.Empty, StringFormat.GenericTypographic);
            var offset = IconLayout.CenterOffset(path.GetBounds(), size);
            using (var m = new Matrix()) { m.Translate(offset.X, offset.Y); path.Transform(m); }
            using (var fg = new SolidBrush(model.Level == Level.Warn ? Color.FromArgb(30, 30, 30) : Color.White))
                g.FillPath(fg, path);

            // marcas: atualização pendente (azul, canto superior direito); mantida ligada (canto inferior direito)
            float badge = Math.Max(5, size * 0.36f);
            if (model.UpdatePending) Badge(g, size - badge, 0, badge, Color.FromArgb(0x3B, 0x8E, 0xF6));
            if (model.KeptAlive) Badge(g, size - badge, size - badge, badge, Color.FromArgb(0xE8, 0xE8, 0xE8));
        }

        var h = bmp.GetHicon();
        var icon = Icon.FromHandle(h);
        var oldHandle = _handle; var oldIcon = Current;
        _handle = h; Current = icon;
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero) Native.DestroyIcon(oldHandle);
        return icon;
    }

    /// <summary>Marca redonda com borda escura, legível sobre qualquer cor do círculo.</summary>
    static void Badge(Graphics g, float x, float y, float d, Color color)
    {
        using var border = new SolidBrush(Color.FromArgb(32, 32, 32));
        using var fill = new SolidBrush(color);
        g.FillEllipse(border, x, y, d, d);
        g.FillEllipse(fill, x + 1, y + 1, d - 2, d - 2);
    }

    public void Dispose()
    {
        Current?.Dispose();
        if (_handle != IntPtr.Zero) Native.DestroyIcon(_handle);
    }
}
