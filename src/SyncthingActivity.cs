using System.Text.Json;

namespace WslSyncMonitor;

/// <summary>
/// Atividade do Syncthing em tempo real, a partir do fluxo de eventos (/rest/events). Um sync pequeno dura ~0,1 s
/// e a leitura completa periódica quase nunca o pega; os eventos chegam na hora e a atividade fica visível por
/// <c>linger</c> ("Sincronizado agora · 5 arquivos") para dar tempo de ver.
/// <para>
/// Não substitui a leitura completa: sobrepõe a ela (<see cref="Overlay"/>) só o que é mais novo que ela.
/// Seguro entre threads: os eventos chegam num thread de fundo e a interface lê.
/// </para>
/// </summary>
public sealed class SyncthingActivity
{
    /// <summary>Quantos arquivos o histórico de atividade guarda.</summary>
    public const int HistorySize = 100;

    readonly TimeSpan _linger;
    readonly object _lock = new();
    readonly Dictionary<string, FolderLive> _folders = new();
    readonly Dictionary<string, DeviceLive> _devices = new();
    TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _seq; // ordem de chegada (eventos do mesmo lote chegam no mesmo instante)
    long _version; // muda a cada lote que altera algo (quem espera compara com o que já viu)
    readonly LinkedList<ActivityEntry> _history = new(); // mais novo primeiro
    // recebidos cujo índice local ainda não foi atualizado (pasta, arquivo): fora das pastas porque sobrevive ao Reset
    readonly Dictionary<(string Folder, string Item), DateTime> _pulled = new();

    sealed class FolderLive
    {
        public string? State;
        public DateTime StateAt;
        public long StateSeq;
        public JsonElement? Summary;
        public DateTime SummaryAt;
        public long SummarySeq;
        public readonly Dictionary<string, long> InProgress = new();     // arquivo → ordem em que começou
        public readonly Dictionary<string, DateTime> Recent = new();     // arquivo → quando terminou (cada um uma vez)
        public bool? Paused;
        public DateTime PausedAt;
        public int? Errors;
        public DateTime ErrorsAt;
        public long ErrorsSeq;
    }

    sealed class DeviceLive
    {
        public bool? Connected;
        public DateTime ConnectedAt;
        public DateTime LastActivity;
        public bool? Paused;
        public DateTime PausedAt;
        public readonly Dictionary<string, (double Completion, double NeedBytes, DateTime At)> Completion = new(); // por pasta
    }

    public SyncthingActivity(TimeSpan linger) => _linger = linger;

    /// <summary>
    /// Aplica um lote de eventos chegados em <paramref name="now"/> e diz o que mudou: pastas ou dispositivos (vale
    /// redesenhar; acorda quem espera mudança) e/ou a configuração (vale uma leitura completa já).
    /// </summary>
    public ActivityChange Apply(IEnumerable<JsonElement> events, DateTime now)
    {
        var change = ActivityChange.None;
        lock (_lock)
        {
            foreach (var e in events)
                change |= ApplyOne(SyncthingApi.Str(e, "type") ?? "", e.TryGetProperty("data", out var d) ? d : default, now);
            Prune(now);
        }
        if (change != ActivityChange.None) Signal();
        return change;
    }

    /// <summary>Esquece arquivos que já saíram da tela (o app fica aberto por semanas: a lista não pode só crescer).</summary>
    void Prune(DateTime now)
    {
        foreach (var f in _folders.Values)
            foreach (var old in f.Recent.Where(p => now - p.Value >= _linger).Select(p => p.Key).ToList()) f.Recent.Remove(old);
        foreach (var old in _pulled.Where(p => now - p.Value >= PulledMemory).Select(p => p.Key).ToList()) _pulled.Remove(old);
    }

    /// <summary>Quanto esperar o índice local citar um arquivo recebido (chega logo depois; mais que isso, se perdeu).</summary>
    static readonly TimeSpan PulledMemory = TimeSpan.FromMinutes(1);

    void Remember(ActivityEntry e)
    {
        _history.AddFirst(e);
        while (_history.Count > HistorySize) _history.RemoveLast();
    }

    /// <summary>
    /// Tira do histórico o "alterado aqui" do arquivo que, na verdade, acabou de ser recebido (o índice local chegou
    /// segundos antes do fim do recebimento). Mais antigo que isso, foi mesmo uma alteração daqui (ex.: um conflito).
    /// </summary>
    bool ForgetChangedHere(string folder, string item, DateTime now)
    {
        for (var n = _history.First; n != null && now - n.Value.At < OutOfOrder; n = n.Next)
            if (n.Value is { Kind: ActivityKind.ChangedHere } e && e.Folder == folder && e.Item == item)
            {
                _history.Remove(n);
                return true;
            }
        return false;
    }

