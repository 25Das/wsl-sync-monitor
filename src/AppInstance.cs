namespace WslSyncMonitor;

public enum ExitRequest
{
    NoneRunning,  // nenhuma instância em execução
    Exited,       // a instância em execução fechou quando pedido
    StillRunning, // pediu, mas ela ainda não fechou (provavelmente esperando a sincronização)
    NotListening, // versão anterior à 1.9, que não atende ao pedido: é preciso fechar pelo menu
}

/// <summary>
/// Uma instância por usuário (mutex) e o pedido para a instância em execução fechar (evento nomeado), usado ao
/// instalar e ao desinstalar. Os nomes são injetáveis para teste.
/// </summary>
public static class AppInstance
{
    public sealed record Names(string Mutex, string ExitEvent);

    public static readonly Names Default = new(@"Local\WslSyncMonitor.SingleInstance", @"Local\WslSyncMonitor.Exit");

    public static Mutex CreateMutex(Names? names = null) => new(false, (names ?? Default).Mutex);

    /// <summary>Tenta ser a instância única, esperando até <paramref name="wait"/> a anterior fechar.</summary>
    public static bool TryAcquire(Mutex mutex, TimeSpan wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; } // a anterior fechou sem liberar: assume
    }

    /// <summary>
    /// O mutex desta instância, que só a thread principal libera. Na atualização, a versão anterior o libera para a
    /// nova assumir e, se a nova falhar, o retoma (ver TrayApp.InstallUpdate).
    /// </summary>
    public sealed class Owner(Mutex mutex)
    {
        bool _owned;

        public bool TryAcquire(TimeSpan wait) => _owned = AppInstance.TryAcquire(mutex, wait);

        /// <summary>Libera, se ainda tem (chamar de novo não faz nada).</summary>
        public void Release()
        {
            if (!_owned) return;
            mutex.ReleaseMutex();
            _owned = false;
        }

        public bool Reacquire(TimeSpan wait) => _owned || TryAcquire(wait);
    }

    /// <summary>A instância deste processo (definida pelo Program).</summary>
    public static Owner? Current { get; set; }

    /// <summary>Evento que a instância em execução escuta; ao ser sinalizado, ela fecha com a saída segura.</summary>
    public static EventWaitHandle CreateExitEvent(Names? names = null) =>
        new(false, EventResetMode.AutoReset, (names ?? Default).ExitEvent);

    /// <summary>
    /// Pede à instância em execução para fechar e espera ela liberar o mutex. Em <see cref="ExitRequest.NoneRunning"/>
    /// e <see cref="ExitRequest.Exited"/>, quem chamou fica com o mutex (e deve liberá-lo).
    /// </summary>
    public static ExitRequest RequestExitAndWait(Mutex mutex, Names? names, TimeSpan wait)
    {
        if (TryAcquire(mutex, TimeSpan.Zero)) return ExitRequest.NoneRunning;
        if (!EventWaitHandle.TryOpenExisting((names ?? Default).ExitEvent, out var ev)) return ExitRequest.NotListening;
        using (ev) ev.Set();
        return TryAcquire(mutex, wait) ? ExitRequest.Exited : ExitRequest.StillRunning;
    }

    /// <summary>Mensagem para quando a instância em execução não fechou.</summary>
    public static string Explain(ExitRequest result) => result switch
    {
        ExitRequest.NotListening => "Há uma versão anterior do WSL Sync Monitor em execução. Feche-a pelo menu (Sair) e tente de novo.",
        ExitRequest.StillRunning => "O WSL Sync Monitor em execução ainda está esperando a sincronização para fechar. Tente de novo em alguns minutos.",
        _ => "",
    };
}
