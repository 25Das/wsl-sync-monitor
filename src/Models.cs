namespace WslSyncMonitor;

public enum Level { Off, Ok, Busy, Warn, Error }

public sealed record DistroInfo(string Name, bool Running, int Version, bool IsDefault);

public sealed class FolderInfo
{
    public string Id = "", Label = "", Path = "", Type = "";
    public bool Paused;
    public string State = "unknown";
    public long GlobalBytes, InSyncBytes, NeedBytes, NeedItems;
    public int Errors;
    public int SharedWith;
    public List<string> DeviceIds = new(); // dispositivos (exceto este) com quem a pasta é compartilhada
    public string? Error;
    public string? CurrentItem;  // arquivo sendo sincronizado agora (eventos do Syncthing)
    public int RecentFiles;      // arquivos sincronizados há pouco (eventos): mostrados por alguns segundos

    public FolderInfo Clone() => (FolderInfo)MemberwiseClone();

    public double Percent => GlobalBytes <= 0 ? 100 : Math.Clamp(100.0 * InSyncBytes / GlobalBytes, 0, 100);

    public (string Text, Level Level) Status => Paused ? ("Pausada", Level.Off) : State switch
    {
        "idle" when Errors > 0 || Error != null => ($"{Errors} erro(s)", Level.Error),
        "idle" when NeedBytes > 0 || NeedItems > 0 => ($"Fora de sincronia ({Fmt.Bytes(NeedBytes)})", Level.Warn),
        "idle" when RecentFiles > 0 => ($"Sincronizado agora · {RecentFiles} arquivo{(RecentFiles == 1 ? "" : "s")}", Level.Busy),
        "idle" => ("Atualizada", Level.Ok),
        "scanning" => ("Verificando", Level.Busy),
        "scan-waiting" => ("Aguardando verificação", Level.Busy),
        "syncing" when CurrentItem != null => ($"Sincronizando {CurrentItem}…", Level.Busy),
        "syncing" => ($"Sincronizando {Percent:0}%", Level.Busy),
        "sync-preparing" => ("Preparando sincronização", Level.Busy),
        "sync-waiting" => ("Aguardando sincronização", Level.Busy),
        "cleaning" or "clean-waiting" => ("Limpando", Level.Busy),
        "error" => ("Erro" + (Error != null ? ": " + Error : ""), Level.Error),
        _ => (State, Level.Warn),
    };
}

public sealed class DeviceInfo
{
    public string Id = "", Name = "";
    public bool Paused, Connected;
    public string Address = "", ClientVersion = "", ConnType = "";
    public double Completion = 100;
    public long NeedBytes;
    public DateTime? LastSeen;
    public double InRate, OutRate;
    public bool RecentActivity; // trocou índices ou dados há pouco (eventos): mostrado por alguns segundos

    public DeviceInfo Clone() => (DeviceInfo)MemberwiseClone();

    /// <summary>Velocidades, tipo e endereço da conexão e versão do Syncthing remoto (só o que se conhece).</summary>
    public string ConnectionText
    {
        get
        {
            var kind = ConnType.Contains("relay", StringComparison.Ordinal) ? "relay" : ConnType.Split('-')[0];
            var parts = new[] { $"↓ {Fmt.Rate(InRate)}  ↑ {Fmt.Rate(OutRate)}", $"{kind} {Address}".Trim(), ClientVersion };
            return string.Join(" · ", parts.Where(p => p.Length > 0));
        }
    }

    public (string Text, Level Level) Status
    {
        get
        {
            if (Paused) return ("Pausado", Level.Off);
            if (!Connected)
                // desligado é normal (ex.: o outro PC); quem avisa é o estado geral quando nenhum está conectado
                return (LastSeen is { Year: > 1970 } ls ? $"Desconectado · visto {Fmt.Ago(ls)}" : "Desconectado", Level.Off);
            if (Completion < 100 || NeedBytes > 0)
                return ($"Sincronizando {Math.Floor(Completion):0}% ({Fmt.Bytes(NeedBytes)})", Level.Busy);
            if (RecentActivity) return ("Sincronizado agora", Level.Busy);
            return ("Atualizado", Level.Ok);
        }
    }
}

public enum StState { Unknown, DistroStopped, NotRunning, Unreachable, AuthError, Running }

