using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>
/// Gera capturas de tela do painel, do widget e do ícone em vários estados, nos temas claro e escuro. No CI, vão
/// para a pasta EVIDENCE_DIR, que o workflow publica no branch ci-evidencias (onde dá para conferir visualmente
/// sem um Windows). Fora do CI, vão para uma pasta temporária (o teste ainda pega exceções de desenho).
/// </summary>
[Collection("Tema")] // muda o tema global: não roda em paralelo com outros testes de desenho
public sealed class EvidenceTests : IClassFixture<EvidenceTests.Index>
{
    readonly Index _index;
    public EvidenceTests(Index index) => _index = index;

    /// <summary>Lista as imagens com uma legenda e grava o index.md no fim.</summary>
    public sealed class Index : IDisposable
    {
        public readonly string Dir = Environment.GetEnvironmentVariable("EVIDENCE_DIR") is { Length: > 0 } d
            ? d : Path.Combine(Path.GetTempPath(), "wsm-evidencias");
        readonly List<(string File, string Caption)> _items = new();

        public Index() => Directory.CreateDirectory(Dir);

        public void Save(Bitmap bmp, string name, string caption)
        {
            var file = name + ".png";
            bmp.Save(Path.Combine(Dir, file), ImageFormat.Png);
            lock (_items) _items.Add((file, caption));
        }

        public void Dispose()
        {
            var lines = new List<string> { "# Capturas de tela geradas pelo CI", "" };
            foreach (var (file, caption) in _items.OrderBy(i => i.File))
                lines.AddRange([$"## {caption}", "", $"![{caption}]({file})", ""]);
            File.WriteAllLines(Path.Combine(Dir, "index.md"), lines);
        }
    }

    static readonly DateTime At = new(2026, 10, 22, 14, 5, 9);

    static Snapshot Snap()
    {
        var s = new Snapshot { Time = At, St = StState.Running, StVersion = "v2.1.6", MyId = "ABCDEFG-1234567", Url = "http://127.0.0.1:8384" };
        s.Distros.Add(s.Target = new DistroInfo("Ubuntu-26.04", true, 2, true));
        s.Distros.Add(new DistroInfo("docker-desktop", false, 2, false));
        s.Folders.Add(new FolderInfo { Id = "a", Label = "Claude", Path = "~/.claude", State = "idle", GlobalBytes = 700_000_000, InSyncBytes = 700_000_000, SharedWith = 2 });
        s.AllFolders.AddRange(s.Folders);
        s.Devices.Add(new DeviceInfo { Id = "D1", Name = "VM", Connected = true, Address = "tcp://1.2.3.4:22000", ConnType = "tcp-client" });
        s.Devices.Add(new DeviceInfo { Id = "D2", Name = "PC de casa", Connected = false, LastSeen = At.AddHours(-2) });
        s.AllDevices.AddRange(s.Devices);
        return s;
    }

    static Snapshot Syncing()
    {
        var s = Snap();
        s.Folders.Add(new FolderInfo { Id = "b", Label = "Projetos", Path = "~/projects", State = "syncing", GlobalBytes = 100_000_000, InSyncBytes = 40_000_000 });
        return s;
    }

    /// <summary>Uma pasta cuja leitura falhou e o WSL que não respondeu: estado desconhecido.</summary>
    static Snapshot Unknown()
    {
        var s = Snap();
        var f = new FolderInfo { Id = "u", Label = "Fotos", Path = "~/fotos" };
        ReadFailures.Folder(f, "HTTP 500 em /rest/db/status");
        s.Folders.Add(f);
        return s;
    }

    static Snapshot WslUnknown()
    {
        var s = new Snapshot { Time = At };
        ReadFailures.DistroList(s, "wsl.exe não respondeu a tempo");
        s.ResolveTarget(null);
        return s;
    }

