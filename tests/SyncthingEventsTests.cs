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
        SyncthingInstance.Wait(() => activity.Overlay(basis, DateTime.Now).Folders[0].RecentFiles == 1, "a atividade chegar", 20);
        Assert.Equal("Sincronizado agora · 1 arquivo", activity.Overlay(basis, DateTime.Now).Folders[0].Status.Text);
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
    public void Termina_quando_o_Syncthing_para()
    {
        var run = Listen();
        Thread.Sleep(500);
        _st.Stop();
        Assert.True(Task.WhenAny(run, Task.Delay(15_000)).Result == run, "o fluxo não terminou com o Syncthing parado");
    }
}
