using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WslSyncMonitor;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; public Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom); }

    [StructLayout(LayoutKind.Sequential)]
    public struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    public const uint ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5;
    public const int ABS_AUTOHIDE = 1;

    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    public const int SW_HIDE = 0, SW_SHOWNA = 8;
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    public const uint GW_HWNDPREV = 3;
    [DllImport("user32.dll")] public static extern IntPtr SetCapture(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ReleaseCapture();

    [StructLayout(LayoutKind.Sequential)]
    struct TRACKMOUSEEVENT { public int cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
    [DllImport("user32.dll")] static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT e);

    /// <summary>Pede WM_MOUSELEAVE quando o mouse sair da janela.</summary>
    public static void TrackMouseLeave(IntPtr hwnd)
    {
        var e = new TRACKMOUSEEVENT { cbSize = Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = 0x2 /* TME_LEAVE */, hwndTrack = hwnd };
        TrackMouseEvent(ref e);
    }

    public const int WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205,
        WM_MOUSELEAVE = 0x2A3, WM_CAPTURECHANGED = 0x215, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3, WM_CONTEXTMENU = 0x7B;
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);

    public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
    public const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;

    // ---- CreateProcess sem herança de handles ----
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    // CreateProcess exige uma linha de comando gravável; o StringBuilder é a forma mais simples (CA1838 é só desempenho)
#pragma warning disable CA1838
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcess(string? app, System.Text.StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string? dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
#pragma warning restore CA1838
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    const uint CREATE_NO_WINDOW = 0x08000000;

    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint exitCode);

    /// <summary>
    /// Processo iniciado sem herdar handles. Guarda o handle do processo (e não só o PID), para que
    /// "terminou?" e "encerrar" valham sempre para este processo, mesmo que o PID seja reutilizado.
    /// </summary>
    public sealed class DetachedProcess : IDisposable
    {
        readonly Microsoft.Win32.SafeHandles.SafeProcessHandle _handle;
        public int Id { get; }

        DetachedProcess(IntPtr handle, int id) { _handle = new(handle, ownsHandle: true); Id = id; }

        public bool HasExited => _handle.IsClosed || WaitForSingleObject(_handle.DangerousGetHandle(), 0) == 0;

        public void Kill() { if (!HasExited) TerminateProcess(_handle.DangerousGetHandle(), 1); }

        public void Dispose() => _handle.Dispose();

        /// <summary>Abre um processo já existente (para poder esperar e encerrar), ou null.</summary>
        public static DetachedProcess? Open(int pid)
        {
            var h = OpenProcess(SYNCHRONIZE | PROCESS_TERMINATE | PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            return h == IntPtr.Zero ? null : new DetachedProcess(h, pid);
        }

        public static DetachedProcess Start(string commandLine)
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (!CreateProcess(null, new System.Text.StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    CREATE_NO_WINDOW, IntPtr.Zero, null, ref si, out var pi))
                throw new System.ComponentModel.Win32Exception();
            CloseHandle(pi.hThread);
            return new DetachedProcess(pi.hProcess, pi.dwProcessId);
        }
    }

    // ---- linha de comando de outro processo ----
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr h, int cls, IntPtr buf, int len, out int retLen);
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x100000, PROCESS_TERMINATE = 0x1;
    const int ProcessCommandLineInformation = 60;

    /// <summary>Lê a linha de comando de um processo (Windows 8.1+). null se não for possível.</summary>
    public static string? GetCommandLine(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            // primeira chamada só descobre o tamanho ("falha" com STATUS_INFO_LENGTH_MISMATCH, esperado)
            _ = NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int len);
            if (len <= 0) return null;
            var buf = Marshal.AllocHGlobal(len);
            try
            {
                if (NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, len, out _) != 0) return null;
                // UNICODE_STRING { ushort Length; ushort MaximumLength; IntPtr Buffer }
                int bytes = Marshal.ReadInt16(buf) & 0xFFFF;
                var str = Marshal.ReadIntPtr(buf, IntPtr.Size);
                return str == IntPtr.Zero ? null : Marshal.PtrToStringUni(str, bytes / 2);
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(h); }
    }

    // ---- desligamento do Windows ----
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string reason);
    [DllImport("user32.dll")] public static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
    [DllImport("kernel32.dll")] public static extern bool SetProcessShutdownParameters(uint level, uint flags);
    public const int WM_QUERYENDSESSION = 0x11, WM_ENDSESSION = 0x16, WM_SETICON = 0x80, ICON_SMALL = 0, ICON_BIG = 1;
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_TOP = IntPtr.Zero;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    // ---- janela em camadas (alpha por pixel) ----
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    // pptDst é um POINT* opcional (IntPtr.Zero = mantém a posição)
    [DllImport("user32.dll", SetLastError = true)] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, IntPtr pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr hObject);
    public const int ULW_ALPHA = 2;
    public const byte AC_SRC_OVER = 0, AC_SRC_ALPHA = 1;

    public const int WS_EX_LAYERED = 0x80000, WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x8000000;
    public const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000, WS_POPUP = unchecked((int)0x80000000);

    /// <summary>
    /// Exibe um bitmap ARGB 32bpp numa janela WS_EX_LAYERED; <paramref name="position"/> null mantém a posição
    /// (janela filha, já posicionada).
    /// </summary>
    public static void SetLayeredBitmap(IntPtr hwnd, Bitmap bmp, Point? position)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero), memDc = CreateCompatibleDC(screenDc);
        IntPtr hBmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBmp = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBmp);
            var size = new SIZE { cx = bmp.Width, cy = bmp.Height };
            var src = new POINT();
            var blend = new BLENDFUNCTION { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };
            IntPtr dst = IntPtr.Zero;
            try
            {
                if (position is { } p)
                {
                    dst = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
                    Marshal.StructureToPtr(new POINT { X = p.X, Y = p.Y }, dst, false);
                }
                if (!UpdateLayeredWindow(hwnd, screenDc, dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA))
                    Log.Failure("widget", "desenho", $"o Windows recusou o desenho do widget (erro {Marshal.GetLastWin32Error()})");
                else Log.Recovered("widget", "desenho", "desenho do widget voltou a funcionar");
            }
            finally { if (dst != IntPtr.Zero) Marshal.FreeHGlobal(dst); }
        }
        finally
        {
            _ = ReleaseDC(IntPtr.Zero, screenDc);
            if (hBmp != IntPtr.Zero) { SelectObject(memDc, old); DeleteObject(hBmp); }
            DeleteDC(memDc);
        }
    }
}

