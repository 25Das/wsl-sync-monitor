using System.Diagnostics;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>
/// Desligamento cancelado depois de o app parar o Syncthing: o app tenta ligá-lo de novo pelo serviço do systemd do
/// usuário (o jeito comum de instalar). Os testes usam um systemctl falso que registra o que foi pedido.
/// </summary>
public sealed class StartSyncthingScriptTests : IDisposable
{
    readonly string _bin = Path.Combine(Path.GetTempPath(), "wsm-systemctl-" + Guid.NewGuid().ToString("N"));
    string Calls => Path.Combine(_bin, "calls");

    public StartSyncthingScriptTests() => Directory.CreateDirectory(_bin);
    public void Dispose() => Directory.Delete(_bin, recursive: true);

    void FakeSystemctl(bool unitExists, bool startWorks)
    {
        var path = Path.Combine(_bin, "systemctl");
        File.WriteAllText(path, $$"""
            #!/bin/sh
            echo "$* XDG=$XDG_RUNTIME_DIR" >> {{Calls}}
            case "$2" in
              cat) exit {{(unitExists ? 0 : 1)}} ;;
              start) exit {{(startWorks ? 0 : 1)}} ;;
            esac
            exit 2
            """.Replace("\r", ""));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    string Run()
    {
        var psi = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(SyncthingScripts.StartSyncthingService);
        psi.Environment["PATH"] = _bin + ":/usr/bin:/bin";
        psi.Environment.Remove("XDG_RUNTIME_DIR"); // como no wsl.exe --exec: sem sessão de login
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return output;
    }

    [ShFact]
    public void Liga_pelo_servico_do_usuario()
    {
        FakeSystemctl(unitExists: true, startWorks: true);
        Assert.Equal("started", Run());
        // sem sessão de login, o systemctl --user só acha o serviço com o XDG_RUNTIME_DIR do usuário
        const string prefix = "--user start syncthing.service XDG=/run/user/";
        var last = File.ReadAllLines(Calls)[^1];
        Assert.StartsWith(prefix, last, StringComparison.Ordinal);
        Assert.True(uint.TryParse(last[prefix.Length..], out _), last);
    }

    [ShFact]
    public void Sem_o_servico_nao_inventa()
    {
        FakeSystemctl(unitExists: false, startWorks: true);
        Assert.Equal("no-unit", Run());
        Assert.DoesNotContain(File.ReadAllLines(Calls), c => c.Contains("start", StringComparison.Ordinal));
    }

    [ShFact]
    public void Falha_ao_ligar_e_informada()
    {
        FakeSystemctl(unitExists: true, startWorks: false);
        Assert.Equal("failed", Run());
    }

    [Theory]
    [InlineData("started", true)]
    [InlineData("no-unit", false)]
    [InlineData("no-systemd", false)]
    [InlineData("failed", false)]
    [InlineData("", false)]
    public void Resultado_por_extenso(string output, bool ok)
    {
        var (started, text) = SyncthingScripts.DescribeStart(output);
        Assert.Equal(ok, started);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }
}