public sealed class Snapshot
{
    /// <summary>
    /// Cópia em que pastas e dispositivos podem ser alterados sem afetar o original (as listas visíveis apontam
    /// para as mesmas cópias que as listas completas).
    /// </summary>
    public Snapshot Clone()
    {
        var c = (Snapshot)MemberwiseClone();
        var folders = AllFolders.ToDictionary<FolderInfo, FolderInfo, FolderInfo>(f => f, f => f.Clone(), ReferenceEqualityComparer.Instance);
        var devices = AllDevices.ToDictionary<DeviceInfo, DeviceInfo, DeviceInfo>(d => d, d => d.Clone(), ReferenceEqualityComparer.Instance);
        c.AllFolders = AllFolders.Select(f => folders[f]).ToList();
        c.Folders = Folders.Select(f => folders.TryGetValue(f, out var x) ? x : f.Clone()).ToList();
        c.AllDevices = AllDevices.Select(d => devices[d]).ToList();
        c.Devices = Devices.Select(d => devices.TryGetValue(d, out var x) ? x : d.Clone()).ToList();
        return c;
    }

    public DateTime Time = DateTime.Now;

    public bool WslInstalled = true;
    public string? WslError;
    public List<DistroInfo> Distros = new();
    public DistroInfo? Target;

    public StState St = StState.Unknown;
    public string? StError, StVersion, MyId, Url;
    public TimeSpan StUptime;
    public double InRate, OutRate;
    public List<FolderInfo> Folders = new();     // pastas visíveis (após filtros)
    public List<FolderInfo> AllFolders = new();
    public List<DeviceInfo> Devices = new();     // dispositivos visíveis (após filtros)
    public List<DeviceInfo> AllDevices = new();

    public bool WslRunning => Target?.Running == true;

    public int FoldersOk => Folders.Count(f => f.Status.Level == Level.Ok);
    public int DevicesOnline => Devices.Count(d => d.Connected);

    public Level WslLevel => !WslInstalled ? Level.Error : WslRunning ? Level.Ok : Level.Off;

    public Level StLevel => St switch
    {
        StState.Running => Level.Ok,
        StState.DistroStopped or StState.Unknown => Level.Off,
        _ => Level.Error,
    };

    /// <summary>
    /// Estado dos dispositivos como um todo: sincronizando se algum está; atenção só se nenhum está conectado
    /// (nada sincroniza); um desligado com outro conectado é normal. Sem dispositivos ativos: neutro.
    /// </summary>
    public Level DevicesLevel
    {
        get
        {
            // todos os dispositivos, inclusive os ocultos do painel: um conectado oculto também sincroniza
            var active = AllDevices.Where(d => !d.Paused).ToList();
            if (active.Count == 0) return Level.Off;
            if (active.Any(d => d.Status.Level == Level.Busy)) return Level.Busy;
            return active.Any(d => d.Connected) ? Level.Ok : Level.Warn;
        }
    }

    public Level Overall
    {
        get
        {
            if (!WslInstalled) return Level.Error;
            if (!WslRunning) return Level.Off;
            if (St != StState.Running) return Level.Error;
            var worst = Levels.Worst(Folders.Select(f => f.Status.Level).Append(DevicesLevel));
            return worst == Level.Off ? Level.Ok : worst; // nada pendente nem desligado conta: tudo ok
        }
    }

    public string StStatusText => St switch
    {
        StState.Running => $"Em execução{(StVersion != null ? " · " + StVersion : "")}",
        StState.DistroStopped => "WSL parado",
        StState.NotRunning => "Não está em execução",
        StState.Unreachable => "Sem resposta da API",
        StState.AuthError => "Chave da API inválida",
        _ => "Desconhecido",
    };
}

/// <summary>Precedência dos estados: erro, depois ocupado (sincronizando), atenção e ok.</summary>
public static class Levels
{
    static readonly Level[] Precedence = [Level.Error, Level.Busy, Level.Warn, Level.Ok];

    /// <summary>O estado mais importante da lista (sem nenhum: desligado).</summary>
    public static Level Worst(IEnumerable<Level> levels)
    {
        var set = levels.ToHashSet();
        return Precedence.FirstOrDefault(set.Contains, Level.Off);
    }
}

public static class Fmt
{
    // a interface é em português: números sempre no formato brasileiro, qualquer que seja o idioma do Windows
    static readonly System.Globalization.CultureInfo PtBr = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");

    public static string Bytes(double b)
    {
        string[] u = { "B", "KiB", "MiB", "GiB", "TiB" };
        int i = 0;
        while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? string.Format(PtBr, "{0:0} {1}", b, u[i]) : string.Format(PtBr, "{0:0.#} {1}", b, u[i]);
    }

    public static string Rate(double bps) => Bytes(bps) + "/s";

    public static string Ago(DateTime t)
    {
        var d = DateTime.Now - t.ToLocalTime();
        if (d.TotalSeconds < 60) return "agora";
        if (d.TotalMinutes < 60) return $"há {d.TotalMinutes:0} min";
        if (d.TotalHours < 24) return $"há {d.TotalHours:0} h";
        return $"há {d.TotalDays:0} d";
    }

    public static string Duration(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{t.Minutes}m {t.Seconds}s";
}
