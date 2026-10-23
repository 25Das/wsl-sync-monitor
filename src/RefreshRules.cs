namespace WslSyncMonitor;

/// <summary>De quanto em quanto tempo o estado completo (WSL e Syncthing) é lido.</summary>
public static class RefreshRules
{
    /// <summary>
    /// O intervalo configurado, ou o triplo (no mínimo 15 s) quando nada depende dele: o fluxo de eventos traz as
    /// mudanças do Syncthing (e, se ele parar, o fluxo cai e a leitura é feita na hora), o painel está fechado (não
    /// mostra velocidades) e a proteção não espera algo que só a leitura vê (<see cref="GuardWatching"/>).
    /// </summary>
    public static TimeSpan Interval(int baseSeconds, bool eventsLive, bool panelOpen, bool guardWatching)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(2, baseSeconds));
        return eventsLive && !panelOpen && !guardWatching ? TimeSpan.FromSeconds(Math.Max(15, 3 * interval.TotalSeconds)) : interval;
    }

    /// <summary>
    /// A proteção espera algo que só a leitura periódica vê: está contando leituras seguidas sem sessões, há uma
    /// sessão aberta cujo fechamento não pode ser observado (ex.: terminal como administrador) ou "Manter ligada"
    /// por tempo vai expirar.
    /// </summary>
    public static bool GuardWatching(GuardState state, int idleTicks, bool sessionsWatched, bool timedKeepAlive) =>
        idleTicks > 0 || (state == GuardState.InUse && !sessionsWatched) || (state == GuardState.KeptAlive && timedKeepAlive);

    /// <summary>Quanto o processo que segura a distro precisa ter vivido para o fim dele pedir uma leitura na hora.</summary>
    public static readonly TimeSpan KeepAliveMinLife = TimeSpan.FromSeconds(10);

    /// <summary>
    /// O processo que segurava a distro terminou: lê na hora (a distro foi encerrada por fora), a não ser que ele tenha
    /// terminado logo ao iniciar (a distro não sobe): ler na hora religaria e cairia de novo, sem parar. Aí fica com a
    /// leitura periódica, como antes.
    /// </summary>
    public static bool RefreshOnKeepAliveExit(TimeSpan lived) => lived >= KeepAliveMinLife;

    /// <summary>
    /// Vale sondar o Syncthing a cada segundo (ele está para responder): a distro acabou de ligar, ou ele acabou de
    /// parar (reiniciando), ou o app acabou de abrir, e ele ainda não responde. Quem continua sem Syncthing não é sondado
    /// de novo (talvez nem use o Syncthing nesta distro).
    /// </summary>
    public static bool ShouldProbe(Snapshot? before, Snapshot after)
    {
        if (after.Target is not { Running: true } || after.St is not (StState.NotRunning or StState.Unreachable)) return false;
        return before == null || before.Target is not { Running: true } || before.St == StState.Running;
    }
}
