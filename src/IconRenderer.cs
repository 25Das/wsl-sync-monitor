using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WslSyncMonitor;

/// <summary>Desenha o ícone dinâmico da bandeja: círculo na cor do estado geral + nº de dispositivos online.</summary>
public sealed class IconRenderer : IDisposable
{
    IntPtr _handle;
    public Icon? Current { get; private set; }

    public Icon Render(Snapshot? s)
    {
        int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            var level = s?.Overall ?? Level.Off;
            var color = Theme.For(level);
            float pad = size / 16f;
            var rc = new RectangleF(pad, pad, size - 2 * pad, size - 2 * pad);
            using (var b = new SolidBrush(color)) g.FillEllipse(b, rc);

            string glyph = s == null ? "…" :
                !s.WslInstalled ? "!" :
                !s.WslRunning ? "W" :
                s.St != StState.Running ? "!" :
                s.DevicesOnline > 9 ? "9+" : s.DevicesOnline.ToString();

            // desenha o contorno do texto e o centraliza pelos limites reais do número (o DrawString centraliza
            // a caixa da fonte, que tem espaço acima do número, e o deixava deslocado)
            float em = size * (glyph.Length > 1 ? 0.48f : 0.66f);
            using var path = new GraphicsPath();
            using (var family = new FontFamily("Segoe UI"))
                path.AddString(glyph, family, (int)FontStyle.Bold, em, PointF.Empty, StringFormat.GenericTypographic);
            var offset = IconLayout.CenterOffset(path.GetBounds(), size);
            using (var m = new Matrix()) { m.Translate(offset.X, offset.Y); path.Transform(m); }
            using var tb = new SolidBrush(level == Level.Warn ? Color.FromArgb(30, 30, 30) : Color.White);
            g.FillPath(tb, path);
        }

        var h = bmp.GetHicon();
        var icon = Icon.FromHandle(h);
        var oldHandle = _handle; var oldIcon = Current;
        _handle = h; Current = icon;
        oldIcon?.Dispose();
        if (oldHandle != IntPtr.Zero) Native.DestroyIcon(oldHandle);
        return icon;
    }

    public void Dispose()
    {
        Current?.Dispose();
        if (_handle != IntPtr.Zero) Native.DestroyIcon(_handle);
    }
}
