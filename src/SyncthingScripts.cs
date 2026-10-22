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

    // ordem: o que o processo em execução usa (--home/--config ou STHOMEDIR), `syncthing paths`, locais padrão
    const string Discover = """
        pid=$(pgrep -xo syncthing)
        if [ -n "$pid" ]; then
          d=$(tr '\0' '\n' < /proc/$pid/cmdline | awk 'p{print;exit} /^--?(home|config)$/{p=1;next} /^--?(home|config)=/{sub(/^--?[a-z]+=/,"");print;exit}')
          [ -z "$d" ] && d=$(tr '\0' '\n' < /proc/$pid/environ 2>/dev/null | sed -n 's/^STHOMEDIR=//p;s/^STCONFDIR=//p' | head -1)
          [ -n "$d" ] && emit "$d/config.xml"
        fi
        p=$(syncthing paths 2>/dev/null | awk '/^Configuration file/{getline; gsub(/^[ \t]+/,""); print; exit}')
        [ -n "$p" ] && emit "$p"
        emit "${XDG_STATE_HOME:-$HOME/.local/state}/syncthing/config.xml"
        emit "${XDG_CONFIG_HOME:-$HOME/.config}/syncthing/config.xml"
        emit "$HOME/snap/syncthing/common/.config/syncthing/config.xml"
        exit 0
        """;

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

    /// <summary>Script que lista os config.xml candidatos (ou só <paramref name="customPath"/>, se definido).</summary>
    public static string ListConfigs(string customPath) =>
        Emit + "\n" + (string.IsNullOrWhiteSpace(customPath)
            ? Discover
            : $"emit '{customPath.Replace("'", "'\\''")}'\nexit 0");
}