    static Snapshot Stopped() => new() { Time = At, St = StState.DistroStopped, Target = new DistroInfo("Ubuntu-26.04", false, 2, true) };

    static Snapshot AuthError() { var s = Snap(); s.St = StState.AuthError; s.Folders.Clear(); s.Devices.Clear(); return s; }

    sealed class NoActions : IPopupActions
    {
        public void RefreshNow() { }
        public void CheckForUpdate() { }
        public void InstallUpdate() { }
        public void ShowTechInfo() { }
        public void RunFooterAction(FooterAction action) { }
        public void ShowItemMenu(PanelItem item, Point screen) { }
    }

    public static TheoryData<string, string> Panels() => new()
    {
        { "ok", "Tudo sincronizado (um dispositivo desligado), versão em dia" },
        { "sincronizando", "Sincronizando uma pasta" },
        { "arquivo", "Recebendo um arquivo agora (eventos do Syncthing)" },
        { "agora", "Sync pequeno que acabou de acontecer: fica visível por alguns segundos" },
        { "atualizacao", "Versão nova disponível (botão Atualizar)" },
        { "falhou", "Busca de atualização falhou" },
        { "operacao", "Operação longa em andamento (status cortado com reticências)" },
        { "parado", "WSL parado" },
        { "erro", "Syncthing com chave da API recusada" },
        { "carregando", "Carregando (sem dados ainda)" },
        { "desconhecido", "Pasta cuja leitura falhou: estado desconhecido (ponto vazado)" },
        { "wsl-desconhecido", "wsl.exe não respondeu: estado do WSL desconhecido" },
        { "ignorados", "Pasta e dispositivo ignorados: seção à parte, sem estado" },
    };

    [Theory]
    [MemberData(nameof(Panels))]
    public void Painel(string state, string caption)
    {
        foreach (var light in new[] { false, true })
        {
            Theme.ForceLight = light;
            try
            {
                using var popup = new DetailPopup(new NoActions());
                popup.SetUpdate(UpdateState.UpToDate(At));
                switch (state)
                {
                    case "ok": popup.SetSnapshot(Snap()); popup.SetGuard("Ubuntu-26.04 em uso por 2 programa(s). Ao fechar, espera sincronizar antes de desligar."); break;
                    case "sincronizando": popup.SetSnapshot(Syncing()); break;
                    case "arquivo":
                        var receiving = Snap();
                        receiving.Folders[0].State = "syncing";
                        receiving.Folders[0].CurrentItem = "relatorio.md";
                        popup.SetSnapshot(receiving);
                        break;
                    case "agora":
                        var recent = Snap();
                        recent.Folders[0].RecentFiles = 5;
                        recent.Devices[0].RecentActivity = true;
                        popup.SetSnapshot(recent);
                        break;
                    case "atualizacao": popup.SetSnapshot(Snap()); popup.SetUpdate(UpdateState.Found(new Version(9, 9, 9), At)); break;
                    case "falhou": popup.SetSnapshot(Snap()); popup.SetUpdate(UpdateState.Failed("Nenhuma conexão")); break;
                    case "operacao": popup.SetSnapshot(Snap()); popup.SetBusy("Encerrando Ubuntu-26.04 com segurança: aguardando o Syncthing terminar de sincronizar 3 pastas…"); break;
                    case "parado": popup.SetSnapshot(Stopped()); break;
                    case "erro": popup.SetSnapshot(AuthError()); break;
                    case "carregando": popup.SetUpdate(UpdateState.NotChecked); break;
                    case "desconhecido": popup.SetSnapshot(Unknown()); break;
                    case "wsl-desconhecido": popup.SetSnapshot(WslUnknown()); break;
                    case "ignorados":
                        var ignoring = Snap();
                        ignoring.IgnoredFolders.Add(new FolderInfo { Id = "tmp", Label = "Temporários", Path = "~/tmp", Ignored = true });
                        ignoring.IgnoredDevices.Add(new DeviceInfo { Id = "OLD1234", Name = "Notebook antigo", Ignored = true });
                        popup.SetSnapshot(ignoring);
                        break;
                }
                using var bmp = popup.RenderPreview();
                var theme = light ? "claro" : "escuro";
                _index.Save(bmp, $"painel-{state}-{theme}", $"Painel · {caption} · tema {theme}");
            }
            finally { Theme.ForceLight = null; }
        }
    }

