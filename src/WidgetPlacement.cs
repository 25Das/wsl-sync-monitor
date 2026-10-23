namespace WslSyncMonitor;

/// <summary>
/// Posição horizontal do widget numa barra horizontal. O lugar de sempre é junto da bandeja (menos o quanto o
/// usuário arrastou); se ali ele ficaria sobre os ícones da barra, vai para o primeiro espaço livre do outro lado;
/// sem espaço em lugar nenhum, fica no lugar de sempre (como antes).
/// </summary>
public static class WidgetPlacement
{
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
}
