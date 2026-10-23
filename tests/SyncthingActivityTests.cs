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
    public async Task Eventos_que_nao_interessam_nao_acordam_quem_espera()
    {
        var a = new SyncthingActivity(Linger);
        var wait = a.WaitForChangeAsync(a.Version, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.False(a.Apply([Event("Ping", "{}")], T0));
        Assert.False(wait.IsCompleted);
        Assert.True(a.Apply(Range("vm", 33, 33), T0)); // StateChanged
        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Espera_por_mudanca_termina_no_prazo_sem_eventos()
    {
        var a = new SyncthingActivity(Linger);
        await a.WaitForChangeAsync(a.Version, TimeSpan.FromMilliseconds(50), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Mudanca_entre_conferir_e_comecar_a_esperar_nao_se_perde()
    {
        var a = new SyncthingActivity(Linger);
        long seen = a.Version;              // antes de conferir
        a.Apply(Range("vm", 33, 33), T0);   // chega enquanto confere
        await a.WaitForChangeAsync(seen, TimeSpan.FromSeconds(30), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Espera_cancelada_avisa_o_cancelamento()
    {
        var a = new SyncthingActivity(Linger);
        using var cts = new CancellationTokenSource();
        var wait = a.WaitForChangeAsync(a.Version, TimeSpan.FromSeconds(30), cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.True(wait.IsCanceled);
    }

    [Fact]
    public void Dispositivo_nao_parece_em_dia_enquanto_outra_pasta_ainda_falta()
    {
        var a = new SyncthingActivity(Linger);
        var basis = TwoFolders(T0.AddSeconds(-1), completion: 80, needBytes: 2_000_000_000); // a pasta "b" está atrasada
        a.Apply([Completion("a", 100, 0)], T0);

        var d = a.Overlay(basis, T0.AddSeconds(Linger.TotalSeconds + 1)).Devices[0];

        Assert.Equal((80, 2_000_000_000L), (d.Completion, d.NeedBytes));
    }

    [Fact]
    public void Dispositivo_atrasa_na_hora_quando_uma_pasta_fica_para_tras()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply([Completion("a", 50, 1000)], T0);
        var d = a.Overlay(TwoFolders(T0.AddSeconds(-1), completion: 100, needBytes: 0), T0).Devices[0];
        Assert.Equal((50, 1000L), (d.Completion, d.NeedBytes));
    }

    [Fact]
    public void Dispositivo_em_dia_quando_todas_as_pastas_dele_chegaram_a_100()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply([Completion("b", 100, 0)], T0.AddSeconds(-5)); // antes da leitura completa, mas ainda é o último de "b"
        a.Apply([Completion("a", 100, 0)], T0);
        var d = a.Overlay(TwoFolders(T0.AddSeconds(-1), completion: 80, needBytes: 2048), T0).Devices[0];
        Assert.Equal((100, 0L), (d.Completion, d.NeedBytes));
    }

    [Fact]
    public void Arquivos_que_ja_sairam_da_tela_sao_esquecidos()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply(Range("vm", 43, 61), T0);
        Assert.True(a.TrackedFiles > 0);
        a.Apply([Event("Ping", "{}")], T0 + Linger + TimeSpan.FromSeconds(1));
        Assert.Equal(0, a.TrackedFiles);
    }

    [Fact]
    public void Arquivo_cujo_fim_nao_chegou_some_quando_a_pasta_para()
    {
        var a = new SyncthingActivity(Linger);
        a.Apply([Event("ItemStarted", """{"folder":"claude","item":"perdido.txt","type":"file","action":"update"}"""),
                 Event("StateChanged", """{"folder":"claude","from":"syncing","to":"idle"}"""),
                 Event("StateChanged", """{"folder":"claude","from":"idle","to":"syncing"}""")], T0);
        Assert.Null(a.Overlay(Base(Trabalho, T0.AddSeconds(-1)), T0).Folders[0].CurrentItem);
        Assert.Equal(0, a.TrackedFiles);
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

    /// <summary>Duas pastas compartilhadas com a "trabalho", que na leitura completa estava com <paramref name="completion"/>.</summary>
    static Snapshot TwoFolders(DateTime at, double completion, long needBytes)
    {
        var s = Base(Trabalho, at);
        s.AllFolders.Clear();
        s.Folders.Clear();
        foreach (var id in new[] { "a", "b" })
            s.Folders.Add(new FolderInfo { Id = id, Label = id, State = "idle", SharedWith = 1, DeviceIds = [Trabalho] });
        s.AllFolders.AddRange(s.Folders);
        s.Devices[0].Completion = completion;
        s.Devices[0].NeedBytes = needBytes;
        return s;
    }

    static JsonElement Completion(string folder, double completion, long needBytes) =>
        Event("FolderCompletion", $$"""{"folder":"{{folder}}","device":"{{Trabalho}}","completion":{{completion}},"needBytes":{{needBytes}},"globalBytes":4096}""");

    static JsonElement Event(string type, string data) =>
        JsonDocument.Parse($$"""{"id":1,"type":"{{type}}","time":"2026-10-23T19:00:00Z","data":{{data}}}""").RootElement.Clone();
}
