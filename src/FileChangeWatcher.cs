namespace WslSyncMonitor;

/// <summary>
/// Avisa quando um arquivo é criado, alterado ou trocado, em vez de conferir a data dele de tempo em tempo.
/// Gravações seguidas (editores gravam em vários passos) viram um aviso só, <c>debounce</c> depois da última.
/// O aviso vem num thread de fundo. A pasta precisa existir.
/// </summary>
public sealed class FileChangeWatcher : IDisposable
{
    readonly FileSystemWatcher _watcher;
    readonly System.Threading.Timer _debounce;
    readonly TimeSpan _delay;

    public FileChangeWatcher(string path, TimeSpan debounce, Action onChanged)
    {
        _delay = debounce;
        _debounce = new System.Threading.Timer(_ => onChanged(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(path)!, Path.GetFileName(path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += (_, _) => Restart();
        _watcher.Created += (_, _) => Restart();
        _watcher.Renamed += (_, _) => Restart(); // gravação "segura": escreve num temporário e troca pelo arquivo
        _watcher.EnableRaisingEvents = true;
    }

    void Restart() => _debounce.Change(_delay, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Dispose();
    }
}
