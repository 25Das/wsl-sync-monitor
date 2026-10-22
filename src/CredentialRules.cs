namespace WslSyncMonitor;

/// <summary>Quando ler o config.xml dentro da distro e o que fazer com a chave quando a API a recusa.</summary>
public static class CredentialRules
{
    /// <summary>Sem chave, o config.xml é relido no máximo uma vez neste intervalo.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(1);

    /// <param name="allowDistroCommands">
    /// false depois que a distro foi liberada para desligar: nada roda nela (cada comando adiaria o
    /// desligamento por ociosidade ou religaria uma distro que acabou de parar).
    /// </param>
    public static bool ShouldReadConfig(bool haveKey, DateTime lastRead, DateTime now, bool allowDistroCommands) =>
        allowDistroCommands && !haveKey && now - lastRead >= RetryInterval;

    /// <summary>
    /// Com a distro em uso, descarta a chave recusada para relê-la. Com a distro liberada não há como reler,
    /// então a chave é mantida (descartá-la deixaria o app sem nenhuma até a distro voltar a ser usada).
    /// </summary>
    public static bool DiscardKeyOnAuthError(bool allowDistroCommands) => allowDistroCommands;
}