public enum TaskbarEdge { Left = 0, Top = 1, Right = 2, Bottom = 3 }

/// <summary>Posição/estado atual da barra de tarefas principal (inclui auto‑ocultar).</summary>
public sealed class TaskbarInfo
{
    public IntPtr Hwnd;
    public TaskbarEdge Edge = TaskbarEdge.Bottom;
    public Rectangle Rect;          // posição atual da janela (desliza para fora da tela quando oculta)
    public Rectangle ScreenBounds;
    public Rectangle? NotifyRect;   // área de notificação (bandeja)
    public bool AutoHide;
    public bool FullscreenApp;
    public int NotificationState; // SHQueryUserNotificationState (para o log)
    public int Dpi = 96;

    public bool Horizontal => Edge is TaskbarEdge.Top or TaskbarEdge.Bottom;
    public int Thickness => Horizontal ? Rect.Height : Rect.Width;

    /// <summary>A barra está totalmente exibida na tela agora?</summary>
    public bool FullyShown
    {
        get
        {
            if (Hwnd == IntPtr.Zero || !Native.IsWindowVisible(Hwnd)) return false;
            var inter = Rectangle.Intersect(Rect, ScreenBounds);
            return Horizontal ? inter.Height >= Rect.Height - 2 : inter.Width >= Rect.Width - 2;
        }
    }

    public static TaskbarInfo Get()
    {
        var t = new TaskbarInfo { Hwnd = Native.FindWindow("Shell_TrayWnd", null) };
        var abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
        if (Native.SHAppBarMessage(Native.ABM_GETTASKBARPOS, ref abd) != IntPtr.Zero)
        {
            t.Edge = (TaskbarEdge)abd.uEdge;
            t.Rect = abd.rc.ToRectangle();
        }
        abd = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>() };
        t.AutoHide = ((long)Native.SHAppBarMessage(Native.ABM_GETSTATE, ref abd) & Native.ABS_AUTOHIDE) != 0;

        if (t.Hwnd != IntPtr.Zero)
        {
            if (Native.GetWindowRect(t.Hwnd, out var r)) t.Rect = r.ToRectangle();
            try { t.Dpi = (int)Native.GetDpiForWindow(t.Hwnd); } catch { }
            var notify = Native.FindWindowEx(t.Hwnd, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr))
            {
                var rr = nr.ToRectangle();
                if (rr.Width > 0 && rr.Height > 0) t.NotifyRect = rr;
            }
        }
        if (t.Dpi <= 0) t.Dpi = 96;

        t.ScreenBounds = (t.Hwnd != IntPtr.Zero ? Screen.FromHandle(t.Hwnd) : Screen.PrimaryScreen!).Bounds;

        // 2 = QUNS_BUSY (app em tela cheia), 3 = D3D tela cheia, 4 = modo apresentação
        if (Native.SHQueryUserNotificationState(out var q) == 0)
        {
            t.NotificationState = (int)q;
            t.FullscreenApp = q is 2 or 3 or 4;
        }
        return t;
    }

    /// <summary>
    /// Calcula onde abrir um painel ao lado da barra. Usa a espessura da barra em relação à borda da
    /// tela (não a posição atual), então funciona mesmo quando a barra está auto‑oculta.
    /// </summary>
    public Point PlacePopup(Size size, Point anchor, int margin)
    {
        var screen = Screen.FromPoint(anchor).Bounds;
        if (!screen.IntersectsWith(ScreenBounds)) screen = ScreenBounds;
        int thick = Thickness > 0 ? Thickness : 48;
        int x, y;
        switch (Edge)
        {
            case TaskbarEdge.Top:
                x = anchor.X - size.Width / 2; y = screen.Top + thick + margin; break;
            case TaskbarEdge.Left:
                x = screen.Left + thick + margin; y = anchor.Y - size.Height / 2; break;
            case TaskbarEdge.Right:
                x = screen.Right - thick - margin - size.Width; y = anchor.Y - size.Height / 2; break;
            default:
                x = anchor.X - size.Width / 2; y = screen.Bottom - thick - margin - size.Height; break;
        }
        x = Math.Clamp(x, screen.Left + margin, Math.Max(screen.Left + margin, screen.Right - size.Width - margin));
        y = Math.Clamp(y, screen.Top + margin, Math.Max(screen.Top + margin, screen.Bottom - size.Height - margin));
        return new Point(x, y);
    }
}

