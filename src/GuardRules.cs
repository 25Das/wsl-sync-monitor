namespace WslSyncMonitor;

public enum GuardState { Off, InUse, Verifying, Released, KeptAlive }

[Flags]
public enum GuardActions { None = 0, Hold = 1, StartVerify = 2, CancelVerify = 4, Release = 8 }

/// <param name="Running">a distro monitorada está ligada</param>
/// <param name="ProtectEnabled">"Esperar a sincronização antes de desligar" está ligada</param>
/// <param name="Busy">outra operação em andamento (iniciar, encerrar…)</param>
/// <param name="Sessions">sessões de outros programas na distro</param>
/// <param name="KeepAliveActive">"Manter ligada" vale agora para a distro</param>
/// <param name="IdleTicks">atualizações seguidas sem sessões</param>
public readonly record struct GuardInput(bool Running, bool ProtectEnabled, bool Busy, int Sessions, bool KeepAliveActive, int IdleTicks);

public readonly record struct GuardStep(GuardState State, GuardActions Actions, int IdleTicks);

/// <summary>O que a proteção da sincronização faz a cada atualização.</summary>
public static class GuardRules
{
    public static GuardStep Step(GuardState s, GuardInput i)
    {
        if (!i.Running)
            return new(GuardState.Off, s == GuardState.Off ? GuardActions.None : GuardActions.CancelVerify | GuardActions.Release, 0);

        if (i.KeepAliveActive) // vale mesmo com a proteção desligada: é um pedido explícito
            return new(GuardState.KeptAlive, GuardActions.Hold | (s == GuardState.Verifying ? GuardActions.CancelVerify : 0), 0);

        if (!i.ProtectEnabled)
            return new(GuardState.Off, s == GuardState.Off ? GuardActions.None : GuardActions.CancelVerify | GuardActions.Release, 0);

        if (i.Busy) return new(s, GuardActions.None, i.IdleTicks);

        if (i.Sessions > 0)
            return new(GuardState.InUse, GuardActions.Hold | (s == GuardState.Verifying ? GuardActions.CancelVerify : 0), 0);

        return s switch
        {
            // ligada por outro meio, ou "Manter ligada" acabou: verifica antes de soltar
            GuardState.Off or GuardState.KeptAlive => new(GuardState.Verifying, GuardActions.Hold | GuardActions.StartVerify, 0),
            // confirma que a sessão fechou mesmo (duas atualizações seguidas sem sessões)
            GuardState.InUse => i.IdleTicks + 1 >= 2
                ? new(GuardState.Verifying, GuardActions.Hold | GuardActions.StartVerify, 0)
                : new(GuardState.InUse, GuardActions.Hold, i.IdleTicks + 1),
            _ => new(s, GuardActions.None, 0),
        };
    }
}

/// <summary>"Manter ligada": até uma hora definida ou até a distro ser desligada.</summary>
public sealed record KeepAliveRequest(string Distro, DateTime? Until)
{
    public static KeepAliveRequest For(string distro, TimeSpan duration, DateTime now) => new(distro, now + duration);
    public static KeepAliveRequest UntilStopped(string distro) => new(distro, null);

    public bool IsActive(DateTime now) => Until == null || now < Until;
    public string Describe() => Until is { } u ? $"até {u:HH:mm}" : "até você desligar";

    /// <summary>Só "até você desligar" é salvo nas configurações (vale depois de reiniciar o app).</summary>
    public bool Persist => Until == null;

    /// <summary>Ao abrir o app: "até você desligar" liga a distro se ela estiver desligada (é o que o pedido quer dizer).</summary>
    public bool StartAtLaunch(DistroInfo? distro) =>
        Persist && distro is { Running: false } d && d.Name.Equals(Distro, StringComparison.OrdinalIgnoreCase);
}
