namespace WslSyncMonitor;

/// <summary>
/// O widget da barra num thread de interface só dele. Dentro da barra de tarefas ele é janela filha de outro
/// processo (o Explorer), e isso liga a fila de entrada do thread dele à da barra: se esse thread ficasse ocupado,
/// a barra travaria junto (e vice-versa). Num thread que só desenha o widget, a interface principal do app pode
/// ficar ocupada (por exemplo, esperando a sincronização no desligamento) sem afetar a barra.
/// <para>
/// Tudo o que vai para o widget passa pelo contexto do thread dele; os eventos do widget voltam pelo contexto da
/// interface principal. O resto (visível, posição na tela) é lido de forma segura entre threads.
/// </para>
/// </summary>
public sealed class WidgetHost : IDisposable
{
    readonly SynchronizationContext _app;
    SynchronizationContext _widgetCtx = null!;
    TaskbarWidget _w = null!;
    volatile bool _menuOpen;

    public event Action? Clicked;
    public event Action? RightClicked;
    public event Action? HoverStarted;
    public event Action? OffsetChanged;

    public WidgetHost(Settings settings)
    {
        _app = SynchronizationContext.Current ?? throw new InvalidOperationException("criar na interface principal");
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            _w = new TaskbarWidget(settings) { YieldTo = () => _menuOpen };
            _w.Clicked += () => _app.Post(_ => Clicked?.Invoke(), null);
            _w.RightClicked += () => _app.Post(_ => RightClicked?.Invoke(), null);
            _w.HoverStarted += () => _app.Post(_ => HoverStarted?.Invoke(), null);
            _w.OffsetChanged += () => _app.Post(_ => OffsetChanged?.Invoke(), null);
            // o primeiro controle criado instala o contexto de sincronização do WinForms neste thread
            _widgetCtx = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            ready.Set();
            Application.Run();
        })
        { IsBackground = true, Name = "Widget da barra" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }

    void OnWidget(Action<TaskbarWidget> action) => _widgetCtx.Post(_ =>
    {
        if (!_w.IsDisposed) action(_w);
    }, null);

    public void SetSnapshot(Snapshot s) => OnWidget(w => w.SetSnapshot(s));
    public bool Busy { set => OnWidget(w => w.Busy = value); }

    /// <summary>Algum menu do app está aberto: o widget não se recoloca acima dele (modo por cima da barra).</summary>
    public bool MenuOpen { set => _menuOpen = value; }

    public void BringAboveTaskbar() => OnWidget(w => w.BringAboveTaskbar());

    /// <summary>O widget está à mostra (lido do estado da janela, seguro entre threads).</summary>
    public bool Visible => _w.IsShown;

    public Rectangle ScreenBounds => _w.ScreenBounds;
    public Point PopupAnchor => _w.PopupAnchor;

    public void Dispose() => OnWidget(w =>
    {
        w.Dispose();
        Application.ExitThread();
    });
}
