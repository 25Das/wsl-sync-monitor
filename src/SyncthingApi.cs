using System.Globalization;
using System.Text.Json;

namespace WslSyncMonitor;

/// <summary>Resultado de "está tudo sincronizado?" (usado pela proteção antes de liberar a distro).</summary>
public sealed record SyncCheck(bool Synced, string Detail, TimeSpan Uptime, bool AllDevicesConnected);

/// <summary>
/// Interpreta as respostas da API REST do Syncthing (sem rede: quem busca é o <c>StatusCollector</c>).
/// Campos ausentes ou de outro tipo viram valores neutros, para aceitar versões diferentes do Syncthing.
/// </summary>
public static class SyncthingApi
{
    /// <summary>GET /rest/system/status e /rest/system/version.</summary>
    public static void ApplySystem(Snapshot snap, JsonElement status, JsonElement version)
    {
        snap.St = StState.Running;
        snap.MyId = Str(status, "myID");
        snap.StUptime = TimeSpan.FromSeconds(Num(status, "uptime"));
        snap.StVersion = Str(version, "version");
    }

    /// <summary>Dispositivos do /rest/config (menos este), com o estado da conexão e quando foi visto pela última vez.</summary>
    public static List<DeviceInfo> Devices(JsonElement config, JsonElement connections, JsonElement stats, string? myId)
    {
        var list = new List<DeviceInfo>();
        if (!config.TryGetProperty("devices", out var devs)) return list;
        foreach (var d in devs.EnumerateArray())
        {
            var id = Str(d, "deviceID") ?? "";
            if (id == myId) continue;
            var dev = new DeviceInfo
            {
                Id = id,
                Name = NonEmpty(Str(d, "name")) ?? ShortId(id),
                Paused = Bool(d, "paused"),
            };
            if (Child(connections, id) is { } cn)
            {
                dev.Connected = Bool(cn, "connected");
                dev.Paused |= Bool(cn, "paused");
                dev.Address = Str(cn, "address") ?? "";
                dev.ClientVersion = Str(cn, "clientVersion") ?? "";
                dev.ConnType = Str(cn, "type") ?? "";
            }
            if (Child(stats, id) is { } st &&
                DateTimeOffset.TryParse(Str(st, "lastSeen"), CultureInfo.InvariantCulture, DateTimeStyles.None, out var seen))
                dev.LastSeen = seen.LocalDateTime;
            list.Add(dev);
        }
        return list;
    }

    /// <summary>Pastas do /rest/config, com os dispositivos (menos este) com quem cada uma é compartilhada.</summary>
    public static List<FolderInfo> Folders(JsonElement config, string? myId)
    {
        var list = new List<FolderInfo>();
        if (!config.TryGetProperty("folders", out var folders)) return list;
        foreach (var f in folders.EnumerateArray())
        {
            var fi = new FolderInfo
            {
                Id = Str(f, "id") ?? "",
                Label = NonEmpty(Str(f, "label")) ?? Str(f, "id") ?? "",
                Path = Str(f, "path") ?? "",
                Type = Str(f, "type") ?? "",
                Paused = Bool(f, "paused"),
            };
            fi.DeviceIds = SharedWith(f, myId).ToList();
            fi.SharedWith = fi.DeviceIds.Count;
            list.Add(fi);
        }
        return list;
    }

    /// <summary>GET /rest/db/status?folder=…</summary>
    public static void ApplyFolderStatus(FolderInfo f, JsonElement db)
    {
        f.State = Str(db, "state") ?? "unknown";
        f.GlobalBytes = (long)Num(db, "globalBytes");
        f.InSyncBytes = (long)Num(db, "inSyncBytes");
        f.NeedBytes = (long)Num(db, "needBytes");
        f.NeedItems = (long)(Num(db, "needFiles") + Num(db, "needDirectories") + Num(db, "needSymlinks") + Num(db, "needDeletes"));
        f.Errors = (int)(Num(db, "errors") + Num(db, "pullErrors"));
        f.Error = NonEmpty(Str(db, "error")) ?? NonEmpty(Str(db, "invalid"));
    }

    /// <summary>GET /rest/db/completion?device=… (quanto o dispositivo já tem do que este PC tem).</summary>
    public static void ApplyCompletion(DeviceInfo d, JsonElement completion)
    {
        d.Completion = Num(completion, "completion", 100);
        d.NeedBytes = (long)Num(completion, "needBytes");
    }

    /// <summary>Dispositivos cujo andamento é preciso consultar para saber se tudo sincronizou: os conectados e ativos.</summary>
    public static IReadOnlyList<string> DevicesToCheck(JsonElement config, JsonElement connections, string? myId, IgnoredItems? ignored = null) =>
        ActiveDevices(config, myId, ignored).Where(d => IsConnected(connections, d.Id)).Select(d => d.Id).ToList();

    /// <summary>Pastas cujo estado é preciso consultar: as ativas e compartilhadas com alguém.</summary>
    public static IReadOnlyList<string> FoldersToCheck(JsonElement config, string? myId, IgnoredItems? ignored = null) =>
        SharedActiveFolders(config, myId, ignored).Select(f => Str(f, "id") ?? "").ToList();

