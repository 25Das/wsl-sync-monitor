using System.Text.Json;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>O fluxo de eventos contra um Syncthing de verdade.</summary>
public sealed class SyncthingEventsTests : IDisposable
{
    readonly SyncthingInstance _st = SyncthingInstance.Installed && OperatingSystem.IsLinux() ? new() : null!;
    readonly CancellationTokenSource _cts = new();
    readonly List<JsonElement> _received = new();

    public void Dispose()
    {
        _cts.Cancel();
        _st?.Dispose();
    }

    Task Listen(SyncthingActivity? activity = null) =>
        new SyncthingEvents(_st.Http).RunAsync(_st.Url, _st.ApiKey, events =>
        {
            lock (_received) _received.AddRange(events);
            activity?.Apply(events, DateTime.Now);
        }, _cts.Token);

    bool Received(string type) { lock (_received) return _received.Any(e => e.GetProperty("type").GetString() == type); }

    [SyncthingFact]
    public void Arquivo_novo_aparece_como_atividade_na_hora()
    {
        var activity = new SyncthingActivity(TimeSpan.FromSeconds(30));
        _ = Listen(activity);
        Thread.Sleep(500);

        _st.WriteFile("nota.txt", "conteúdo");

        var basis = new Snapshot { Time = DateTime.Now.AddMinutes(-1), St = StState.Running };
        basis.Folders.Add(new FolderInfo { Id = "claude", Label = "Claude", State = "idle" });
        basis.AllFolders.AddRange(basis.Folders);
        // primeiro "Verificando" (o Syncthing ainda está registrando), depois "Sincronizado agora"
        SyncthingInstance.Wait(() => activity.Overlay(basis, DateTime.Now).Folders[0].Status.Text == "Sincronizado agora · 1 arquivo",
            "a atividade aparecer", 20);
    }

    [SyncthingFact]
    public void Comeca_do_agora_sem_repetir_eventos_antigos()
    {
        _st.WriteFile("antigo.txt", "x");
        SyncthingInstance.Wait(() => JsonDocument.Parse(_st.Http.GetStringAsync(_st.Url + "/rest/db/status?folder=claude").Result)
            .RootElement.GetProperty("globalFiles").GetInt32() == 1, "o arquivo antigo ser indexado");

        _ = Listen();
        Thread.Sleep(2000);

        Assert.False(Received("LocalIndexUpdated"));
    }

    [SyncthingFact]
    public async Task Percebe_o_Syncthing_respondendo_sem_a_chave()
    {
        using var http = new HttpClient(); // sem a chave: a sonda não precisa dela
        Assert.True(await new SyncthingEvents(http).WaitUntilUpAsync(_st.Url, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(5), _cts.Token));
    }

    [SyncthingFact]
    public async Task Desiste_da_sonda_no_prazo_se_ninguem_responde()
    {
        _st.Stop();
        var t0 = DateTime.Now;
        Assert.False(await new SyncthingEvents(_st.Http).WaitUntilUpAsync(_st.Url, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(1), _cts.Token));
        Assert.InRange(DateTime.Now - t0, TimeSpan.FromSeconds(0.9), TimeSpan.FromSeconds(5));
    }

    [SyncthingFact]
    public void Termina_quando_o_Syncthing_para()
    {
        var run = Listen();
        Thread.Sleep(500);
        _st.Stop();
        Assert.True(Task.WhenAny(run, Task.Delay(15_000)).Result == run, "o fluxo não terminou com o Syncthing parado");
    }
}
