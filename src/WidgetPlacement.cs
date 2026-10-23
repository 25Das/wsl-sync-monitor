namespace WslSyncMonitor;

/// <summary>
/// Posição horizontal do widget numa barra horizontal. O lugar de sempre é junto da bandeja (menos o quanto o
/// usuário arrastou); se ali ele ficaria sobre os ícones da barra, vai para o primeiro espaço livre do outro lado;
/// sem espaço em lugar nenhum, fica no lugar de sempre (como antes).
/// </summary>
public static class WidgetPlacement
{
    /// <summary>
    /// O widget deve aparecer? Senão, o motivo (para o log; null = escolha do usuário). Dentro da barra ele vai e
    /// volta com ela sozinho; por cima, some quando ela se recolhe ou quando há app em tela cheia.
    /// </summary>
    public static (bool Show, string? Reason) Visibility(bool enabled, bool inside, bool taskbarExists, int thickness,
        bool fullyShown, bool fullscreen, int notificationState)
    {
        if (!enabled) return (false, null);
        if (!taskbarExists) return (false, "a barra de tarefas ainda não existe (Windows iniciando?)");
        if (thickness <= 0) return (false, "a barra de tarefas não tem tamanho ainda");
        if (inside) return (true, null);
        if (fullscreen) return (false, $"o Windows informa app em tela cheia ou ocupado (estado {notificationState})");
        return fullyShown ? (true, null) : (false, "a barra de tarefas está oculta ou fora da tela");
    }

    /// <summary>
    /// Windows 11 com os ícones centralizados mostra o botão Widgets (clima) no canto esquerdo, e nem sempre a
    /// automação de interface o enxerga: o canto fica reservado. Valores do registro Explorer\Advanced:
    /// TaskbarAl (0 = ícones à esquerda) e TaskbarDa (0 = Widgets desligado); ausentes = padrão (centralizado, ligado).
    /// </summary>
    public static bool WidgetsCornerReserved(int windowsBuild, int? taskbarAlignment, int? widgetsButton) =>
        windowsBuild >= 22000 && taskbarAlignment != 0 && widgetsButton != 0;

    /// <param name="left">início da barra</param>
    /// <param name="right">início da bandeja (o widget fica à esquerda dela)</param>
    /// <param name="icons">intervalos [início, fim) ocupados pelos ícones da barra (botão Iniciar, apps, clima…)</param>
    /// <param name="offset">quanto o usuário arrastou o widget para longe da bandeja</param>
    public static int ChooseX(int left, int right, IReadOnlyList<(int Start, int End)> icons, int width, int gap, int offset)
    {
        int usual = right - width - gap;
        int dragged = Math.Clamp(usual - offset, left, Math.Max(left, right - width));
        if (icons.Count == 0) return dragged;

        bool Fits(int x) =>
            x >= left && x + width <= right &&
            icons.All(i => x + width + gap <= i.Start || x >= i.End + gap);

        if (Fits(dragged)) return dragged;
        if (Fits(usual)) return usual;

        // o outro lado: o primeiro espaço livre entre os ícones, da esquerda para a direita
        int start = left;
        foreach (var (s, e) in icons.OrderBy(i => i.Start))
        {
            if (Fits(start + gap)) return start + gap;
            start = Math.Max(start, e);
        }
        return dragged;
    }

    /// <summary>
    /// De quanto em quanto tempo o widget confere a barra além dos avisos do Windows: rede de segurança espaçada;
    /// por cima da barra que se oculta, acompanha a animação dela; sem barra (Explorer reiniciando), confere logo.
    /// </summary>
    public static int FollowIntervalMs(bool autoHide, bool inside, bool taskbarFound) =>
        !taskbarFound ? 500 : autoHide && !inside ? 150 : 2000;

    /// <summary>Espera da animação da barra depois de uma janela abrir ou fechar, antes de reler os ícones.</summary>
    public static readonly TimeSpan IconsSettle = TimeSpan.FromMilliseconds(500);

    /// <summary>Intervalo mínimo entre duas leituras dos ícones (muitas janelas abrindo e fechando).</summary>
    public static readonly TimeSpan IconsMinSpacing = TimeSpan.FromSeconds(1);

    /// <summary>Folga para o relógio, que avança em passos de ~16 ms: o timer da espera não pode chegar "cedo demais".</summary>
    static readonly TimeSpan ClockSlack = TimeSpan.FromMilliseconds(20);

    /// <summary>Releitura periódica dos ícones mesmo sem aviso (algo que os avisos não cobrem).</summary>
    public static readonly TimeSpan IconsMaxAge = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Hora de reler os ícones da barra: janelas abriram ou fecharam depois da última leitura (<paramref name="changedAt"/>
    /// é a primeira mudança ainda não lida, então uma rajada contínua não adia a releitura para sempre), a animação já
    /// acabou e a última leitura não foi há pouco; ou a leitura está velha.
    /// </summary>
    public static bool IconsReadDue(DateTime now, DateTime lastRead, DateTime? changedAt) =>
        (changedAt is { } c && c > lastRead && now - c >= IconsSettle - ClockSlack && now - lastRead >= IconsMinSpacing - ClockSlack)
        || now - lastRead >= IconsMaxAge;

    /// <summary>A janela tem botão na barra de tarefas (só essas mudam os ícones): de nível superior, sem dona e não
    /// "de ferramenta" (dicas, miniaturas, menus), a não ser que peça o botão (WS_EX_APPWINDOW).</summary>
    public static bool HasTaskbarButton(bool topLevel, bool owned, int exStyle)
    {
        const int toolWindow = 0x80, appWindow = 0x40000;
        if (!topLevel) return false;
        if ((exStyle & appWindow) != 0) return true;
        return !owned && (exStyle & toolWindow) == 0;
    }
}
