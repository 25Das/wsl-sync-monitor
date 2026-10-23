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
    readonly TimeSpan _linger;
    readonly object _lock = new();
    readonly Dictionary<string, FolderLive> _folders = new();
    readonly Dictionary<string, DeviceLive> _devices = new();
    TaskCompletionSource _changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    long _seq; // ordem de chegada (eventos do mesmo lote chegam no mesmo instante)

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
    }

    sealed class DeviceLive
    {
        public bool? Connected;
        public DateTime ConnectedAt;
        public DateTime LastActivity;
        public readonly Dictionary<string, (double Completion, double NeedBytes, DateTime At)> Completion = new(); // por pasta
    }

    public SyncthingActivity(TimeSpan linger) => _linger = linger;

    /// <summary>Aplica um lote de eventos chegados em <paramref name="now"/>; acorda quem espera mudança.</summary>
    public void Apply(IEnumerable<JsonElement> events, DateTime now)
    {
        bool relevant = false;
        lock (_lock)
        {
            foreach (var e in events)
                relevant |= ApplyOne(SyncthingApi.Str(e, "type") ?? "", e.TryGetProperty("data", out var d) ? d : default, now);
        }
        if (relevant) Signal();
    }

    bool ApplyOne(string type, JsonElement d, DateTime now)
    {
        string folder = SyncthingApi.Str(d, "folder") ?? "";
        long seq = ++_seq;
        switch (type)
        {
            case "StateChanged":
                var f = Folder(folder);
                f.State = SyncthingApi.Str(d, "to");
                f.StateAt = now;
                f.StateSeq = seq;
                return true;
            case "ItemStarted":
                Folder(folder).InProgress[SyncthingApi.Str(d, "item") ?? ""] = seq;
                return true;
            case "ItemFinished":
                var item = SyncthingApi.Str(d, "item") ?? "";
                Folder(folder).InProgress.Remove(item);
                Folder(folder).Recent[item] = now;
                return true;
            case "LocalIndexUpdated":
                // alterações detectadas aqui (quem envia) ou gravadas depois de receber (quem recebe: mesmos nomes)
                var names = d.TryGetProperty("filenames", out var fn) && fn.ValueKind == JsonValueKind.Array
                    ? fn.EnumerateArray().Select(x => x.GetString() ?? "").ToList()
                    : Enumerable.Range(0, (int)SyncthingApi.Num(d, "items")).Select(i => $"#{SyncthingApi.Num(d, "sequence")}-{i}").ToList();
                foreach (var name in names) Folder(folder).Recent[name] = now;
                return true;
            case "RemoteIndexUpdated":
                Device(SyncthingApi.Str(d, "device") ?? "").LastActivity = now; // ele mudou algo ou confirmou o que recebeu
                return true;
            case "FolderCompletion":
                var dev = Device(SyncthingApi.Str(d, "device") ?? "");
                double completion = SyncthingApi.Num(d, "completion", 100);
                dev.Completion[folder] = (completion, SyncthingApi.Num(d, "needBytes"), now);
                if (completion < 100) dev.LastActivity = now;
                return true;
            case "FolderSummary":
                var fs = Folder(folder);
                fs.Summary = d.TryGetProperty("summary", out var sum) ? sum.Clone() : null;
                fs.SummaryAt = now;
                fs.SummarySeq = seq;
                return true;
            case "DeviceConnected":
            case "DeviceDisconnected":
                var c = Device(SyncthingApi.Str(d, "id") ?? "");
                c.Connected = type == "DeviceConnected";
                c.ConnectedAt = now;
                return true;
            default:
                return false;
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
                if (fi.State == "syncing" && f.InProgress.Count > 0) fi.CurrentItem = f.InProgress.MaxBy(p => p.Value).Key;
                fi.RecentFiles = f.Recent.Count(p => now - p.Value < _linger);
            }
            foreach (var di in s.AllDevices)
            {
                if (!_devices.TryGetValue(di.Id, out var d)) continue;
                if (d.Connected is { } connected && d.ConnectedAt >= basis.Time) di.Connected = connected;
                var newer = d.Completion.Values.Where(c => c.At >= basis.Time).ToList();
                if (newer.Count > 0)
                {
                    di.Completion = newer.Min(c => c.Completion);
                    di.NeedBytes = (long)newer.Sum(c => c.NeedBytes);
                }
                di.RecentActivity = now - d.LastActivity < _linger;
            }
        }
        return s;
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

    /// <summary>
    /// Espera um evento que mude pastas ou dispositivos, ou o prazo (o que vier primeiro). Usado por quem antes
    /// conferia de tempo em tempo, como a espera da sincronização.
    /// </summary>
    public Task WaitForChangeAsync(TimeSpan max, CancellationToken ct)
    {
        Task changed;
        lock (_lock) changed = _changed.Task;
        return Task.WhenAny(changed, Task.Delay(max, ct)).ContinueWith(_ => { }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    void Signal()
    {
        TaskCompletionSource done;
        lock (_lock)
        {
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
