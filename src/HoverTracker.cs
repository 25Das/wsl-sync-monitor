namespace WslSyncMonitor;

/// <param name="Panel">estado do painel</param>
/// <param name="OverWidget">mouse sobre o widget</param>
/// <param name="OverPanel">mouse sobre o painel (com uma pequena margem)</param>
/// <param name="MenuOpen">o menu do app está aberto</param>
/// <param name="ButtonDown">algum botão do mouse pressionado</param>
/// <param name="OverMenu">mouse sobre um menu aberto (o principal ou o de uma linha do painel)</param>
public readonly record struct HoverInput(PanelState Panel, bool OverWidget, bool OverPanel, bool MenuOpen, bool ButtonDown, bool OverMenu);

public enum HoverAction { None, ShowPopup, MouseLeft, ClickOutside }

/// <summary>
/// Painel aberto pela passagem do mouse no widget. A entrada e a saída do mouse chegam como eventos da janela do
/// widget; o mouse só precisa ser conferido (<see cref="Tick"/>) enquanto o painel está aberto ou esperando o atraso
/// para abrir (<see cref="NeedsTicks"/>), não o tempo todo.
/// </summary>
public sealed class HoverTracker
{
    /// <summary>Quanto o mouse pode ficar fora do widget e do painel antes de o painel aberto pela passagem fechar.</summary>
    public const int LeaveGraceMs = 400;

    DateTime? _enteredAt;   // mouse entrou no widget e o painel vai abrir após o atraso
    DateTime? _outsideSince;

    /// <summary>O mouse entrou no widget (<paramref name="enabled"/>: abrir pela passagem está ligado e nada mais está aberto).</summary>
    public void WidgetEntered(DateTime now, bool enabled) => _enteredAt = enabled ? now : null;

    /// <summary>O mouse saiu do widget: não abre mais (se já abriu, quem decide fechar é <see cref="Tick"/>).</summary>
    public void WidgetLeft() => _enteredAt = null;

    /// <summary>Precisa conferir o mouse: painel aberto (fechar ao sair ou ao clicar fora) ou esperando para abrir.</summary>
    public bool NeedsTicks(PanelState panel) => panel.Visible || _enteredAt != null;

    public HoverAction Tick(DateTime now, HoverInput i, int delayMs)
    {
        var p = i.Panel;
        if (p.Visible && (p.Clicked || p.Locked))
        {
            // aberto por clique (ou fixado): clique fora fecha (reforço caso a janela não tenha recebido o foco)
            _enteredAt = null;
            return i.ButtonDown && !i.OverPanel && !i.OverMenu ? HoverAction.ClickOutside : HoverAction.None;
        }
        if (!p.Visible)
        {
            _outsideSince = null;
            if (_enteredAt is not { } entered) return HoverAction.None;
            if (!i.OverWidget || i.MenuOpen) { _enteredAt = null; return HoverAction.None; }
            if ((now - entered).TotalMilliseconds < delayMs) return HoverAction.None;
            _enteredAt = null;
            return HoverAction.ShowPopup;
        }
        // aberto pela passagem do mouse: fecha quando o mouse fica fora do widget e do painel por um tempo
        if (i.OverWidget || i.OverPanel) { _outsideSince = null; return HoverAction.None; }
        _outsideSince ??= now;
        if ((now - _outsideSince.Value).TotalMilliseconds <= LeaveGraceMs) return HoverAction.None;
        _outsideSince = null;
        return HoverAction.MouseLeft;
    }
}