/// <summary>Cores e tema claro/escuro do Windows.</summary>
public static class Theme
{
    // lidos do registro só quando o tema muda (o widget consulta várias vezes por segundo)
    static bool? _appsLight, _taskbarLight;
    static Theme() => SystemEvents.UserPreferenceChanged += (_, _) => { _appsLight = null; _taskbarLight = null; };

    static bool ReadLight(string name)
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue(name) is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Força o tema claro (true) ou escuro (false) em vez do tema do Windows; usado nas capturas de tela do CI.</summary>
    internal static bool? ForceLight { get; set; }

    public static bool AppsLight => ForceLight ?? (_appsLight ??= ReadLight("AppsUseLightTheme"));
    public static bool TaskbarLight => ForceLight ?? (_taskbarLight ??= ReadLight("SystemUsesLightTheme"));

    public static Color For(Level l) => l switch
    {
        Level.Ok => Color.FromArgb(0x3F, 0xB9, 0x50),
        Level.Busy => Color.FromArgb(0x3B, 0x8E, 0xF6),
        Level.Warn => Color.FromArgb(0xE3, 0xA0, 0x08),
        Level.Error => Color.FromArgb(0xF8, 0x51, 0x49),
        _ => Color.FromArgb(0x8B, 0x94, 0x9E),
    };

    public sealed record Palette(Color Bg, Color Card, Color Border, Color Text, Color Dim, Color Accent, Color Hover);

    public static Palette Current => AppsLight
        ? new(Color.FromArgb(249, 249, 249), Color.White, Color.FromArgb(222, 222, 222), Color.FromArgb(28, 28, 28), Color.FromArgb(110, 110, 110), Color.FromArgb(0, 95, 184), Color.FromArgb(235, 235, 235))
        : new(Color.FromArgb(32, 32, 32), Color.FromArgb(43, 43, 43), Color.FromArgb(60, 60, 60), Color.FromArgb(240, 240, 240), Color.FromArgb(160, 160, 160), Color.FromArgb(96, 205, 255), Color.FromArgb(58, 58, 58));
}

/// <summary>
/// Onde estão os ícones da barra de tarefas (botão Iniciar, apps fixados e abertos, clima…). No Windows 11 a barra
/// é desenhada em XAML, sem uma janela por botão: só a automação de interface (UIA) enxerga os botões.
/// </summary>
static class TaskbarIcons
{
    static int? ExplorerSetting(string name)
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
        return k?.GetValue(name) is int v ? v : null;
    }

    /// <summary>
    /// Intervalos horizontais ocupados (fora da bandeja), ou null se não deu para ler. Inclui o canto do botão
    /// Widgets do Windows 11 (<paramref name="cornerWidth"/> a partir de <paramref name="left"/>) quando ele existe.
    /// </summary>
    public static List<(int Start, int End)>? Read(IntPtr taskbar, Rectangle? notify, int left, int cornerWidth)
    {
        if (taskbar == IntPtr.Zero) return null;
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(taskbar);
            var buttons = root.FindAll(System.Windows.Automation.TreeScope.Descendants,
                new System.Windows.Automation.PropertyCondition(System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Button));
            var list = new List<(int Start, int End)>();
            foreach (System.Windows.Automation.AutomationElement b in buttons)
            {
                var r = b.Current.BoundingRectangle;
                if (r.IsEmpty || r.Width <= 0 || double.IsInfinity(r.Left)) continue;
                if (notify is { } n && r.Left >= n.Left - 1) continue; // bandeja e relógio
                list.Add(((int)r.Left, (int)Math.Ceiling(r.Right)));
            }
            if (WidgetPlacement.WidgetsCornerReserved(Environment.OSVersion.Version.Build,
                    ExplorerSetting("TaskbarAl"), ExplorerSetting("TaskbarDa")))
                list.Add((left, left + cornerWidth));
            return list;
        }
        catch (Exception ex)
        {
            Log.Failure("widget", "icones", "não foi possível ler os ícones da barra: " + ex.Message);
            return null;
        }
    }
}
