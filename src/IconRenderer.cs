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

            float fs = size * (glyph.Length > 1 ? 0.42f : 0.58f);
            using var font = new Font("Segoe UI", fs, FontStyle.Bold, GraphicsUnit.Pixel);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var tb = new SolidBrush(level == Level.Warn ? Color.FromArgb(30, 30, 30) : Color.White);
            g.DrawString(glyph, font, tb, new RectangleF(0, size * 0.03f, size, size), sf);
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
