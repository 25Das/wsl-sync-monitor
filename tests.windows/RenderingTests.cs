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
}
