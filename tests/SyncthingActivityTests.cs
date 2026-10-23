using System.Text.Json;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Atividade em tempo real a partir dos eventos do Syncthing (/rest/events). Os eventos são os reais, capturados
/// de duas instâncias: "trabalho" cria 1 arquivo e depois 5, e a "vm" os recebe. Syncs pequenos duram ~0,1 s:
/// o painel os mostra por alguns segundos para dar tempo de ver.
/// </summary>
public class SyncthingActivityTests
{
    const string Trabalho = "QZ3DGLP-YXG63IA-GAK5XWT-6AZSQ65-EEMUENC-J4DRA55-TPBBPIO-C5J27QD";
    const string Vm = "NHSFH3O-OYPNMRV-MDS4GFX-HIBXOW3-RBJNUEB-FGMR4WF-FACMW4M-UFDOZAR";
    static readonly TimeSpan Linger = TimeSpan.FromSeconds(5);
    static readonly DateTime T0 = new(2026, 10, 23, 16, 0, 0);

    static List<JsonElement> Events(string side) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "syncthing", $"events-{side}.json")))
            .RootElement.EnumerateArray().Select(e => e.Clone()).ToList();

    static List<JsonElement> Range(string side, int fromId, int toId) =>
        Events(side).Where(e => e.GetProperty("id").GetInt32() is var id && id >= fromId && id <= toId).ToList();

    /// <summary>O que a última leitura completa (polling) viu: tudo parado e atualizado.</summary>
    static Snapshot Base(string otherDevice, DateTime at)
    {
        var s = new Snapshot { Time = at, St = StState.Running, Target = new DistroInfo("Ubuntu", true, 2, true) };
        s.Distros.Add(s.Target);
        s.Folders.Add(new FolderInfo { Id = "claude", Label = "Claude", State = "idle", SharedWith = 1 });
        s.AllFolders.AddRange(s.Folders);
        s.Devices.Add(new DeviceInfo { Id = otherDevice, Name = "outro", Connected = true });
        s.AllDevices.AddRange(s.Devices);
        return s;
    }

    [Fact]
    public void Quem_recebe_mostra_o_arquivo_sendo_sincronizado()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("vm", 43, 48), T0); // índice novo da "trabalho", começou a baixar n2 e n3 e entrou em "syncing"

        var f = a.Overlay(Base(Trabalho, T0.AddSeconds(-10)), T0).Folders[0];

        Assert.Equal("syncing", f.State);
        Assert.Equal(("Sincronizando n3.txt…", Level.Busy), f.Status);
    }

    [Fact]
    public void Sync_rapido_fica_visivel_por_alguns_segundos_contando_cada_arquivo_uma_vez()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("vm", 43, 61), T0); // 5 arquivos: ItemFinished e LocalIndexUpdated citam os mesmos nomes

        var snap = a.Overlay(Base(Trabalho, T0.AddSeconds(-10)), T0.AddSeconds(1));

        Assert.Equal(("Sincronizado agora · 5 arquivos", Level.Busy), snap.Folders[0].Status);
        Assert.Equal(("Sincronizado agora", Level.Busy), snap.Devices[0].Status); // a "trabalho" mandou o índice
        Assert.Equal(Level.Busy, snap.Overall); // ícone e widget também mostram
    }

    [Fact]
    public void Quem_envia_mostra_as_alteracoes_e_que_o_outro_recebeu()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("trabalho", 27, 33), T0); // detectou 5 alterações; a vm confirmou

        var snap = a.Overlay(Base(Vm, T0.AddSeconds(-10)), T0.AddSeconds(1));

        Assert.Equal(("Sincronizado agora · 5 arquivos", Level.Busy), snap.Folders[0].Status);
        Assert.Equal(("Sincronizado agora", Level.Busy), snap.Devices[0].Status);
    }

    [Fact]
    public void Um_arquivo_no_singular()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("trabalho", 20, 26), T0);
        Assert.Equal("Sincronizado agora · 1 arquivo", a.Overlay(Base(Vm, T0.AddSeconds(-10)), T0).Folders[0].Status.Text);
    }

    [Fact]
    public void Depois_de_alguns_segundos_volta_ao_estado_normal()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("vm", 43, 61), T0);

        var snap = a.Overlay(Base(Trabalho, T0.AddSeconds(-10)), T0 + Linger + TimeSpan.FromMilliseconds(1));

        Assert.Equal(("Atualizada", Level.Ok), snap.Folders[0].Status);
        Assert.Equal(("Atualizado", Level.Ok), snap.Devices[0].Status);
        Assert.Null(a.NextExpiry(T0 + Linger + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void Diz_quando_redesenhar_para_tirar_o_aviso() =>
        Assert.Equal(T0 + Linger, Apply(Range("vm", 43, 61), T0).NextExpiry(T0.AddSeconds(1)));

    [Fact]
    public void Arquivos_de_syncs_anteriores_ja_expirados_nao_entram_na_conta()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("vm", 32, 42), T0);                 // nota.txt
        a.Apply(Range("vm", 43, 61), T0.AddSeconds(10));  // 5 arquivos, 10 s depois
        Assert.Equal("Sincronizado agora · 5 arquivos", a.Overlay(Base(Trabalho, T0), T0.AddSeconds(10)).Folders[0].Status.Text);
    }

    [Fact]
    public void Leitura_completa_mais_nova_vale_mais_que_evento_antigo()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("vm", 43, 48), T0);                 // estado "syncing"
        var newer = Base(Trabalho, T0.AddSeconds(2));     // polling depois: parado
        Assert.Equal("idle", a.Overlay(newer, T0.AddSeconds(2)).Folders[0].State);
    }

    [Fact]
    public void Nao_altera_o_snapshot_original()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("vm", 43, 47), T0);
        var basis = Base(Trabalho, T0.AddSeconds(-10));
        a.Overlay(basis, T0);
        Assert.Equal("idle", basis.Folders[0].State);
        Assert.Equal(("Atualizada", Level.Ok), basis.Folders[0].Status);
    }

    [Fact]
    public void Conexao_e_desconexao_de_dispositivo_na_hora()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply([Event("DeviceDisconnected", $$"""{"id":"{{Trabalho}}","error":"EOF"}""")], T0);
        Assert.False(a.Overlay(Base(Trabalho, T0.AddSeconds(-1)), T0).Devices[0].Connected);

        a.Apply([Event("DeviceConnected", $$"""{"id":"{{Trabalho}}","addr":"127.0.0.1:22100","clientVersion":"v1.27.2","type":"tcp-client"}""")], T0.AddSeconds(1));
        Assert.True(a.Overlay(Base(Trabalho, T0.AddSeconds(-1)), T0.AddSeconds(1)).Devices[0].Connected);
    }

    [Fact]
    public void Resumo_da_pasta_atualiza_pendencias_na_hora()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply([Event("FolderSummary", """{"folder":"claude","summary":{"state":"idle","needBytes":2048,"needFiles":3,"globalBytes":10,"inSyncBytes":5,"errors":0,"pullErrors":0}}""")], T0);
        var f = a.Overlay(Base(Trabalho, T0.AddSeconds(-1)), T0.AddSeconds(Linger.TotalSeconds + 1)).Folders[0];
        Assert.Equal((2048L, 3L), (f.NeedBytes, f.NeedItems));
        Assert.Equal(Level.Warn, f.Status.Level);
    }

    [Fact]
    public void Eventos_que_nao_interessam_nao_acordam_quem_espera()
    {
        var a = new SyncthingActivity(Linger);
        var wait = a.WaitForChangeAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        a.Apply([Event("Ping", "{}")], T0);
        Assert.False(wait.IsCompleted);
        a.Apply(Range("vm", 33, 33), T0); // StateChanged
        Assert.True(wait.Wait(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Espera_por_mudanca_termina_no_prazo_sem_eventos()
    {
        var a = new SyncthingActivity(Linger);
        Assert.True(a.WaitForChangeAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None).Wait(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void Recomecar_esquece_a_atividade()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("vm", 43, 61), T0);
        a.Reset();
        Assert.Equal(("Atualizada", Level.Ok), a.Overlay(Base(Trabalho, T0.AddSeconds(-10)), T0).Folders[0].Status);
    }

    static SyncthingActivity Apply(List<JsonElement> events, DateTime at)
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(events, at);
        return a;
    }

    static JsonElement Event(string type, string data) =>
        JsonDocument.Parse($$"""{"id":1,"type":"{{type}}","time":"2026-10-23T19:00:00Z","data":{{data}}}""").RootElement.Clone();
}