    [Fact]
    public void Widget()
    {
        var cases = new (string Name, string Caption, Snapshot? Snap)[]
        {
            ("ok", "tudo certo", Snap()),
            ("sincronizando", "sincronizando", Syncing()),
            ("parado", "WSL parado", Stopped()),
            ("carregando", "carregando", null),
            ("desconhecido", "pasta com estado desconhecido", Unknown()),
            ("wsl-desconhecido", "WSL sem resposta", WslUnknown()),
        };
        foreach (var light in new[] { false, true })
            foreach (var (barName, edge, thickness) in new[] { ("barra-alta", TaskbarEdge.Bottom, 48), ("barra-baixa", TaskbarEdge.Bottom, 32), ("barra-vertical", TaskbarEdge.Left, 48) })
                foreach (var (name, caption, snap) in cases)
                {
                    Theme.ForceLight = light;
                    try
                    {
                        var tb = new TaskbarInfo
                        {
                            Edge = edge,
                            Rect = edge == TaskbarEdge.Left ? new Rectangle(0, 0, thickness, 1040) : new Rectangle(0, 1032, 1920, thickness),
                        };
                        using var bmp = new WidgetPainter { Snapshot = snap }.Preview(tb);
                        var theme = light ? "claro" : "escuro";
                        _index.Save(Zoom(bmp, 2), $"widget-{barName}-{name}-{theme}", $"Widget · {caption} · {barName} ({thickness}px) · tema {theme} (ampliado 2×)");
                    }
                    finally { Theme.ForceLight = null; }
                }
    }

    [Fact]
    public void Icones_da_bandeja()
    {
        var glyphs = Enum.GetValues<IconGlyph>();
        var marks = new[] { (false, false), (true, false), (false, true), (true, true) };
        const int cell = 16, zoom = 4, pad = 8;
        using var sheet = new Bitmap(pad + glyphs.Length * (cell * zoom + pad), pad + marks.Length * (cell * zoom + pad));
        using (var g = Graphics.FromImage(sheet))
        {
            g.Clear(Color.FromArgb(28, 28, 28));
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using var renderer = new IconRenderer();
            for (int c = 0; c < glyphs.Length; c++)
                for (int r = 0; r < marks.Length; r++)
                {
                    var level = glyphs[c] switch
                    {
                        IconGlyph.Ok => Level.Ok, IconGlyph.Sync => Level.Busy, IconGlyph.Warn => Level.Warn,
                        IconGlyph.Error => Level.Error, _ => Level.Off,
                    };
                    // o renderer é dono do ícone (descarta o anterior): sem "using" aqui
                    var icon = renderer.Render(new TrayIconModel(glyphs[c], level, marks[r].Item1, marks[r].Item2));
                    using var bmp = icon.ToBitmap();
                    g.DrawImage(bmp, new Rectangle(pad + c * (cell * zoom + pad), pad + r * (cell * zoom + pad), cell * zoom, cell * zoom));
                }
        }
        _index.Save(sheet, "icones-bandeja",
            $"Ícone da bandeja (ampliado {zoom}×) · colunas: {string.Join(", ", glyphs)} · linhas: sem marcas, mantida ligada, atualização, ambas");
    }

    static Bitmap Zoom(Bitmap src, int factor)
    {
        var dst = new Bitmap(src.Width * factor, src.Height * factor);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.DrawImage(src, new Rectangle(0, 0, dst.Width, dst.Height));
        return dst;
    }
}
