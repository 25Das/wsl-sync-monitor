using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;

namespace WslSyncMonitor.Tests;

/// <summary>Teste que precisa do Syncthing instalado (no CI do Linux, o workflow o instala).</summary>
public sealed class SyncthingFactAttribute : FactAttribute
{
    public SyncthingFactAttribute()
    {
        // no CI do Linux o Syncthing é instalado pelo workflow: lá o teste nunca é pulado (se faltar, falha)
        bool ci = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";
        if (!OperatingSystem.IsLinux() || (!SyncthingInstance.Installed && !ci)) Skip = "precisa do Syncthing instalado (Linux)";
    }
}

/// <summary>
/// Uma instância real do Syncthing, numa pasta temporária, só com uma pasta local "claude" (sem dispositivos,
/// sem descoberta nem relays). Detecta mudanças em 1 s (o padrão do Syncthing é 10 s).
/// </summary>
public sealed class SyncthingInstance : IDisposable
{
    public static bool Installed => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
        .Any(d => File.Exists(Path.Combine(d, "syncthing")));

    readonly string _root = Path.Combine(Path.GetTempPath(), "wsm-st-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Process _process;
    public readonly string Url, ApiKey = "chave-de-teste-" + Guid.NewGuid().ToString("N")[..8];
    public string FolderPath => Path.Combine(_root, "claude");
    public readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    public SyncthingInstance()
    {
        Directory.CreateDirectory(FolderPath);
        int gui = FreePort(), listen = FreePort();
        Url = $"http://127.0.0.1:{gui}";
        Run("generate", $"--home={Path.Combine(_root, "home")}").WaitForExit();
        _process = Run("serve", $"--home={Path.Combine(_root, "home")}", "--no-browser", "--no-restart",
            $"--gui-address=127.0.0.1:{gui}", $"--gui-apikey={ApiKey}");
        Http.DefaultRequestHeaders.Add("X-API-Key", ApiKey);

        Wait(() => Http.GetAsync(Url + "/rest/system/ping").Result.IsSuccessStatusCode, "o Syncthing responder");
        Send(HttpMethod.Patch, "/rest/config/options",
            $$"""{"listenAddresses":["tcp://127.0.0.1:{{listen}}"],"globalAnnounceEnabled":false,"localAnnounceEnabled":false,"relaysEnabled":false,"natEnabled":false,"urAccepted":-1}""");
        Send(HttpMethod.Post, "/rest/config/folders",
            $$"""{"id":"claude","label":"Claude","path":"{{FolderPath}}","fsWatcherEnabled":true,"fsWatcherDelayS":1}""");
        Wait(() => Http.GetFromJsonAsync<System.Text.Json.JsonElement>(Url + "/rest/db/status?folder=claude").Result
            .GetProperty("state").GetString() == "idle", "a pasta ficar parada");
    }

    static Process Run(params string[] args)
    {
        var psi = new ProcessStartInfo("syncthing") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    void Send(HttpMethod method, string path, string json)
    {
        using var req = new HttpRequestMessage(method, Url + path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        Http.SendAsync(req).Result.EnsureSuccessStatusCode();
    }

    public static void Wait(Func<bool> done, string what, int seconds = 30)
    {
        var end = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < end)
        {
            try { if (done()) return; } catch { /* ainda subindo */ }
            Thread.Sleep(200);
        }
        throw new TimeoutException("não aconteceu: " + what);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// Escreve um arquivo na pasta e pede a verificação na hora (o monitor de arquivos do Syncthing leva um
    /// instante para começar depois que a pasta fica pronta: sem isto, o teste dependeria de tempo).
    /// </summary>
    public void WriteFile(string name, string content)
    {
        File.WriteAllText(Path.Combine(FolderPath, name), content);
        Http.PostAsync(Url + "/rest/db/scan?folder=claude", null).Result.EnsureSuccessStatusCode();
    }

    /// <summary>Para o Syncthing como o desligamento faz (pela API).</summary>
    public void Stop()
    {
        try { Http.PostAsync(Url + "/rest/system/shutdown", null).Wait(5000); } catch { /* já parou */ }
        _process.WaitForExit(10_000);
    }

    public void Dispose()
    {
        Stop();
        try { if (!_process.HasExited) _process.Kill(true); } catch { }
        Http.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }
}
