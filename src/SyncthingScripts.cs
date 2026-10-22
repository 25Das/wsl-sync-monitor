namespace WslSyncMonitor;

/// <summary>Scripts de shell executados dentro da distro (sem dependências, para poder ser testado fora do Windows).</summary>
public static class SyncthingScripts
{
    // imprime "@@FILE <caminho>" seguido do conteúdo de cada config.xml legível, sem repetir arquivos
    const string Emit = """
        seen=""
        emit() {
          f=$(readlink -f "$1" 2>/dev/null) || return 0
          [ -r "$f" ] || return 0
          case " $seen " in *" $f "*) return 0;; esac
          seen="$seen $f"
          printf '@@FILE %s\n' "$f"; cat "$f"; printf '\n'
        }
        """;

    // locais padrão, do atual (Syncthing 1.27+) para os antigos; outro local: SyncthingConfigPath nas configurações
    const string Discover = """
        emit "${XDG_STATE_HOME:-$HOME/.local/state}/syncthing/config.xml"
        emit "${XDG_CONFIG_HOME:-$HOME/.config}/syncthing/config.xml"
        emit "$HOME/snap/syncthing/common/.config/syncthing/config.xml"
        exit 0
        """;

    /// <summary>Arquivos em conflito do Syncthing numa pasta (fora das versões antigas), no máximo 200.</summary>
    public static string ListConflicts(string folderPath) =>
        ForSh($"find {WslPaths.ShQuote(folderPath)} -name '*.sync-conflict-*' -not -path '*/.stversions/*' 2>/dev/null | head -200");

    /// <summary>Espaço do disco da distro: "df -B1 --output=size,avail /".</summary>
    public const string DiskUsage = "df -B1 --output=size,avail /";

    /// <summary>Pasta home do usuário padrão da distro.</summary>
    public const string Home = "printf '%s' \"$HOME\"";

    /// <summary>Interpreta a saída de <see cref="ListConfigs"/>: caminho e conteúdo de cada config.xml válido.</summary>
    public static List<(string Path, string Xml)> ParseConfigs(string output)
    {
        var list = new List<(string, string)>();
        foreach (var part in output.Split("@@FILE ", StringSplitOptions.RemoveEmptyEntries))
        {
            int nl = part.IndexOf('\n');
            if (nl <= 0) continue;
            var xml = part[(nl + 1)..];
            if (xml.Contains("<configuration")) list.Add((part[..nl].TrimEnd('\r'), xml));
        }
        return list;
    }

    /// <summary>
    /// Deixa o script com quebras de linha do Linux. Compilado no Windows (git com autocrlf), o texto dos scripts
    /// no código vem com CRLF, e o sh não entende "then\r" nem "fi\r".
    /// </summary>
    public static string ForSh(string script) => script.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Script que lista os config.xml candidatos (ou só <paramref name="customPath"/>, se definido).</summary>
    public static string ListConfigs(string customPath) =>
        ForSh(Emit + "\n" + (string.IsNullOrWhiteSpace(customPath)
            ? Discover
            : $"emit '{customPath.Replace("'", "'\\''")}'\nexit 0"));
}