    /// <summary>Quanto o índice local pode chegar antes do fim do recebimento do mesmo arquivo.</summary>
    static readonly TimeSpan OutOfOrder = TimeSpan.FromSeconds(5);

    /// <summary>Arquivos recebidos, alterados aqui ou que falharam, do mais novo ao mais antigo (de uma pasta ou de todas).</summary>
    public IReadOnlyList<ActivityEntry> History(string? folder = null)
    {
        lock (_lock) return _history.Where(e => folder == null || e.Folder == folder).ToList();
    }

    /// <summary>Quantos arquivos estão sendo lembrados (em andamento ou recentes).</summary>
    internal int TrackedFiles { get { lock (_lock) return _folders.Values.Sum(f => f.InProgress.Count + f.Recent.Count); } }

    /// <summary>Pastas e dispositivos ignorados pelo usuário: os eventos deles são descartados (não são monitorados).</summary>
    public IgnoredItems Ignored { get; set; } = IgnoredItems.None;

    ActivityChange ApplyOne(string type, JsonElement d, DateTime now)
    {
        const ActivityChange redraw = ActivityChange.Redraw;
        string folder = SyncthingApi.Str(d, "folder") ?? "";
        var device = SyncthingApi.Str(d, "device") ?? SyncthingApi.Str(d, "id");
        if (Ignored.Folders.Contains(folder) || (device != null && Ignored.Devices.Contains(device))) return ActivityChange.None;
        long seq = ++_seq;
        switch (type)
        {
            case "StateChanged":
                var f = Folder(folder);
                f.State = SyncthingApi.Str(d, "to");
                f.StateAt = now;
                f.StateSeq = seq;
                if (f.State != "syncing") f.InProgress.Clear(); // parou: nada mais está baixando (mesmo se um ItemFinished se perdeu)
                return redraw;
            case "ItemStarted":
                Folder(folder).InProgress[SyncthingApi.Str(d, "item") ?? ""] = seq;
                return redraw;
            case "ItemFinished":
                var item = SyncthingApi.Str(d, "item") ?? "";
                var error = SyncthingApi.NonEmpty(SyncthingApi.Str(d, "error"));
                Folder(folder).InProgress.Remove(item);
                Folder(folder).Recent[item] = now;
                // o índice local pode ter chegado logo antes (comum ao apagar): então não foi "alterado aqui"; senão,
                // marca para o índice local que vem depois não contar como "alterado aqui"
                if (!ForgetChangedHere(folder, item, now)) _pulled[(folder, item)] = now;
                Remember(new ActivityEntry(now, folder, item,
                    error != null ? ActivityKind.Failed : SyncthingApi.Str(d, "action") == "delete" ? ActivityKind.Deleted : ActivityKind.Received, error));
                return redraw;
            case "LocalIndexUpdated":
                // alterações detectadas aqui (quem envia) ou gravadas depois de receber (quem recebe: mesmos nomes)
                var names = d.TryGetProperty("filenames", out var fn) && fn.ValueKind == JsonValueKind.Array
                    ? fn.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : Enumerable.Range(0, (int)SyncthingApi.Num(d, "items")).Select(i => $"#{SyncthingApi.Num(d, "sequence")}-{i}").ToList();
                foreach (var name in names) Folder(folder).Recent[name] = now;
                // os que não acabaram de ser recebidos foram alterados aqui (e vão ser enviados)
                if (fn.ValueKind == JsonValueKind.Array)
                    foreach (var name in names)
                        if (!_pulled.Remove((folder, name))) Remember(new ActivityEntry(now, folder, name, ActivityKind.ChangedHere, null));
                return redraw;
            case "RemoteIndexUpdated":
                Device(SyncthingApi.Str(d, "device") ?? "").LastActivity = now; // ele mudou algo ou confirmou o que recebeu
                return redraw;
            case "FolderCompletion":
                var dev = Device(SyncthingApi.Str(d, "device") ?? "");
                double completion = SyncthingApi.Num(d, "completion", 100);
                dev.Completion[folder] = (completion, SyncthingApi.Num(d, "needBytes"), now);
                if (completion < 100) dev.LastActivity = now;
                return redraw;
            case "FolderSummary":
                var fs = Folder(folder);
                fs.Summary = d.TryGetProperty("summary", out var sum) ? sum.Clone() : null;
                fs.SummaryAt = now;
                fs.SummarySeq = seq;
                return redraw;
            case "DeviceConnected":
            case "DeviceDisconnected":
                var c = Device(SyncthingApi.Str(d, "id") ?? "");
                c.Connected = type == "DeviceConnected";
                c.ConnectedAt = now;
                return redraw;
            case "FolderPaused":
            case "FolderResumed":
                var p = Folder(SyncthingApi.Str(d, "id") ?? "");
                p.Paused = type == "FolderPaused";
                p.PausedAt = now;
                return redraw;
            case "DevicePaused":
            case "DeviceResumed":
                var dp = Device(SyncthingApi.Str(d, "device") ?? "");
                dp.Paused = type == "DevicePaused";
                dp.PausedAt = now;
                return redraw;
            case "FolderErrors":
                var fe = Folder(folder);
                fe.Errors = d.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array ? errs.GetArrayLength() : 0;
                fe.ErrorsAt = now;
                fe.ErrorsSeq = seq;
                return redraw;
            case "ConfigSaved":             // pasta ou dispositivo criado, removido ou alterado (aqui ou pela interface web)
            case "PendingDevicesChanged":   // dispositivo novo pedindo para conectar
            case "PendingFoldersChanged":   // pasta oferecida por outro dispositivo
                return ActivityChange.NeedsRefresh;
            default:
                return ActivityChange.None;
        }
    }

