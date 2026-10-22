namespace WslSyncMonitor;

public enum FooterAction { None, OpenSyncthing, StartDistro, StopDistro }

public sealed record FooterButton(string Label, FooterAction Action, bool Primary);

/// <summary>Botões do rodapé do painel, conforme o estado (sempre com o nome da distro, que é onde agem).</summary>
public static class PanelFooter
{
    public static IReadOnlyList<FooterButton> For(string? distro, bool running, bool syncthing, string? busy)
    {
        if (distro == null) return [];
        if (busy != null) return [new("Aguarde…", FooterAction.None, false)];
        if (!running) return [new($"Iniciar {distro}", FooterAction.StartDistro, true)];
        var stop = new FooterButton($"Encerrar {distro}", FooterAction.StopDistro, false);
        return syncthing ? [new("Abrir interface do Syncthing", FooterAction.OpenSyncthing, true), stop] : [stop];
    }
}
