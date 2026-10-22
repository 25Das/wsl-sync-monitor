namespace WslSyncMonitor;

/// <param name="Visible">o painel está aberto</param>
/// <param name="Clicked">aberto por clique (senão, pela passagem do mouse)</param>
/// <param name="Locked">fixado pelo botão de pin: só fecha depois de liberado</param>
public readonly record struct PanelState(bool Visible, bool Clicked, bool Locked);

public enum PanelEvent
{
    ClickOutside,   // clique do mouse fora do painel
    Deactivated,    // o painel perdeu o foco
    Escape,         // tecla Esc
    MouseLeft,      // o mouse saiu do painel e do ícone/widget
    SourceClicked,  // clique no ícone da bandeja ou no widget
}

public enum PanelAction { None, Hide, ShowClicked, BringToFront }

/// <summary>Regras de quando o painel detalhado abre, fecha ou continua aberto.</summary>
public static class PanelBehavior
{
    public static PanelAction Decide(PanelState s, PanelEvent e)
    {
        if (!s.Visible) return e == PanelEvent.SourceClicked ? PanelAction.ShowClicked : PanelAction.None;
        if (s.Locked) return e == PanelEvent.SourceClicked ? PanelAction.BringToFront : PanelAction.None;
        if (!s.Clicked) // aberto pela passagem do mouse
            return e switch
            {
                PanelEvent.MouseLeft or PanelEvent.Escape => PanelAction.Hide,
                PanelEvent.SourceClicked => PanelAction.ShowClicked,
                _ => PanelAction.None,
            };
        return e == PanelEvent.MouseLeft ? PanelAction.None : PanelAction.Hide;
    }

    /// <summary>Botão de pin. Fixar também conta como aberto por clique, então liberar volta a esse comportamento.</summary>
    public static PanelState ToggleLock(PanelState s) => s with { Clicked = true, Locked = !s.Locked };
}