    FolderLive Folder(string id) => _folders.TryGetValue(id, out var f) ? f : _folders[id] = new FolderLive();
    DeviceLive Device(string id) => _devices.TryGetValue(id, out var d) ? d : _devices[id] = new DeviceLive();

    /// <summary>
    /// Cópia do snapshot com o que os eventos trouxeram de mais novo que ele (estado e resumo das pastas, conexão
    /// e andamento dos dispositivos) e com a atividade recente. O original não muda.
    /// </summary>
    public Snapshot Overlay(Snapshot basis, DateTime now)
    {
        var s = basis.Clone();
        lock (_lock)
        {
            foreach (var fi in s.AllFolders)
            {
                if (!_folders.TryGetValue(fi.Id, out var f)) continue;
                // só o que é mais novo que a leitura completa, na ordem em que chegou (o resumo também traz o estado)
                bool summary = f.Summary != null && f.SummaryAt >= basis.Time;
                bool state = f.State != null && f.StateAt >= basis.Time;
                if (summary && (!state || f.SummarySeq < f.StateSeq)) SyncthingApi.ApplyFolderStatus(fi, f.Summary!.Value);
                if (state) fi.State = f.State!;
                if (summary && state && f.SummarySeq > f.StateSeq) SyncthingApi.ApplyFolderStatus(fi, f.Summary!.Value);
                if (f.Errors is { } errors && f.ErrorsAt >= basis.Time && (!summary || f.ErrorsSeq > f.SummarySeq)) fi.Errors = errors;
                if (f.Paused is { } paused && f.PausedAt >= basis.Time) fi.Paused = paused;
                if (fi.State == "syncing" && f.InProgress.Count > 0) fi.CurrentItem = f.InProgress.MaxBy(p => p.Value).Key;
                fi.RecentFiles = f.Recent.Count(p => now - p.Value < _linger);
            }
            foreach (var di in s.AllDevices)
            {
                if (!_devices.TryGetValue(di.Id, out var d)) continue;
                if (d.Connected is { } connected && d.ConnectedAt >= basis.Time) di.Connected = connected;
                if (d.Paused is { } devicePaused && d.PausedAt >= basis.Time) di.Paused = devicePaused;
                ApplyCompletion(di, d, s, basis.Time);
                di.RecentActivity = now - d.LastActivity < _linger;
            }
        }
        s.ShowHiddenProblems(); // pasta oculta que um evento deixou com problema volta a aparecer
        return s;
    }