    /// <summary>
    /// Tudo sincronizado? Dispositivos conectados a 100% e pastas compartilhadas paradas e sem pendências.
    /// Dispositivos desconectados não são esperados. Uma resposta que faltou conta como "ainda não". Pastas e
    /// dispositivos ignorados ficam de fora (o usuário pediu para não monitorá-los).
    /// </summary>
    public static SyncCheck EvaluateSync(JsonElement status, JsonElement config, JsonElement connections,
        IReadOnlyDictionary<string, JsonElement> completions, IReadOnlyDictionary<string, JsonElement> folderStatuses,
        IgnoredItems? ignored = null)
    {
        var myId = Str(status, "myID");
        var uptime = TimeSpan.FromSeconds(Num(status, "uptime"));
        bool allConnected = true;

        foreach (var (id, name) in ActiveDevices(config, myId, ignored))
        {
            if (!IsConnected(connections, id)) { allConnected = false; continue; }
            if (!completions.TryGetValue(id, out var comp))
                return new(false, $"aguardando {name}", uptime, allConnected);
            double pct = Num(comp, "completion", 100);
            if (pct < 100 || Num(comp, "needItems") > 0 || Num(comp, "needDeletes") > 0)
                return new(false, string.Create(CultureInfo.InvariantCulture, $"enviando para {name} ({pct:0}%)"), uptime, allConnected);
        }

        foreach (var f in SharedActiveFolders(config, myId, ignored))
        {
            var label = NonEmpty(Str(f, "label")) ?? Str(f, "id") ?? "";
            if (!folderStatuses.TryGetValue(Str(f, "id") ?? "", out var db))
                return new(false, $"{label}: aguardando", uptime, allConnected);
            var state = Str(db, "state") ?? "";
            if (state != "idle") return new(false, $"{label}: {state}", uptime, allConnected);
            if (Num(db, "needTotalItems") > 0 || Num(db, "needBytes") > 0)
                return new(false, $"{label}: recebendo", uptime, allConnected);
        }
        return new(true, "sincronizado", uptime, allConnected);
    }

    static IEnumerable<(string Id, string Name)> ActiveDevices(JsonElement config, string? myId, IgnoredItems? ignored)
    {
        ignored ??= IgnoredItems.None;
        if (!config.TryGetProperty("devices", out var devs)) yield break;
        foreach (var d in devs.EnumerateArray())
        {
            var id = Str(d, "deviceID") ?? "";
            if (id == myId || Bool(d, "paused") || ignored.Devices.Contains(id)) continue;
            yield return (id, NonEmpty(Str(d, "name")) ?? ShortId(id));
        }
    }

    static IEnumerable<JsonElement> SharedActiveFolders(JsonElement config, string? myId, IgnoredItems? ignored)
    {
        ignored ??= IgnoredItems.None;
        if (!config.TryGetProperty("folders", out var folders)) yield break;
        foreach (var f in folders.EnumerateArray())
            if (!Bool(f, "paused") && !ignored.Folders.Contains(Str(f, "id") ?? "") && SharedWith(f, myId).Any()) yield return f;
    }

    static IEnumerable<string> SharedWith(JsonElement folder, string? myId) =>
        folder.TryGetProperty("devices", out var fd) && fd.ValueKind == JsonValueKind.Array
            ? fd.EnumerateArray().Select(x => Str(x, "deviceID") ?? "").Where(id => id.Length > 0 && id != myId)
            : [];

    static bool IsConnected(JsonElement connections, string id) => Child(connections, id) is { } c && Bool(c, "connected");

    static string ShortId(string id) => id[..Math.Min(7, id.Length)];

    static JsonElement? Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : null;

    // ---- acesso tolerante aos campos ----

    internal static string? Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double Num(JsonElement e, string p, double def = 0) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : def;

    internal static bool Bool(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.True;

    internal static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Velocidades de transferência: diferença dos totais de bytes entre duas leituras de /rest/system/connections.</summary>
public sealed class TransferRates
{
    DateTime _lastAt;
    long _lastIn, _lastOut;
    readonly Dictionary<string, (long In, long Out)> _lastDev = new();

    /// <summary>Preenche as velocidades do snapshot (total e de cada dispositivo em <c>snap.Devices</c>).</summary>
    public void Apply(Snapshot snap, JsonElement connectionsResponse, DateTime now)
    {
        double dt = (now - _lastAt).TotalSeconds;
        bool hasBase = _lastAt != default && dt > 0.5;
        if (connectionsResponse.TryGetProperty("total", out var total))
        {
            long tin = (long)SyncthingApi.Num(total, "inBytesTotal"), tout = (long)SyncthingApi.Num(total, "outBytesTotal");
            if (hasBase)
            {
                snap.InRate = Math.Max(0, (tin - _lastIn) / dt);
                snap.OutRate = Math.Max(0, (tout - _lastOut) / dt);
            }
            _lastIn = tin; _lastOut = tout;
        }
        var conns = connectionsResponse.TryGetProperty("connections", out var c) ? c : default;
        foreach (var dev in snap.Devices)
        {
            if (conns.ValueKind != JsonValueKind.Object || !conns.TryGetProperty(dev.Id, out var cn)) continue;
            long din = (long)SyncthingApi.Num(cn, "inBytesTotal"), dout = (long)SyncthingApi.Num(cn, "outBytesTotal");
            if (hasBase && _lastDev.TryGetValue(dev.Id, out var prev))
            {
                dev.InRate = Math.Max(0, (din - prev.In) / dt);
                dev.OutRate = Math.Max(0, (dout - prev.Out) / dt);
            }
            _lastDev[dev.Id] = (din, dout);
        }
        _lastAt = now;
    }
}
