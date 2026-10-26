using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Pastas e dispositivos ocultos só contam quando estão com problema: uma pasta oculta com erro volta a aparecer
/// (e entra nos contadores e na cor); um dispositivo oculto não deixa o widget azul nem verde, mas impede o falso
/// "nenhum dispositivo conectado".
/// </summary>
public class HiddenItemsTests
{
    static Snapshot Running()
    {
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running };
        s.Distros.Add(s.Target);
        return s;
    }

    static Snapshot WithFolders(params (FolderInfo F, bool Hidden)[] folders)
    {
        var s = Running();
        s.AllFolders.AddRange(folders.Select(f => f.F));
        s.ApplyFilters(f => folders.First(x => x.F == f).Hidden, _ => false);
        return s;
    }

    [Theory]
    [InlineData("idle", 0, false)]      // atualizada: continua oculta
    [InlineData("syncing", 0, false)]   // sincronizando: continua oculta
    [InlineData("idle", 5, true)]       // fora de sincronia: aparece
    [InlineData("error", 0, true)]      // erro: aparece
    public void Pasta_oculta_so_aparece_com_problema(string state, long need, bool shown)
    {
        var hidden = new FolderInfo { Id = "h", State = state, NeedBytes = need };
        var s = WithFolders((new FolderInfo { Id = "v", State = "idle" }, false), (hidden, true));
        Assert.Equal(shown, s.Folders.Contains(hidden));
        Assert.Equal(shown ? "1/2" : "1/1", WidgetModel.From(s, false).Folders);
        Assert.True(hidden.Hidden);
    }

    [Fact]
    public void Pasta_oculta_pausada_continua_oculta()
    {
        var hidden = new FolderInfo { State = "idle", Paused = true };
        var s = WithFolders((new FolderInfo { State = "idle" }, false), (hidden, true));
        Assert.DoesNotContain(hidden, s.Folders);
    }

    [Fact]
    public void Erro_que_chega_pelos_eventos_tambem_mostra_a_pasta_oculta()
    {
        var hidden = new FolderInfo { Id = "h", State = "idle" };
        var s = WithFolders((new FolderInfo { Id = "v", State = "idle" }, false), (hidden, true));
        s.Time = DateTime.Now.AddMinutes(-1);
        var a = new SyncthingActivity(TimeSpan.FromSeconds(5));
        a.Apply([System.Text.Json.JsonDocument.Parse(
            """{"id":1,"type":"StateChanged","time":"2099-01-01T00:00:00Z","data":{"folder":"h","from":"idle","to":"error"}}""").RootElement], DateTime.Now);
        var shown = a.Overlay(s, DateTime.Now);
        Assert.Contains(shown.Folders, f => f.Id == "h");
        Assert.DoesNotContain(s.Folders, f => f.Id == "h"); // o original não muda
    }

    static Snapshot Devices(params (bool Connected, double Completion, bool Hidden)[] devices)
    {
        var s = Running();
        s.AllFolders.Add(new FolderInfo { State = "idle" });
        var all = devices.Select(d => (Info: new DeviceInfo { Id = Guid.NewGuid().ToString(), Connected = d.Connected, Completion = d.Completion }, d.Hidden)).ToList();
        s.AllDevices.AddRange(all.Select(d => d.Info));
        s.ApplyFilters(_ => false, d => all.First(x => x.Info == d).Hidden);
        return s;
    }

    [Fact]
    public void Dispositivo_oculto_sincronizando_nao_deixa_o_widget_azul()
    {
        var s = Devices((true, 100, false), (true, 50, true));
        Assert.Equal(("1/1", Level.Ok), (WidgetModel.From(s, false).Devices, s.DevicesLevel));
    }

    [Fact]
    public void Dispositivo_oculto_conectado_evita_o_falso_nenhum_conectado_sem_pintar_de_verde()
    {
        var s = Devices((false, 100, false), (true, 100, true));
        Assert.Equal(("0/1", Level.Off), (WidgetModel.From(s, false).Devices, s.DevicesLevel));
        Assert.Equal(Level.Ok, s.Overall);
    }

    [Fact]
    public void Nenhum_conectado_entre_visiveis_e_ocultos_e_atencao()
    {
        var s = Devices((false, 100, false), (false, 100, true));
        Assert.Equal(Level.Warn, s.DevicesLevel);
    }
}
