using WslSyncMonitor;

namespace WslSyncMonitor.WindowsTests;

/// <summary>Executa o código de desenho (ícone e painel) com estados variados: pega exceções e travamentos.</summary>
public class RenderingTests
{
    [Theory]
    [InlineData(IconGlyph.Loading)]
    [InlineData(IconGlyph.Off)]
    [InlineData(IconGlyph.Sync)]
    [InlineData(IconGlyph.Ok)]
    [InlineData(IconGlyph.Warn)]
    [InlineData(IconGlyph.Error)]
    public void Desenha_o_icone_em_todos_os_estados_e_marcas(IconGlyph glyph)
    {
        using var renderer = new IconRenderer();
        foreach (var (kept, update) in new[] { (false, false), (true, false), (false, true), (true, true) })
        {
            var icon = renderer.Render(new TrayIconModel(glyph, Level.Ok, kept, update));
            Assert.True(icon.Width > 0);
        }
    }

    sealed class NoActions : IPopupActions
    {
        public void RefreshNow() { }
        public void CheckForUpdate() { }
        public void InstallUpdate() { }
        public void ShowTechInfo() { }
        public void RunFooterAction(FooterAction action) { }
        public void ShowItemMenu(PanelItem item, Point screen) { }
    }

    static Snapshot FullSnapshot()
    {
        var s = new Snapshot { St = StState.Running, StVersion = "v2.1.6", MyId = "ABCDEFG-1234567", Url = "http://127.0.0.1:8384" };
        s.Distros.Add(s.Target = new DistroInfo("Ubuntu-24.04", true, 2, true));
        s.Distros.Add(new DistroInfo("docker-desktop", false, 2, false));
        s.Folders.Add(new FolderInfo { Id = "a", Label = "Claude", Path = "~/.claude", State = "idle", GlobalBytes = 700_000_000, InSyncBytes = 700_000_000, SharedWith = 2 });
        s.Folders.Add(new FolderInfo { Id = "b", Label = "Projetos", Path = "/home/daniel/p", State = "syncing", GlobalBytes = 100, InSyncBytes = 40 });
        s.AllFolders.AddRange(s.Folders);
        s.Devices.Add(new DeviceInfo { Id = "D1", Name = "Oracle", Connected = true, Address = "tcp://1.2.3.4:22000", ConnType = "tcp-client" });
        s.Devices.Add(new DeviceInfo { Id = "D2", Name = "Trabalho", Connected = false, LastSeen = DateTime.Now.AddHours(-2) });
        s.AllDevices.AddRange(s.Devices);
        return s;
    }

    [Fact]
    public void Desenha_o_painel_com_todas_as_secoes()
    {
        using var popup = new DetailPopup(new NoActions());
        popup.SetSnapshot(FullSnapshot());
        popup.SetGuard("Ubuntu-24.04 em uso por 3 programa(s). Ao fechar, espera sincronizar antes de desligar.");
        popup.SetBusy(null);
        popup.Size = new Size(400, 600);
        using var bmp = new Bitmap(popup.Width, popup.Height);

        popup.DrawToBitmap(bmp, new Rectangle(Point.Empty, popup.Size));
    }

    [Fact]
    public void Desenha_o_painel_sem_dados_e_com_o_wsl_parado()
    {
        using var popup = new DetailPopup(new NoActions());
        popup.Size = new Size(400, 300);
        using var bmp = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(bmp, new Rectangle(Point.Empty, popup.Size));

        popup.SetSnapshot(new Snapshot { St = StState.DistroStopped, Target = new DistroInfo("Ubuntu-24.04", false, 2, true) });
        popup.DrawToBitmap(bmp, new Rectangle(Point.Empty, popup.Size));
    }

    public static TheoryData<UpdateState> UpdateStates() =>
    [
        UpdateState.NotChecked, UpdateState.Checking, UpdateState.UpToDate(DateTime.Now),
        UpdateState.Failed("sem rede"), UpdateState.Found(new Version(9, 9, 9), DateTime.Now),
    ];

    [Theory]
    [MemberData(nameof(UpdateStates))]
    public void Desenha_o_cabecalho_com_status_longo_e_cada_estado_da_atualizacao(UpdateState update)
    {
        using var popup = new DetailPopup(new NoActions());
        popup.SetSnapshot(FullSnapshot());
        popup.SetUpdate(update);
        popup.SetBusy("Encerrando Ubuntu-24.04 com segurança: aguardando o Syncthing terminar de sincronizar 3 pastas…");
        popup.Size = new Size(400, 600);
        using var bmp = new Bitmap(popup.Width, popup.Height);
        popup.DrawToBitmap(bmp, new Rectangle(Point.Empty, popup.Size));
    }

    [Fact]
    public void Barra_vertical_pinta_os_contadores_com_a_cor_do_estado()
    {
        // sem espaço para os ícones, os números levam a cor: pasta sincronizando fica azul como na barra horizontal
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running };
        s.Distros.Add(s.Target);
        s.Folders.Add(new FolderInfo { State = "syncing", GlobalBytes = 10, InSyncBytes = 5 });
        s.AllFolders.AddRange(s.Folders);
        var tb = new TaskbarInfo { Edge = TaskbarEdge.Left, Rect = new Rectangle(0, 0, 48, 1040) };
        Theme.ForceLight = false;
        try
        {
            using var bmp = new WidgetPainter { Snapshot = s }.Preview(tb);
            var busy = Theme.For(Level.Busy);
            bool found = false;
            // só a linha do contador de pastas (a 96 DPI, centro em 10 + (70 - 20) / 3 ≈ 27 px; pontos em 10 px,
            // dispositivos em ≈ 43 px)
            for (int y = 19; y < 36 && !found; y++)
                for (int x = 0; x < bmp.Width && !found; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    found = Math.Abs(c.R - busy.R) < 40 && Math.Abs(c.G - busy.G) < 40 && Math.Abs(c.B - busy.B) < 40;
                }
            Assert.True(found, "contador de pastas do widget vertical não está azul");
        }
        finally { Theme.ForceLight = null; }
    }
}
