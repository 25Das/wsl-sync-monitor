using System.Diagnostics;

namespace WslSyncMonitor;

/// <summary>
/// Avisa quando um dos processos observados termina (as sessões wsl.exe de outros programas na distro): a proteção
/// reage na hora em vez de esperar a próxima leitura. Seguro entre threads; <see cref="Exited"/> vem de outro thread.
/// </summary>
public sealed class ProcessExitWatcher : IDisposable
{
    readonly object _lock = new();
    readonly Dictionary<int, Process> _watched = new();

    public event Action? Exited;

    /// <summary>Passa a observar exatamente estes processos (os que saíram da lista deixam de ser observados).</summary>
    public void Watch(IEnumerable<int> pids)
    {
        var wanted = pids.ToHashSet();
        bool alreadyExited = false, allWatched = true;
        lock (_lock)
        {
            foreach (var gone in _watched.Keys.Where(id => !wanted.Contains(id)).ToList()) Forget(gone);
            foreach (var id in wanted.Where(id => !_watched.ContainsKey(id)))
            {
                try
                {
                    var p = Process.GetProcessById(id);
                    p.EnableRaisingEvents = true;
                    p.Exited += (_, _) => OnExited(id);
                    _watched[id] = p;
                    if (p.HasExited) { Forget(id); alreadyExited = true; } // terminou antes de começar a observar
                }
                catch (ArgumentException) { } // já terminou: nada a esperar
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    allWatched = false; // sem acesso (ex.: processo de administrador): o fim dele só a leitura periódica vê
                }
            }
            AllWatched = allWatched;
        }
        if (alreadyExited) Exited?.Invoke();
    }

    /// <summary>Todos os processos da última <see cref="Watch"/> estão sendo observados (nenhum recusou o acesso).</summary>
    public bool AllWatched { get; private set; } = true;

    /// <summary>Quantos processos estão sendo observados.</summary>
    public int Count { get { lock (_lock) return _watched.Count; } }

    void OnExited(int id)
    {
        lock (_lock) { if (!_watched.ContainsKey(id)) return; Forget(id); }
        Exited?.Invoke();
    }

    void Forget(int id)
    {
        if (_watched.Remove(id, out var p)) p.Dispose();
    }

    public void Dispose()
    {
        lock (_lock) foreach (var id in _watched.Keys.ToList()) Forget(id);
    }
}
