namespace WslSyncMonitor;

/// <summary>Caminhos do Linux no Windows (Explorer) e no sh.</summary>
public static class WslPaths
{
    /// <summary>Caminho de pasta do Syncthing (absoluto, com ~ ou relativo ao home) como caminho absoluto do Linux.</summary>
    public static string ToAbsolute(string linuxPath, string home) =>
        linuxPath == "~" ? home
        : linuxPath.StartsWith("~/") ? home.TrimEnd('/') + linuxPath[1..]
        : linuxPath.StartsWith('/') ? linuxPath
        : home.TrimEnd('/') + "/" + linuxPath;

    /// <summary>O mesmo caminho no formato do Explorer (\\wsl.localhost\distro\...).</summary>
    public static string ToUnc(string distro, string linuxPath, string home) =>
        $@"\\wsl.localhost\{distro}" + ToAbsolute(linuxPath, home).Replace('/', '\\');

    /// <summary>Caminho entre aspas para o sh, expandindo ~ para "$HOME".</summary>
    public static string ShQuote(string path)
    {
        static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
        if (path == "~") return "\"$HOME\"";
        if (path.StartsWith("~/")) return "\"$HOME\"" + Q(path[1..]);
        return Q(path);
    }
}
