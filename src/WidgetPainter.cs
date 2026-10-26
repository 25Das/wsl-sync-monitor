using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace WslSyncMonitor;

/// <summary>
/// Desenho do widget da barra (pontos do WSL e do Syncthing, pastas e dispositivos), separado da janela: a janela
/// (<see cref="TaskbarWidget"/>) só posiciona e exibe, e as capturas de tela do CI desenham sem janela nenhuma.
/// O tamanho acompanha o DPI e a espessura da barra.
/// </summary>
public sealed class WidgetPainter
{
    const string FolderGlyph = "", DeviceGlyph = ""; // Segoe MDL2 Assets: pasta e notebook

    public Snapshot? Snapshot { get; set; }

    /// <summary>Operação em andamento (iniciar/encerrar WSL): o ponto do WSL fica azul.</summary>
    public bool Busy { get; set; }

    TaskbarInfo _tb = new();
    float S => _tb.Dpi / 96f;
    int P(float v) => (int)Math.Round(v * S);

    (Snapshot?, int, int, bool) _measureKey;
    Size _measured;

    /// <summary>Tamanho do widget nesta barra: largura mínima para o conteúdo atual (recalculada só quando muda).</summary>
    public Size Measure(TaskbarInfo tb)
    {
        _tb = tb;
        if (!tb.Horizontal) return new Size(Math.Max(P(30), tb.Thickness - P(6)), P(70));
        int h = tb.Thickness - P(8);
        var key = (Snapshot, h, tb.Dpi, tb.Horizontal);
        if (key == _measureKey) return _measured;
        using var bmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        _measureKey = key;
        return _measured = new Size((int)Math.Ceiling(Paint(g, 0, h, draw: false)), h);
    }

    /// <summary>
    /// Imagem ARGB do widget para a janela em camadas: fundo quase transparente (alpha 1, para receber o mouse)
    /// e realce ao passar o mouse.
    /// </summary>
    public Bitmap Render(TaskbarInfo tb, Size size, bool hover)
    {
        _tb = tb;
        var bmp = new Bitmap(size.Width, size.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        using (var bg = new SolidBrush(Color.FromArgb(hover ? 40 : 1, Theme.TaskbarLight ? Color.Black : Color.White)))
        using (var path = Round(new Rectangle(Point.Empty, size), P(5)))
            g.FillPath(bg, path);
        Paint(g, size.Width, size.Height, draw: true);
        return bmp;
    }

    /// <summary>O widget sobre uma faixa da cor da barra (capturas de tela do CI).</summary>
    public Bitmap Preview(TaskbarInfo tb)
    {
        var size = Measure(tb);
        var bmp = new Bitmap(size.Width + P(16), Math.Max(size.Height, tb.Thickness));
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Theme.TaskbarLight ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28));
        g.TranslateTransform(P(8), (bmp.Height - size.Height) / 2f);
        Paint(g, size.Width, size.Height, draw: true);
        return bmp;
    }

    /// <summary>Desenha (draw = true) ou só mede o conteúdo. Retorna a largura necessária.</summary>
    float Paint(Graphics g, int w, int h, bool draw)
    {
        var fg = Theme.TaskbarLight ? Color.FromArgb(20, 20, 20) : Color.FromArgb(245, 245, 245);
        using var font = new Font("Segoe UI", P(11.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        using var icons = new Font("Segoe MDL2 Assets", P(11f), FontStyle.Regular, GraphicsUnit.Pixel);
        using var fb = new SolidBrush(fg);

        var (wsl, st, folders, fLevel, devices, dLevel) = WidgetModel.From(Snapshot, Busy);

        int dot = P(7);
        float x0 = P(8);
        float maxX = 0;

        float MeasureW(string t, Font f) => g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic).Width;
        void DotAt(float x, float cy, Level l)
        {
            maxX = Math.Max(maxX, x + dot);
            if (draw) Theme.Dot(g, x, cy - dot / 2f, dot, l);
        }
        float Text(string t, Font f, float x, float cy, Brush? br = null)
        {
            var sz = g.MeasureString(t, f, PointF.Empty, StringFormat.GenericTypographic);
            if (draw) g.DrawString(t, f, br ?? fb, x, cy - sz.Height / 2f, StringFormat.GenericTypographic);
            maxX = Math.Max(maxX, x + sz.Width);
            return x + sz.Width;
        }
        float Item(float x, float cy, Level l, string label)
        {
            DotAt(x, cy, l);
            return Text(label, font, x + dot + P(4), cy);
        }
        Color CountColor(Level l) => l == Level.Off ? fg : Theme.For(l);
        float Count(float x, float cy, string glyph, Level l, string value)
        {
            using var gb = new SolidBrush(CountColor(l));
            x = Text(glyph, icons, x, cy, gb) + P(3);
            return Text(value, font, x, cy);
        }

        if (!_tb.Horizontal)
        {
            // barra vertical: pontos em cima, contagens empilhadas (sem espaço para os ícones: o número leva a cor)
            if (!draw) return w;
            float lineH = font.GetHeight(g);
            float cy = P(10), step = (h - P(20)) / 3f;
            DotAt(w / 2f - dot - P(2), cy, wsl);
            DotAt(w / 2f + P(2), cy, st);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var small = new Font("Segoe UI", P(10f), GraphicsUnit.Pixel);
            using var fBrush = new SolidBrush(CountColor(fLevel));
            using var dBrush = new SolidBrush(CountColor(dLevel));
            g.DrawString(folders, small, fBrush, new RectangleF(0, cy + step - lineH / 2, w, lineH), sf);
            g.DrawString(devices, small, dBrush, new RectangleF(0, cy + 2 * step - lineH / 2, w, lineH), sf);
        }
        else if (_tb.Thickness >= P(38))
        {
            // barra alta: duas linhas, em duas colunas alinhadas qualquer que seja a largura de cada valor
            float cy1 = h * 0.30f, cy2 = h * 0.72f;
            float col1 = Math.Max(dot + P(4) + MeasureW("WSL", font), MeasureW(FolderGlyph, icons) + P(3) + MeasureW(folders, font));
            float col2 = x0 + col1 + P(10);
            Item(x0, cy1, wsl, "WSL");
            Item(col2, cy1, st, "Sync");
            Count(x0, cy2, FolderGlyph, fLevel, folders);
            Count(col2, cy2, DeviceGlyph, dLevel, devices);
        }
        else
        {
            float cy = h / 2f;
            float x = Item(x0, cy, wsl, "WSL") + P(8);
            x = Item(x, cy, st, "Sync") + P(10);
            x = Count(x, cy, FolderGlyph, fLevel, folders) + P(8);
            Count(x, cy, DeviceGlyph, dLevel, devices);
        }
        return maxX + P(8);
    }

    static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d - 1, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d - 1, r.Bottom - d - 1, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d - 1, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
