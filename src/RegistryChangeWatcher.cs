using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;

namespace WslSyncMonitor;

/// <summary>
/// Avisa quando uma chave do registro (ou algo abaixo dela) muda: o Windows sinaliza (RegNotifyChangeKeyValue), sem
/// consultas. Usado na lista de distros do WSL (instalar, remover, trocar a padrão). Os dados continuam vindo do
/// wsl.exe; o registro só diz quando vale ler de novo. O aviso vem num thread do pool.
/// </summary>
public sealed class RegistryChangeWatcher : IDisposable
{
    const int REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    // o aviso não depende de o thread que pediu continuar existindo (Windows 8+): pode ser pedido de um thread do pool
    const int REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;

    [DllImport("advapi32.dll")]
    static extern int RegNotifyChangeKeyValue(SafeRegistryHandle key, [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        int filter, SafeWaitHandle signal, [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    readonly RegistryKey _key;
    readonly Action _changed;
    readonly AutoResetEvent _signal = new(false);
    readonly RegisteredWaitHandle _wait;
    readonly object _lock = new();
    bool _disposed;

    RegistryChangeWatcher(RegistryKey key, Action changed)
    {
        _key = key;
        _changed = changed;
        Arm();
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) => OnSignal(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    /// <summary>Passa a observar a chave, ou null se ela não existe (ex.: WSL não instalado).</summary>
    public static RegistryChangeWatcher? Open(RegistryHive hive, string path, Action changed)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        var key = root.OpenSubKey(path);
        return key == null ? null : new RegistryChangeWatcher(key, changed);
    }

    /// <summary>Pede o próximo aviso (cada pedido vale uma vez).</summary>
    void Arm()
    {
        int error = RegNotifyChangeKeyValue(_key.Handle, true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET | REG_NOTIFY_THREAD_AGNOSTIC,
            _signal.SafeWaitHandle, true);
        if (error != 0) throw new System.ComponentModel.Win32Exception(error);
    }

    void OnSignal()
    {
        lock (_lock)
        {
            if (_disposed) return;
            try { Arm(); } // antes de avisar: uma mudança enquanto quem recebe o aviso trabalha não se perde
            catch (System.ComponentModel.Win32Exception ex) { Log.Warn("wsl", "observar a lista de distros no registro", ex); return; }
        }
        _changed();
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        using (var done = new ManualResetEvent(false))
            if (_wait.Unregister(done)) done.WaitOne(); // espera um aviso em andamento terminar
        _key.Dispose(); // fechar a chave cancela o pedido pendente
        _signal.Dispose();
    }
}