    /// <summary>
    /// Andamento do dispositivo: a leitura completa traz o total de todas as pastas; os eventos, o de cada pasta. Com o
    /// último evento de todas as pastas dele, o total sai dos eventos; senão, os eventos só podem atrasá-lo (uma pasta
    /// em dia não diz nada das outras, que podem estar faltando).
    /// </summary>
    static void ApplyCompletion(DeviceInfo di, DeviceLive d, Snapshot s, DateTime basisTime)
    {
        if (!d.Completion.Values.Any(c => c.At >= basisTime)) return;
        var shared = s.AllFolders.Where(f => f.DeviceIds.Contains(di.Id)).Select(f => f.Id).ToList();
        if (shared.Count > 0 && shared.All(d.Completion.ContainsKey))
        {
            var all = shared.Select(id => d.Completion[id]).ToList();
            di.Completion = all.Min(c => c.Completion);
            di.NeedBytes = (long)all.Sum(c => c.NeedBytes);
            di.CompletionUnknown = false; // os eventos trouxeram o andamento de todas as pastas
            return;
        }
        var newer = d.Completion.Values.Where(c => c.At >= basisTime).ToList();
        di.Completion = Math.Min(di.Completion, newer.Min(c => c.Completion));
        di.NeedBytes = Math.Max(di.NeedBytes, (long)newer.Sum(c => c.NeedBytes));
    }

    /// <summary>Quando o aviso de atividade recente mais próximo vence (para redesenhar), ou null se nenhum.</summary>
    public DateTime? NextExpiry(DateTime now)
    {
        lock (_lock)
        {
            var times = _folders.Values.SelectMany(f => f.Recent.Values).Concat(_devices.Values.Select(d => d.LastActivity))
                .Select(t => t + _linger).Where(t => t > now).ToList();
            return times.Count == 0 ? null : times.Min();
        }
    }

    /// <summary>Muda a cada lote de eventos que altera algo: guarde antes de conferir e passe a <see cref="WaitForChangeAsync"/>.</summary>
    public long Version { get { lock (_lock) return _version; } }

    /// <summary>
    /// Espera um evento que mude pastas ou dispositivos depois de <paramref name="seen"/> (volta na hora se já houve),
    /// ou o prazo (o que vier primeiro). Usado por quem antes conferia de tempo em tempo, como a espera da sincronização.
    /// </summary>
    public async Task WaitForChangeAsync(long seen, TimeSpan max, CancellationToken ct)
    {
        Task changed;
        lock (_lock)
        {
            if (_version != seen) return;
            changed = _changed.Task;
        }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Task.WhenAny(changed, Task.Delay(max, stop.Token)).ConfigureAwait(false);
        stop.Cancel(); // não deixa o prazo pendente
        ct.ThrowIfCancellationRequested();
    }

    void Signal()
    {
        TaskCompletionSource done;
        lock (_lock)
        {
            _version++;
            done = _changed;
            _changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        done.TrySetResult();
    }

    /// <summary>Esquece tudo (o fluxo caiu: o Syncthing pode ter reiniciado, e a leitura completa vale de novo).</summary>
    public void Reset()
    {
        lock (_lock) { _folders.Clear(); _devices.Clear(); }
    }
}

/// <summary>O que um lote de eventos mudou.</summary>
[Flags]
public enum ActivityChange
{
    None = 0,
    /// <summary>Pastas ou dispositivos mudaram: vale redesenhar.</summary>
    Redraw = 1,
    /// <summary>A configuração mudou (pastas ou dispositivos novos, removidos, pedidos de conexão): vale uma leitura completa já.</summary>
    NeedsRefresh = 2,
}

public enum ActivityKind { Received, Deleted, ChangedHere, Failed }

/// <summary>Um arquivo no histórico de atividade.</summary>
public sealed record ActivityEntry(DateTime At, string Folder, string Item, ActivityKind Kind, string? Error)
{
    /// <summary>Uma linha por arquivo, com o nome da pasta (ou o id, se ela não existe mais) e a data se não for de hoje.</summary>
    public static string Format(IEnumerable<ActivityEntry> entries, IReadOnlyDictionary<string, string> folderLabels, DateTime now) =>
        string.Join("\n", entries.Select(e =>
        {
            var (mark, what) = e.Kind switch
            {
                ActivityKind.Received => ("↓", "recebido"),
                ActivityKind.Deleted => ("−", "apagado"),
                ActivityKind.ChangedHere => ("↑", "alterado aqui"),
                _ => ("✕", "falhou: " + e.Error),
            };
            var folder = folderLabels.TryGetValue(e.Folder, out var label) ? label : e.Folder;
            var when = e.At.ToString(e.At.Date == now.Date ? "HH:mm:ss" : "dd/MM HH:mm:ss", // o app fica aberto por dias
                System.Globalization.CultureInfo.InvariantCulture);
            return $"{when}  {mark} {folder}/{e.Item} ({what})";
        }));
}
