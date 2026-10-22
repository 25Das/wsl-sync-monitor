namespace WslSyncMonitor;

/// <summary>Interpreta linhas de comando de processos wsl.exe (para saber quem está usando cada distro).</summary>
public static class WslCommandLine
{
    // opções do wsl.exe que só administram o WSL (não abrem sessão na distro)
    static readonly HashSet<string> AdminFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "-l", "--list", "--shutdown", "-t", "--terminate", "--status", "--version", "-v", "--update",
        "-s", "--set-default", "--set-version", "--set-default-version", "--install", "--unregister",
        "--export", "--import", "--import-in-place", "--mount", "--unmount", "--manage", "--help", "--debug-shell",
    };

    /// <summary>Argumentos sem o executável, respeitando aspas (o caminho do wsl.exe pode ter espaços).</summary>
    public static List<string> Arguments(string cmd)
    {
        var args = new List<string>();
        var cur = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in cmd)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted) { if (any) args.Add(cur.ToString()); cur.Clear(); any = false; }
            else { cur.Append(c); any = true; }
        }
        if (any) args.Add(cur.ToString());
        return args.Skip(1).ToList();
    }

    /// <summary>
    /// Distro em que o wsl.exe abre sessão; "" se só administra o WSL; null se não der para saber
    /// (sem linha de comando, ou sem -d e sem distro padrão conhecida): nesse caso conta como sessão.
    /// </summary>
    public static string? SessionTarget(string? cmd, string? defaultDistro)
    {
        if (cmd == null) return null;
        var tokens = Arguments(cmd);
        string? target = null;
        for (int i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t is "--" or "-e" or "--exec") break;          // daqui em diante é o comando Linux
            if (AdminFlags.Contains(t)) return "";
            if ((t is "-d" or "--distribution") && i + 1 < tokens.Count) target = tokens[++i];
        }
        return target ?? defaultDistro;
    }

    /// <summary>É o processo que o app cria para segurar a distro (atual, com pedidos, ou o antigo "sleep infinity")?</summary>
    public static bool IsKeepAlive(string? cmd, out string distro)
    {
        distro = "";
        if (cmd == null) return false;
        var t = Arguments(cmd);
        if (t.Count < 5 || t[0] != "-d" || t[2] != "--exec") return false;
        bool sleeper = t.Count == 5 && t[3] == "sleep" && t[4] == "infinity";            // versões até a 1.11
        bool withRequests = t.Count >= 6 && t[^1] == SyncthingScripts.KeepAliveMarker;    // atende pedidos (sync…)
        if (!sleeper && !withRequests) return false;
        distro = t[1];
        return true;
    }
}
