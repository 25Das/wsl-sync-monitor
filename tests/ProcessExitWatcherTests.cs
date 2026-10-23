using System.Diagnostics;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

/// <summary>Avisa na hora quando uma sessão do WSL (processo wsl.exe) fecha, em vez de esperar a próxima conferência.</summary>
public sealed class ProcessExitWatcherTests : IDisposable
{
    readonly ProcessExitWatcher _w = new();
    readonly List<Process> _started = new();

    public void Dispose()
    {
        _w.Dispose();
        foreach (var p in _started) { try { p.Kill(); } catch (InvalidOperationException) { } p.Dispose(); }
    }

    Process Start(string seconds)
    {
        var p = Process.Start(new ProcessStartInfo("sleep", seconds))!;
        _started.Add(p);
        return p;
    }

    [ShFact]
    public void Avisa_quando_um_processo_observado_termina()
    {
        using var exited = new ManualResetEventSlim();
        _w.Exited += exited.Set;
        _w.Watch([Start("0.2").Id]);
        Assert.True(exited.Wait(TimeSpan.FromSeconds(5)));
    }

    [ShFact]
    public void Processo_que_deixou_de_contar_nao_avisa()
    {
        using var exited = new ManualResetEventSlim();
        _w.Exited += exited.Set;
        var p = Start("0.5");
        _w.Watch([p.Id]);
        _w.Watch([]); // não é mais uma sessão da distro (ex.: mudou de distro)
        p.WaitForExit();
        Assert.False(exited.Wait(TimeSpan.FromMilliseconds(500)));
        Assert.Equal(0, _w.Count);
    }

    [ShFact]
    public void Observar_de_novo_o_mesmo_processo_nao_duplica()
    {
        var p = Start("5");
        _w.Watch([p.Id]);
        _w.Watch([p.Id]);
        Assert.Equal(1, _w.Count);
    }

    [Fact]
    public void Processo_que_ja_terminou_e_ignorado()
    {
        _w.Watch([int.MaxValue - 7]); // não existe
        Assert.Equal(0, _w.Count);
        Assert.True(_w.AllWatched); // terminar não é falha: não há o que esperar
    }
}
