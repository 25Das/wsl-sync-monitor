using System.Runtime.InteropServices;
using FlaUI.Core.Input;

namespace WslSyncMonitor.WindowsTests.E2E;

/// <summary>
/// Barra de tarefas que se oculta sozinha: com o mouse parado em cima do widget, a barra (e o widget) devem ficar
/// à mostra. O teste liga o auto-ocultar no runner do CI e o desliga no fim.
/// </summary>
[Collection("E2E")]
public sealed class AutoHideTaskbarTests : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint uCallbackMessage; public uint uEdge; public RECT rc; public IntPtr lParam; }
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("shell32.dll")] static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string? title);

    const uint ABM_GETSTATE = 4, ABM_SETSTATE = 10;
    const int ABS_AUTOHIDE = 1;
    readonly IntPtr _previous;

    static void SetState(IntPtr state)
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null), lParam = state };
        SHAppBarMessage(ABM_SETSTATE, ref d);
    }

    public AutoHideTaskbarTests()
    {
        var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>() };
        _previous = SHAppBarMessage(ABM_GETSTATE, ref d);
        if (Environment.GetEnvironmentVariable("WSM_E2E") == "1") SetState(ABS_AUTOHIDE);
    }

    public void Dispose() { if (Environment.GetEnvironmentVariable("WSM_E2E") == "1") SetState(_previous); }

    [E2EFact]
    public void Mouse_parado_sobre_o_widget_mantem_a_barra_e_o_widget_a_mostra()
    {
        using var app = new AppSession("""{ "SettingsVersion": 2, "ShowTaskbarWidget": true, "InstallPromptDismissed": true }""");
        try
        {
            var widget = app.WaitWindow("WSL Sync Monitor Widget", TimeSpan.FromSeconds(30));
            var hwnd = widget.Properties.NativeWindowHandle.Value;
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;

            // leva o mouse à borda de baixo: a barra aparece, e o widget com ela
            Mouse.MoveTo(screen.Width / 2, screen.Bottom - 1);
            AppSession.WaitFor(() => IsWindowVisible(hwnd) ? "ok" : null, TimeSpan.FromSeconds(10), "widget com a barra à mostra");
            Thread.Sleep(800);

            // e para em cima do widget
            GetWindowRect(hwnd, out var r);
            Mouse.MoveTo((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);
            app.Screenshot("6-auto-ocultar-mouse-no-widget");

            int changes = 0;
            bool last = IsWindowVisible(hwnd);
            var hiddenAt = new List<string>();
            for (int i = 0; i < 80; i++) // 8 s
            {
                Thread.Sleep(100);
                bool now = IsWindowVisible(hwnd);
                if (now != last) { changes++; hiddenAt.Add($"{i * 100} ms: {(now ? "voltou" : "sumiu")}"); }
                last = now;
            }
            app.Screenshot("7-auto-ocultar-depois-de-8s");
            Assert.True(changes == 0 && last,
                $"o widget mudou {changes} vez(es) com o mouse parado sobre ele: {string.Join(", ", hiddenAt)}\n{app.LogText()}");
        }
        catch (Exception ex) when (ex is not Xunit.Sdk.XunitException)
        {
            app.Screenshot("falha-auto-ocultar");
            throw;
        }
    }
}
