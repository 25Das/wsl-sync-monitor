using System.Text.Json;

namespace WslSyncMonitor;

/// <summary>
/// Tempo real: o fluxo de eventos do Syncthing atualiza painel, widget e ícone na hora (syncs pequenos duram
/// ~0,1 s e a leitura completa periódica quase nunca os pegava) e acorda quem espera a sincronização.
/// A leitura completa continua como base; os eventos só sobrepõem o que é mais novo (<see cref="SyncthingActivity"/>).
/// </summary>
public sealed partial class TrayApp
{
    /// <summary>Quanto tempo a atividade recente fica visível ("Sincronizado agora").</summary>
    static readonly TimeSpan ActivityLinger = TimeSpan.FromSeconds(5);

    readonly SyncthingActivity _activity = new(ActivityLinger);
    readonly HttpClient _eventsHttp = new() { Timeout = SyncthingEvents.LongPoll + TimeSpan.FromSeconds(30) };
    readonly CancellationTokenSource _eventsCts = new();
    readonly System.Windows.Forms.Timer _lingerTimer = new();
    SynchronizationContext _ui = null!;
    TaskCompletionSource _syncthingUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    FileChangeWatcher? _settingsWatcher;

    /// <summary>O que painel, widget e ícone mostram: a leitura completa mais a atividade em tempo real.</summary>
    Snapshot? Display => _snap == null ? null : _activity.Overlay(_snap, DateTime.Now);

    void StartRealTime()
    {
        _ui = SynchronizationContext.Current!;
        _lingerTimer.Tick += (_, _) => { _lingerTimer.Stop(); ShowState(); }; // a atividade recente venceu
        _ = EventsLoopAsync();

        // settings.json editado à mão vale na hora (antes, só na próxima atualização)
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            _settingsWatcher = new FileChangeWatcher(Settings.FilePath, TimeSpan.FromMilliseconds(300),
                () => _ui.Post(_ => ReloadSettingsIfChanged(), null));
        }
        catch (Exception ex) { Log.Warn("configurações", "observar o settings.json", ex); }
    }

    /// <summary>A leitura completa diz se o Syncthing está respondendo: é quando o fluxo de eventos pode começar.</summary>
    void SetSyncthingUp(bool up)
    {
        if (up) _syncthingUp.TrySetResult();
        else if (_syncthingUp.Task.IsCompleted) _syncthingUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    async Task EventsLoopAsync()
    {
        var stream = new SyncthingEvents(_eventsHttp);
        var ct = _eventsCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _syncthingUp.Task.WaitAsync(ct).ConfigureAwait(false);
                var (url, key) = _collector.ApiAccess(_settings);
                await stream.RunAsync(url, key, OnEvents, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log.Failure("syncthing", "eventos", "fluxo de eventos interrompido: " + ex.Message); }

            // caiu: o Syncthing parou ou reiniciou? A leitura completa confere na hora (antes, até o próximo ciclo)
            _activity.Reset();
            _ui.Post(_ => { SetSyncthingUp(false); _ = RefreshAsync(); }, null);
            try { await Task.Delay(2000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
        }
    }

    void OnEvents(IReadOnlyList<JsonElement> events)
    {
        _activity.Apply(events, DateTime.Now);
        Log.Recovered("syncthing", "eventos", "fluxo de eventos do Syncthing conectado");
        _ui.Post(_ => ShowState(), null);
    }

    /// <summary>Mostra o estado atual (leitura completa + tempo real) e agenda o fim do aviso de atividade recente.</summary>
    void ShowState()
    {
        if (Display is not { } shown) return;
        _popup.SetSnapshot(shown);
        _widget.SetSnapshot(shown);
        UpdateTrayIcon();
        _lingerTimer.Stop();
        if (_activity.NextExpiry(DateTime.Now) is { } next)
        {
            _lingerTimer.Interval = Math.Max(50, (int)(next - DateTime.Now).TotalMilliseconds + 50);
            _lingerTimer.Start();
        }
    }

    void StopRealTime()
    {
        _eventsCts.Cancel();
        _lingerTimer.Stop();
        _settingsWatcher?.Dispose();
    }
}
