using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.Core.Definitions;
using FlaUI.UIA2;

namespace WslSyncMonitor.WindowsTests.E2E;

/// <summary>
/// O app em execução para os testes de ponta a ponta: pasta de dados temporária (WSLSYNCMONITOR_HOME), servidor
/// falso de atualizações (WSLSYNCMONITOR_UPDATE_URL) e capturas da tela em cada passo (EVIDENCE_DIR).
/// </summary>
public sealed class AppSession : IDisposable
{
    public readonly UIA2Automation Automation = new();
    public readonly Process Process;
    public readonly string Home = Path.Combine(Path.GetTempPath(), "wsm-e2e-" + Guid.NewGuid().ToString("N")[..8]);
    readonly HttpListener _server = new();
    readonly string _evidence = Environment.GetEnvironmentVariable("EVIDENCE_DIR") is { Length: > 0 } d
        ? d : Path.Combine(Path.GetTempPath(), "wsm-evidencias");

    /// <summary>Versão que o servidor falso anuncia como a mais recente.</summary>
    public volatile string LatestVersion = "0.0.1";

    public AppSession(string settingsJson)
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(_evidence);
        File.WriteAllText(Path.Combine(Home, "settings.json"), settingsJson);

        int port = FreePort();
        _server.Prefixes.Add($"http://127.0.0.1:{port}/");
        _server.Start();
        _ = ServeAsync();

        var psi = new ProcessStartInfo(AppExe()) { UseShellExecute = false };
        psi.Environment["WSLSYNCMONITOR_HOME"] = Home;
        psi.Environment["WSLSYNCMONITOR_UPDATE_URL"] = $"http://127.0.0.1:{port}/latest";
        Process = Process.Start(psi)!;
    }

    static string AppExe()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "WslSyncMonitor.exe");
        if (!File.Exists(exe)) throw new FileNotFoundException("WslSyncMonitor.exe não está na saída dos testes", exe);
        return exe;
    }

    static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    async Task ServeAsync()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); } catch { return; }
            var json = $$"""
                {"draft":false,"prerelease":false,"tag_name":"v{{LatestVersion}}","html_url":"http://127.0.0.1/release",
                 "assets":[{"name":"WslSyncMonitor.exe","browser_download_url":"http://127.0.0.1/nao-baixar","size":1}]}
                """;
            var body = Encoding.UTF8.GetBytes(json);
            ctx.Response.ContentType = "application/json";
            await ctx.Response.OutputStream.WriteAsync(body);
            ctx.Response.Close();
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);

    /// <summary>O widget visível: dentro da barra de tarefas (janela filha dela) ou, no modo antigo, por cima.</summary>
    public AutomationElement Widget(TimeSpan? timeout = null) => WaitFor(() =>
    {
        var tray = FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Shell_TrayWnd", null);
        var h = FindWindowEx(tray, IntPtr.Zero, null, "WSL Sync Monitor Widget");
        if (h == IntPtr.Zero) h = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "WSL Sync Monitor Widget");
        return h != IntPtr.Zero && IsWindowVisible(h) ? Automation.FromHandle(h) : null;
    }, timeout, "widget");

    /// <summary>Janela de nível superior do app com o título dado (espera até aparecer).</summary>
    public Window WaitWindow(string title, TimeSpan? timeout = null) =>
        WaitFor(() => Automation.GetDesktop()
                    .FindAllChildren(cf => cf.ByProcessId(Process.Id))
                    .FirstOrDefault(w => w.Name == title)?.AsWindow(),
                timeout, $"janela \"{title}\"");

    /// <summary>Itens de menu visíveis do app (cada menu aberto é uma janela de nível superior do processo).</summary>
    IEnumerable<AutomationElement> MenuItems() =>
        Automation.GetDesktop().FindAllChildren(cf => cf.ByProcessId(Process.Id))
            .SelectMany(w => w.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)))
            .Where(i => !i.IsOffscreen);

    /// <summary>Algum menu do app está aberto? (null = nenhum)</summary>
    public AutomationElement? OpenMenu() => MenuItems().FirstOrDefault();

    /// <summary>Item de menu visível cujo nome começa com <paramref name="prefix"/>, em qualquer menu aberto do app.</summary>
    public AutomationElement? MenuItem(string prefix) =>
        MenuItems().FirstOrDefault(i => (i.Name ?? "").StartsWith(prefix, StringComparison.Ordinal));

    /// <summary>Nomes dos itens visíveis (para a mensagem de erro quando algo não aparece).</summary>
    public string VisibleMenuItems() => string.Join(" | ", MenuItems().Select(i => i.Name));

    public static T WaitFor<T>(Func<T?> find, TimeSpan? timeout, string what) where T : class
    {
        var end = DateTime.Now + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTime.Now < end)
        {
            try { if (find() is { } found) return found; } catch { /* a interface ainda está mudando */ }
            Thread.Sleep(250);
        }
        throw new TimeoutException($"não apareceu: {what}");
    }

    /// <summary>Captura a tela inteira (como o usuário vê) para conferência visual.</summary>
    public void Screenshot(string name)
    {
        try
        {
            using var img = Capture.Screen();
            img.Bitmap.Save(Path.Combine(_evidence, $"e2e-{name}.png"), ImageFormat.Png);
        }
        catch { /* sem captura, o teste ainda vale */ }
    }

    public string LogText() =>
        Directory.Exists(Path.Combine(Home, "logs"))
            ? string.Join("\n", Directory.GetFiles(Path.Combine(Home, "logs")).Select(File.ReadAllText))
            : "";

    public void Dispose()
    {
        try { if (!Process.HasExited) Process.Kill(true); } catch { }
        try { File.WriteAllText(Path.Combine(_evidence, $"e2e-log-{Guid.NewGuid().ToString("N")[..6]}.txt"), LogText()); } catch { }
        _server.Close();
        Automation.Dispose();
        try { Directory.Delete(Home, recursive: true); } catch { }
    }
}
