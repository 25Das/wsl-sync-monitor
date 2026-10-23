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
    readonly HttpClient _eventsHttp = SyncthingHttp.Create(SyncthingEvents.LongPoll + TimeSpan.FromSeconds(30));
    readonly CancellationTokenSource _eventsCts = new();
    readonly System.Windows.Forms.Timer _lingerTimer = new();
    readonly object _eventsLock = new();
    SynchronizationContext _ui = null!;
    /// <summary>Endereço e chave da API, lidos na thread da interface quando a leitura completa vê o Syncthing respondendo.</summary>
    TaskCompletionSource<(string Url, string Key)> _syncthingUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
    CancellationTokenSource? _eventsConnection;
    FileChangeWatcher? _settingsWatcher;
    /// <summary>O fluxo de eventos está conectado (só lido e escrito na thread da interface).</summary>
    bool _eventsLive;

    /// <summary>O que painel, widget e ícone mostram: a leitura completa mais a atividade em tempo real.</summary>
    Snapshot? Display => _snap == null ? null : _activity.Overlay(_snap, DateTime.Now);

    void StartRealTime()
    {
        _ui = SynchronizationContext.Current!;
        _lingerTimer.Tick += (_, _) => { _lingerTimer.Stop(); ShowState(); }; // a atividade recente venceu
        _refreshSoon.Tick += (_, _) => { _refreshSoon.Stop(); RequestRefresh(); };
        _ = EventsLoopAsync();

        // settings.json editado à mão vale na hora (antes, só na próxima atualização)
        try
        {
            Directory.CreateDirectory(Settings.Dir);
            _settingsWatcher = new FileChangeWatcher(Settings.FilePath, TimeSpan.FromMilliseconds(300),
                () => _ui.Post(_ => ReloadSettingsIfChanged(), null));
        }
        catch (Exception ex) { Log.Warn("configurações", "observar o settings.json", ex); }

        WatchWslRegistry();
    }

    /// <summary>Onde o WSL guarda a lista de distros do usuário (e a chave acima, para quando ele ainda não existe).</summary>
    const string WslRegistryParent = @"Software\Microsoft\Windows\CurrentVersion", WslRegistryKey = WslRegistryParent + @"\Lxss";

    RegistryChangeWatcher? _wslRegistry, _wslRegistryParent;

    /// <summary>
    /// Distro instalada, removida ou trocada a padrão: o Windows avisa (antes, só na próxima leitura). Sem o WSL ainda,
    /// observa a chave de cima até a do WSL aparecer.
    /// </summary>
    void WatchWslRegistry()
    {
        try
        {
            _wslRegistry ??= RegistryChangeWatcher.Open(Microsoft.Win32.RegistryHive.CurrentUser, WslRegistryKey,
                () => _ui.Post(_ => RequestRefreshSoon(), null));
            if (_wslRegistry != null) { _wslRegistryParent?.Dispose(); _wslRegistryParent = null; return; }
            _wslRegistryParent ??= RegistryChangeWatcher.Open(Microsoft.Win32.RegistryHive.CurrentUser, WslRegistryParent,
                () => _ui.Post(_ => { WatchWslRegistry(); RequestRefreshSoon(); }, null), subtree: false);
        }
        catch (Exception ex) { Log.Warn("wsl", "observar a lista de distros no registro", ex); }
    }

    /// <summary>A leitura completa diz se o Syncthing está respondendo: é quando o fluxo de eventos pode começar.</summary>
    void SetSyncthingUp(bool up)
    {
        lock (_eventsLock)
        {
            if (up) { if (!_syncthingUp.Task.IsCompleted) _syncthingUp.TrySetResult(_collector.ApiAccess(_settings)); }
            else if (_syncthingUp.Task.IsCompleted) _syncthingUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    /// <summary>Distro, endereço ou chave mudaram: o fluxo recomeça com o novo acesso quando a leitura completa confirmar.</summary>
    void RestartEvents()
    {
        lock (_eventsLock)
        {
            if (_syncthingUp.Task.IsCompleted) _syncthingUp = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _eventsConnection?.Cancel();
        }
    }

    async Task EventsLoopAsync()
    {
        var stream = new SyncthingEvents(_eventsHttp);
        var stop = _eventsCts.Token;
        int failures = 0;
        while (!stop.IsCancellationRequested)
        {
            Task<(string Url, string Key)> up;
            lock (_eventsLock) up = _syncthingUp.Task;
            (string Url, string Key) access;
            try { access = await up.WaitAsync(stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            CancellationTokenSource connection;
            lock (_eventsLock)
            {
                if (up != _syncthingUp.Task) continue; // o acesso mudou enquanto esperava
                _eventsConnection = connection = CancellationTokenSource.CreateLinkedTokenSource(stop);
            }
            bool received = false;
            try
            {
                await stream.RunAsync(access.Url, access.Key, events => { received = true; OnEvents(events); }, connection.Token,
                    onConnected: () => _ui.Post(_ => SetEventsLive(true), null)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested)
            {
                _activity.Reset(); // saindo, ou o acesso mudou: o que veio do Syncthing anterior não vale mais
                continue;
            }
            catch (Exception ex) { Log.Failure("syncthing", "eventos", "fluxo de eventos interrompido: " + ex.Message); }
            finally
            {
                _ui.Post(_ => SetEventsLive(false), null);
                lock (_eventsLock) _eventsConnection = null;
                connection.Dispose();
            }

            // caiu: o Syncthing parou ou reiniciou? A leitura completa confere na hora (antes, até o próximo ciclo).
            // Falhando seguidas vezes (ex.: um proxy reverso que recusa o fluxo), espera cada vez mais.
            failures = received ? 1 : failures + 1;
            _activity.Reset();
            _ui.Post(_ => { SetSyncthingUp(false); RequestRefresh(); }, null);
            try { await Task.Delay(SyncthingEvents.RetryDelay(failures), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Com o fluxo conectado, a leitura completa periódica pode ser mais espaçada (ver <see cref="RefreshRules"/>).</summary>
    void SetEventsLive(bool live)
    {
        _eventsLive = live;
        UpdateRefreshInterval();
    }

    bool _probing;

    /// <summary>
    /// A distro acabou de ligar (ou o Syncthing de parar) e ele ainda não responde: confere a cada segundo, sem executar
    /// nada na distro, e lê tudo assim que ele responder (antes, até o próximo ciclo; ver <see cref="RefreshRules.ShouldProbe"/>).
    /// </summary>
    async Task ProbeSyncthingAsync()
    {
        if (_probing) return;
        _probing = true;
        try
        {
            if (await new SyncthingEvents(_eventsHttp).WaitUntilUpAsync(_collector.GuiUrl(_settings), TimeSpan.FromSeconds(1),
                    TimeSpan.FromMinutes(2), _eventsCts.Token))
                RequestRefresh();
        }
        catch (OperationCanceledException) { /* saindo */ }
        finally { _probing = false; }
    }

    void OnEvents(IReadOnlyList<JsonElement> events)
    {
        Log.Recovered("syncthing", "eventos", "fluxo de eventos do Syncthing conectado");
        var change = _activity.Apply(events, DateTime.Now);
        if (change == ActivityChange.None) return; // só redesenha se algo mudou
        _ui.Post(_ =>
        {
            if (change.HasFlag(ActivityChange.NeedsRefresh)) RequestRefreshSoon(); // pasta ou dispositivo novo, removido…
            if (change.HasFlag(ActivityChange.Redraw)) ShowState();
        }, null);
    }

    readonly System.Windows.Forms.Timer _refreshSoon = new() { Interval = 300 };

    /// <summary>A configuração do Syncthing mudou: lê tudo de novo em instantes (as mudanças costumam vir em rajada).</summary>
    void RequestRefreshSoon()
    {
        _refreshSoon.Stop();
        _refreshSoon.Start();
    }

    /// <summary>Mostra o estado atual (leitura completa + tempo real) e agenda o fim do aviso de atividade recente.</summary>
    void ShowState()
    {
        if (Display is not { } shown) return;
        _popup.SetSnapshot(shown);
        _widget.SetSnapshot(shown);
        UpdateTrayIcon(shown);
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
        _refreshSoon.Stop();
        _settingsWatcher?.Dispose();
        _wslRegistry?.Dispose();
        _wslRegistryParent?.Dispose();
    }
}
