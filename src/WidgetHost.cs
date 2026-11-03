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
    TaskbarWidget? _w; // null se não deu para criar o widget (o app segue sem ele)
    volatile bool _menuOpen;

    public event Action? Clicked;
    public event Action? RightClicked;
    public event Action? HoverStarted;
    public event Action? HoverEnded;
    public event Action? OffsetChanged;

    public WidgetHost(Settings settings) : this(settings, () => new TaskbarWidget(settings)) { }

    /// <param name="create">Cria o widget (nos testes, simula falhas).</param>
    internal WidgetHost(Settings settings, Func<TaskbarWidget> create)
    {
        _app = SynchronizationContext.Current ?? throw new InvalidOperationException("criar na interface principal");
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            // contexto de sincronização deste thread (o widget é uma janela nativa: nenhum controle o instala)
            _widgetCtx = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_widgetCtx);
            TaskbarWidget? w = null;
            try
            {
                w = create();
                w.YieldTo = () => _menuOpen;
                w.Clicked += () => _app.Post(_ => Clicked?.Invoke(), null);
                w.RightClicked += () => _app.Post(_ => RightClicked?.Invoke(), null);
                w.HoverStarted += () => _app.Post(_ => HoverStarted?.Invoke(), null);
                w.HoverEnded += () => _app.Post(_ => HoverEnded?.Invoke(), null);
                w.OffsetChanged += () => _app.Post(_ => OffsetChanged?.Invoke(), null);
                w.Start(); // só depois de os eventos estarem ligados
                _w = w;
            }
            catch (Exception ex)
            {
                // uma exceção aqui, fora do loop de mensagens, derrubaria o app inteiro: o widget é só um enfeite
                Log.Error("widget", "iniciar o widget da barra (o app segue sem ele)", ex);
                w?.Dispose();
            }
            finally { ready.Set(); }
            if (_w != null) Application.Run();
        })
        { IsBackground = true, Name = "Widget da barra" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();
    }

    void OnWidget(Action<TaskbarWidget> action)
    {
        if (_w is not { } w) return; // sem widget: nada a fazer (e o thread dele já terminou)
        _widgetCtx.Post(_ =>
        {
            if (!w.IsDisposed) action(w);
        }, null);
    }

    public void SetSnapshot(Snapshot s) => OnWidget(w => w.SetSnapshot(s));
    public bool Busy { set => OnWidget(w => w.Busy = value); }

    /// <summary>Oculto enquanto a versão nova do app abre (atualização).</summary>
    public bool Suspended { set => OnWidget(w => w.Suspended = value); }

    /// <summary>Algum menu do app está aberto: o widget não se recoloca acima dele (modo por cima da barra).</summary>
    public bool MenuOpen { set => _menuOpen = value; }

    public void BringAboveTaskbar() => OnWidget(w => w.BringAboveTaskbar());

    /// <summary>O widget está à mostra (lido do estado da janela, seguro entre threads).</summary>
    public bool Visible => _w?.IsShown ?? false;

    public Rectangle ScreenBounds => _w?.ScreenBounds ?? Rectangle.Empty;
    public Point PopupAnchor => _w?.PopupAnchor ?? Point.Empty;

    public void Dispose() => OnWidget(w =>
    {
        w.Dispose();
        Application.ExitThread();
    });
}
