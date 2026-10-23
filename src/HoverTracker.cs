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
/// Painel aberto pela passagem do mouse no widget. A entrada e a saída do mouse chegam como avisos das janelas do
/// widget e do painel; o mouse só é conferido (<see cref="Tick"/>) nesses avisos e nos instantes que
/// <see cref="NextCheck"/> indica (fim do atraso para abrir, fim da tolerância para fechar), não de tempos em tempos.
/// </summary>
public sealed class HoverTracker
{
    /// <summary>Quanto o mouse pode ficar fora do widget e do painel antes de o painel aberto pela passagem fechar.</summary>
    public const int LeaveGraceMs = 400;

    DateTime? _enteredAt;   // mouse entrou no widget e o painel vai abrir após o atraso
    DateTime? _outsideSince;
    bool _uncertain;        // o painel ou o widget avisou a saída do mouse, mas ele ainda estava na área (ex.: sobre uma dica)

    /// <summary>Com o painel aberto por clique e sem o foco (o Windows pode negá-lo), de quanto em quanto conferir o clique fora.</summary>
    public static readonly TimeSpan ClickOutsideCheck = TimeSpan.FromMilliseconds(100);

    /// <summary>O mouse entrou no widget (<paramref name="enabled"/>: abrir pela passagem está ligado e nada mais está aberto).</summary>
    public void WidgetEntered(DateTime now, bool enabled) => _enteredAt = enabled ? now : null;

    /// <summary>O mouse saiu do widget: não abre mais (se já abriu, quem decide fechar é <see cref="Tick"/>).</summary>
    public void WidgetLeft() => _enteredAt = null;

    /// <summary>O painel ou o widget avisou que o mouse saiu.</summary>
    public void LeaveSeen() => _uncertain = true;

    /// <summary>O painel ou o widget avisou que o mouse entrou: os avisos de saída voltam a valer.</summary>
    public void MouseEntered() => _uncertain = false;

    /// <summary>
    /// Daqui a quanto conferir o mouse de novo, ou null se os avisos bastam: o fim do atraso para abrir, o fim da
    /// tolerância depois de o mouse sair, uma saída ainda não confirmada, ou o clique fora com o painel sem o foco
    /// (com o foco, clicar fora tira o foco e isso fecha o painel).
    /// </summary>
    public TimeSpan? NextCheck(DateTime now, PanelState panel, bool panelFocused, int delayMs)
    {
        if (!panel.Visible) return _enteredAt is { } e ? Max0(e.AddMilliseconds(delayMs) - now) : null;
        if (panel.Clicked || panel.Locked) return panelFocused ? null : ClickOutsideCheck;
        if (_outsideSince is { } o) return Max0(o.AddMilliseconds(LeaveGraceMs + 1) - now);
        return _uncertain ? TimeSpan.FromMilliseconds(LeaveGraceMs) : null;
    }

    static TimeSpan Max0(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;


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
            _uncertain = false;
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
        _uncertain = false;
        return HoverAction.MouseLeft;
    }
}
