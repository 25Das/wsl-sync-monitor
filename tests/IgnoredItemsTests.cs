using System.Text.Json;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Ignorar é diferente de ocultar: a pasta ou o dispositivo ignorado sai das listas, dos contadores, das cores, dos
/// avisos e da proteção da sincronização; o app não lê o estado dele. Só a presença (na configuração do Syncthing)
/// continua conhecida, para listá-lo à parte, sem ações além de deixar de ignorar.
/// </summary>
public class IgnoredItemsTests
{
    const string Me = "AAAAAAA-BBBBBBB-CCCCCCC-DDDDDDD-EEEEEEE-FFFFFFF-GGGGGGG-HHHHHHH";
    const string Vm = "VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV";
    const string Casa = "CASACAS-CASACAS-CASACAS-CASACAS-CASACAS-CASACAS-CASACAS-CASACAS";

    static JsonElement Json(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "syncthing", name + ".json")));
        return doc.RootElement.Clone();
    }

    static JsonElement Connections() => Json("system-connections").GetProperty("connections");

    static IgnoredItems Ignore(string[]? folders = null, string[]? devices = null) =>
        new(new HashSet<string>(folders ?? []), new HashSet<string>(devices ?? []));

    [Fact]
    public void Protecao_nao_espera_pasta_nem_dispositivo_ignorado()
    {
        var ignored = Ignore(["izdab-dr9wa"], [Vm]);
        Assert.Empty(SyncthingApi.DevicesToCheck(Json("config"), Connections(), Me, ignored));
        Assert.Empty(SyncthingApi.FoldersToCheck(Json("config"), Me, ignored));
        // sem nenhuma resposta da pasta nem do dispositivo: não fazem falta
        var c = SyncthingApi.EvaluateSync(Json("system-status"), Json("config"), Connections(), new Dictionary<string, JsonElement>(),
            new Dictionary<string, JsonElement>(), ignored);
        Assert.True(c.Synced);
    }

    [Fact]
    public void Dispositivo_ignorado_desligado_nao_atrasa_o_inicio()
    {
        // "todos conectados" encurta a espera ao ligar; a casa (desligada) ignorada não conta
        var c = SyncthingApi.EvaluateSync(Json("system-status"), Json("config"), Connections(),
            new Dictionary<string, JsonElement> { [Vm] = Json("db-completion-done") },
            new Dictionary<string, JsonElement> { ["izdab-dr9wa"] = Json("db-status-idle") }, Ignore(devices: [Casa]));
        Assert.Equal((true, true), (c.Synced, c.AllDevicesConnected));
    }

    [Fact]
    public void Sem_ignorados_tudo_continua_igual()
    {
        Assert.Equal([Vm], SyncthingApi.DevicesToCheck(Json("config"), Connections(), Me, IgnoredItems.None));
        Assert.Equal(["izdab-dr9wa"], SyncthingApi.FoldersToCheck(Json("config"), Me, IgnoredItems.None));
    }

    static Snapshot Running()
    {
        var s = new Snapshot { Target = new DistroInfo("Ubuntu", true, 2, true), St = StState.Running };
        s.Distros.Add(s.Target);
        s.Folders.Add(new FolderInfo { Id = "ok", Label = "Ok", State = "idle" });
        s.Folders.Add(new FolderInfo { Id = "ig", Label = "Ignorada", Path = "~/ig", State = "error", Error = "x", SharedWith = 1 });
        s.Devices.Add(new DeviceInfo { Id = "vm", Name = "vm", Connected = true });
        s.Devices.Add(new DeviceInfo { Id = "old", Name = "notebook velho", Connected = false });
        return s;
    }

    [Fact]
    public void Ignorados_saem_das_listas_e_ficam_so_com_o_que_identifica()
    {
        var s = Running();
        s.ApplyIgnored(Ignore(["ig"], ["old"]));

        Assert.Equal(["ok"], s.Folders.Select(f => f.Id));
        Assert.Equal(["vm"], s.Devices.Select(d => d.Id));
        var f = Assert.Single(s.IgnoredFolders);
        Assert.Equal(("ig", "Ignorada", "~/ig", "Ignorada"), (f.Id, f.Label, f.Path, f.Status.Text));
        Assert.Equal(Level.Off, f.Status.Level); // não monitorada: nem erro, nem desconhecido
        var d = Assert.Single(s.IgnoredDevices);
        Assert.Equal(("old", "notebook velho", "Ignorado", Level.Off), (d.Id, d.Name, d.Status.Text, d.Status.Level));
    }

    [Fact]
    public void Ignorados_nao_contam_em_nada()
    {
        var s = Running();
        s.ApplyIgnored(Ignore(["ig"], ["old"]));
        s.AllFolders.AddRange(s.Folders);
        s.AllDevices.AddRange(s.Devices);
        Assert.Equal(("1/1", Level.Ok, "1/1"), (WidgetModel.From(s, false).Folders, s.FoldersLevel, WidgetModel.From(s, false).Devices));
        Assert.Equal(Level.Ok, s.Overall); // a pasta com erro ignorada não deixa vermelho
        Assert.Empty(HealthCheck.Find(s, DateTime.Now, 7));
    }

    [Fact]
    public void Ignorar_o_unico_conectado_avisa_que_nenhum_esta_conectado()
    {
        // o ignorado não é monitorado: se só ele está conectado, para o app nada sincroniza
        var s = Running();
        s.ApplyIgnored(Ignore(devices: ["vm"]));
        s.AllDevices.AddRange(s.Devices);
        Assert.Equal(Level.Warn, s.DevicesLevel);
        s.AllFolders.AddRange(s.Folders.Where(f => f.Id == "ok"));
        s.Folders.RemoveAll(f => f.Id != "ok");
        Assert.Equal("Nenhum dispositivo monitorado conectado", PanelHeader.For(s, null, new Version(1, 0, 0), UpdateState.NotChecked).Status);
        Assert.Contains("nenhum dispositivo monitorado conectado", HealthCheck.Find(s, DateTime.Now, 7).Select(i => i.Text));
    }

    [Fact]
    public void Copia_do_snapshot_leva_os_ignorados()
    {
        var s = Running();
        s.ApplyIgnored(Ignore(["ig"], ["old"]));
        var c = s.Clone();
        Assert.Equal(["ig"], c.IgnoredFolders.Select(f => f.Id));
        Assert.Equal(["old"], c.IgnoredDevices.Select(d => d.Id));
    }

    static JsonElement Event(string type, string data) =>
        JsonDocument.Parse($$"""{"id":1,"type":"{{type}}","time":"2026-10-27T10:00:00Z","data":{{data}}}""").RootElement.Clone();

    [Fact]
    public void Eventos_de_itens_ignorados_sao_descartados()
    {
        var a = new SyncthingActivity(TimeSpan.FromSeconds(5)) { Ignored = Ignore(["ig"], ["old"]) };
        var now = DateTime.Now;
        Assert.Equal(ActivityChange.None, a.Apply([
            Event("ItemFinished", """{"folder":"ig","item":"a.txt","action":"update","type":"file","error":null}"""),
            Event("StateChanged", """{"folder":"ig","from":"idle","to":"error"}"""),
            Event("DeviceDisconnected", """{"id":"old","error":"x"}"""),
            Event("FolderCompletion", """{"folder":"ok","device":"old","completion":50,"needBytes":1,"globalBytes":2}"""),
        ], now));
        Assert.Empty(a.History());

        // os demais continuam valendo
        Assert.NotEqual(ActivityChange.None, a.Apply([Event("StateChanged", """{"folder":"ok","from":"idle","to":"syncing"}""")], now));
    }

    // ---- andamento dos dispositivos sem as pastas ignoradas ----

    [Fact]
    public void Andamento_por_pasta_so_quando_o_dispositivo_compartilha_pasta_ignorada()
    {
        Assert.Null(SyncthingApi.CompletionFolders(Json("config"), Vm, Me, IgnoredItems.None)); // total do Syncthing serve
        Assert.Null(SyncthingApi.CompletionFolders(Json("config"), Vm, Me, Ignore(["default"])));  // "default" não é dela
        // ignorando a "izdab-dr9wa", sobra só a "fotos", que está pausada: nada a esperar
        Assert.Equal([], SyncthingApi.CompletionFolders(Json("config"), Vm, Me, Ignore(["izdab-dr9wa"])));
    }

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void Andamento_combinado_das_pastas_monitoradas()
    {
        var c = SyncthingApi.CombineCompletion([
            J("""{"completion":50,"globalBytes":100,"needBytes":50,"needItems":1,"needDeletes":0}"""),
            J("""{"completion":100,"globalBytes":300,"needBytes":0,"needItems":0,"needDeletes":0}"""),
        ]);
        Assert.Equal((87.5, 50.0, 1.0), (SyncthingApi.Num(c, "completion"), SyncthingApi.Num(c, "needBytes"), SyncthingApi.Num(c, "needItems")));
        Assert.Equal(100, SyncthingApi.Num(SyncthingApi.CombineCompletion([]), "completion", -1));
        // só exclusões pendentes: não está em dia (o Syncthing também não diz 100%)
        var deletes = SyncthingApi.CombineCompletion([J("""{"globalBytes":100,"needBytes":0,"needItems":0,"needDeletes":2}""")]);
        Assert.True(SyncthingApi.Num(deletes, "completion") < 100);
    }

    // ---- eventos ----

    [Fact]
    public void Pasta_ignorada_pausada_ou_retomada_pela_interface_do_Syncthing_tambem_e_descartada()
    {
        var a = new SyncthingActivity(TimeSpan.FromSeconds(5)) { Ignored = Ignore(["ig"]) };
        Assert.Equal(ActivityChange.None, a.Apply([Event("FolderPaused", """{"id":"ig","label":"Ignorada"}""")], DateTime.Now));
        // um dispositivo com o mesmo texto de id não é confundido com a pasta
        var b = new SyncthingActivity(TimeSpan.FromSeconds(5)) { Ignored = Ignore(devices: ["x"]) };
        Assert.NotEqual(ActivityChange.None, b.Apply([Event("FolderPaused", """{"id":"x","label":"X"}""")], DateTime.Now));
    }

    [Fact]
    public void Ao_ignorar_o_que_se_sabia_do_item_e_esquecido()
    {
        // senão, ao deixar de ignorar, voltaria um arquivo "em andamento" de muito tempo atrás
        var a = new SyncthingActivity(TimeSpan.FromSeconds(5));
        a.Apply([Event("ItemStarted", """{"folder":"ig","item":"a.txt","action":"update","type":"file"}""")], DateTime.Now);
        Assert.Equal(1, a.TrackedFiles);
        a.Ignored = Ignore(["ig"]);
        Assert.Equal(0, a.TrackedFiles);
    }

    // ---- velocidades, compartilhamento e itens que sumiram do Syncthing ----

    static JsonElement Conns(long totalIn, long vmIn, long oldIn) => J(
        "{\"total\":{\"inBytesTotal\":" + totalIn + ",\"outBytesTotal\":0},\"connections\":{" +
        "\"vm\":{\"inBytesTotal\":" + vmIn + ",\"outBytesTotal\":0},\"old\":{\"inBytesTotal\":" + oldIn + ",\"outBytesTotal\":0}}}");

    [Fact]
    public void Velocidade_total_nao_inclui_dispositivo_ignorado()
    {
        var rates = new TransferRates();
        var t0 = new DateTime(2026, 10, 27, 10, 0, 0);
        var s = Running(); s.ApplyIgnored(Ignore(devices: ["old"]));
        rates.Apply(s, Conns(0, 0, 0), t0);
        s = Running(); s.ApplyIgnored(Ignore(devices: ["old"]));
        rates.Apply(s, Conns(3000, 1000, 2000), t0.AddSeconds(1));
        Assert.Equal((1000.0, 1000.0), (s.InRate, s.Devices.Single(d => d.Id == "vm").InRate));
    }

    [Fact]
    public void Pasta_compartilhada_so_com_dispositivo_ignorado_conta_como_nao_compartilhada()
    {
        var s = Running();
        s.Folders[0].DeviceIds = ["vm", "old"]; s.Folders[0].SharedWith = 2;
        s.ApplyIgnored(Ignore(devices: ["old"]));
        Assert.Equal(["vm"], s.Folders[0].DeviceIds);
        Assert.Equal(1, s.Folders[0].SharedWith);
        Assert.False(SyncthingApi.FoldersToCheck(Json("config"), Me, Ignore(devices: [Vm, Casa])).Any());
    }

    [Fact]
    public void Ignorado_que_saiu_do_Syncthing_continua_listado_para_poder_deixar_de_ignorar()
    {
        var s = Running();
        s.ApplyIgnored(Ignore(["sumiu"], ["sumiu-dev"]));
        var f = Assert.Single(s.IgnoredFolders);
        Assert.Equal(("sumiu", "Ignorada · não está mais no Syncthing"), (f.Label, f.Status.Text));
        Assert.Equal("Ignorado · não está mais no Syncthing", Assert.Single(s.IgnoredDevices).Status.Text);
    }
}
