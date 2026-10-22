using System.Text.Json;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Leitura das respostas da API REST do Syncthing (exemplos em Fixtures/syncthing, no formato real): este PC
/// ("trabalho"), a VM conectada e o PC de casa desligado.
/// </summary>
public class SyncthingApiTests
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

    [Fact]
    public void Estado_do_sistema()
    {
        var snap = new Snapshot();
        SyncthingApi.ApplySystem(snap, Json("system-status"), Json("system-version"));
        Assert.Equal((StState.Running, Me, TimeSpan.FromSeconds(3725), "v2.0.10"), (snap.St, snap.MyId, snap.StUptime, snap.StVersion));
    }

    [Fact]
    public void Dispositivos_sem_este_pc_com_conexao_e_ultima_vez_visto()
    {
        var devices = SyncthingApi.Devices(Json("config"), Connections(), Json("stats-device"), Me);

        Assert.Equal([Vm, Casa], devices.Select(d => d.Id));
        var vm = devices[0];
        Assert.Equal(("vm", true, "203.0.113.10:22000", "v2.0.10", "tcp-client"), (vm.Name, vm.Connected, vm.Address, vm.ClientVersion, vm.ConnType));
        var casa = devices[1];
        Assert.Equal("CASACAS", casa.Name); // sem nome: começo do ID
        Assert.False(casa.Connected);
        Assert.Equal(DateTimeOffset.Parse("2026-10-21T22:15:40-03:00").LocalDateTime, casa.LastSeen);
    }

    [Fact]
    public void Pastas_com_compartilhamento_e_rotulo()
    {
        var folders = SyncthingApi.Folders(Json("config"), Me);

        Assert.Equal(["Claude", "Default Folder", "fotos"], folders.Select(f => f.Label)); // sem rótulo: o ID
        Assert.Equal([Vm, Casa], folders[0].DeviceIds);
        Assert.Equal([2, 0, 1], folders.Select(f => f.SharedWith));
        Assert.Equal(("~/.claude", "sendreceive", false), (folders[0].Path, folders[0].Type, folders[0].Paused));
        Assert.True(folders[2].Paused);
    }

    [Fact]
    public void Estado_de_uma_pasta_sincronizando()
    {
        var f = new FolderInfo();
        SyncthingApi.ApplyFolderStatus(f, Json("db-status-syncing"));
        Assert.Equal(("syncing", 100_000_000L, 40_000_000L, 60_000_000L, 13L, 1), (f.State, f.GlobalBytes, f.InSyncBytes, f.NeedBytes, f.NeedItems, f.Errors));
        Assert.Equal(Level.Busy, f.Status.Level);
    }

    [Fact]
    public void Pasta_parada_e_atualizada_sem_erro()
    {
        var f = new FolderInfo();
        SyncthingApi.ApplyFolderStatus(f, Json("db-status-idle"));
        Assert.Null(f.Error); // "error": "" não é erro
        Assert.Equal(("Atualizada", Level.Ok), f.Status);
    }

    [Fact]
    public void Quanto_falta_enviar_para_um_dispositivo()
    {
        var d = new DeviceInfo { Connected = true };
        SyncthingApi.ApplyCompletion(d, Json("db-completion-vm"));
        Assert.Equal((87.5, 87_500_000L), (d.Completion, d.NeedBytes));
        Assert.Equal(Level.Busy, d.Status.Level);
    }

    [Fact]
    public void Velocidade_vem_da_diferenca_entre_duas_leituras()
    {
        var rates = new TransferRates();
        var t0 = new DateTime(2026, 10, 22, 9, 0, 0, DateTimeKind.Utc);
        var first = Snap(Connections());
        rates.Apply(first, Json("system-connections"), t0);
        Assert.Equal((0.0, 0.0), (first.InRate, first.OutRate)); // primeira leitura: sem base

        var later = JsonDocument.Parse("""
            { "connections": { "VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV-VMVMVMV":
                { "connected": true, "inBytesTotal": 1200000, "outBytesTotal": 500000 } },
              "total": { "inBytesTotal": 2400000, "outBytesTotal": 900000 } }
            """).RootElement.Clone();
        var second = Snap(later.GetProperty("connections"));
        rates.Apply(second, later, t0.AddSeconds(2));
        Assert.Equal((200_000.0, 50_000.0), (second.InRate, second.OutRate));
        Assert.Equal((100_000.0, 0.0), (second.Devices[0].InRate, second.Devices[0].OutRate));

        static Snapshot Snap(JsonElement conns)
        {
            var s = new Snapshot();
            s.Devices.AddRange(SyncthingApi.Devices(Json("config"), conns, Json("stats-device"), Me));
            return s;
        }
    }

    // ---- "está tudo sincronizado?" (proteção: antes de liberar a distro, sair ou desligar) ----

    static SyncCheck Evaluate(string vmCompletion = "db-completion-done", string claudeStatus = "db-status-idle")
    {
        var config = Json("config");
        var conns = Connections();
        Assert.Equal([Vm], SyncthingApi.DevicesToCheck(config, conns, Me));         // só os conectados
        Assert.Equal(["izdab-dr9wa"], SyncthingApi.FoldersToCheck(config, Me));     // só as compartilhadas e ativas
        return SyncthingApi.EvaluateSync(Json("system-status"), config, conns,
            new Dictionary<string, JsonElement> { [Vm] = Json(vmCompletion) },
            new Dictionary<string, JsonElement> { ["izdab-dr9wa"] = Json(claudeStatus) });
    }

    [Fact]
    public void Tudo_sincronizado_com_um_dispositivo_desligado()
    {
        var c = Evaluate();
        Assert.Equal((true, "sincronizado", false), (c.Synced, c.Detail, c.AllDevicesConnected));
        Assert.Equal(TimeSpan.FromSeconds(3725), c.Uptime);
    }

    [Fact]
    public void Enviando_para_um_dispositivo_conectado() =>
        Assert.Equal((false, "enviando para vm (88%)"), (Evaluate(vmCompletion: "db-completion-vm").Synced, Evaluate(vmCompletion: "db-completion-vm").Detail));

    [Fact]
    public void Pasta_compartilhada_ainda_sincronizando() =>
        Assert.Equal((false, "Claude: syncing"), (Evaluate(claudeStatus: "db-status-syncing").Synced, Evaluate(claudeStatus: "db-status-syncing").Detail));

    [Fact]
    public void Pasta_parada_mas_com_pendencias_esta_recebendo()
    {
        var pending = JsonDocument.Parse("""{ "state": "idle", "needTotalItems": 2, "needBytes": 10 }""").RootElement.Clone();
        var c = SyncthingApi.EvaluateSync(Json("system-status"), Json("config"), Connections(),
            new Dictionary<string, JsonElement> { [Vm] = Json("db-completion-done") },
            new Dictionary<string, JsonElement> { ["izdab-dr9wa"] = pending });
        Assert.Equal((false, "Claude: recebendo"), (c.Synced, c.Detail));
    }

    [Fact]
    public void Resposta_que_faltou_conta_como_nao_sincronizado()
    {
        var c = SyncthingApi.EvaluateSync(Json("system-status"), Json("config"), Connections(),
            new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>());
        Assert.False(c.Synced);
    }
}
