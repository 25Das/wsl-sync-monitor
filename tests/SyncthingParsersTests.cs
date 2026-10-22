using System.Diagnostics;
using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class SyncthingParsersTests
{
    [Fact]
    public void Le_os_erros_recentes_do_Syncthing()
    {
        const string json = """
            {"errors":[{"when":"2026-10-22T14:01:02-03:00","message":"Failed to parse dialer address","level":2},
                       {"when":"2026-10-22T14:05:00-03:00","message":"Folder error","level":3}]}
            """;
        var errors = SyncthingParsers.Errors(json);

        Assert.Equal(["Failed to parse dialer address", "Folder error"], errors.Select(e => e.Message));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 14, 1, 2, TimeSpan.FromHours(-3)), errors[0].When);
    }

    [Theory]
    [InlineData("""{"errors":null}""")]
    [InlineData("""{"errors":[]}""")]
    [InlineData("não é json")]
    public void Sem_erros(string json) => Assert.Empty(SyncthingParsers.Errors(json));

    [Fact]
    public void Le_o_espaco_em_disco_do_df()
    {
        var (total, free) = SyncthingParsers.Df("  1B-blocks         Avail\n1081101176832 1016858365952\n")!.Value;
        Assert.Equal((1081101176832L, 1016858365952L), (total, free));
    }

    [Fact]
    public void Df_invalido() => Assert.Null(SyncthingParsers.Df("erro"));

    [Fact]
    public void Script_lista_arquivos_em_conflito_fora_das_versoes_antigas()
    {
        var root = Directory.CreateTempSubdirectory("wslsync-conflicts").FullName;
        try
        {
            var home = Directory.CreateDirectory(Path.Combine(root, "home")).FullName;
            var folder = Directory.CreateDirectory(Path.Combine(home, "pasta", "sub")).Parent!.FullName;
            File.WriteAllText(Path.Combine(folder, "a.txt"), "");
            File.WriteAllText(Path.Combine(folder, "sub", "b.sync-conflict-20261022-140102-ABCDEFG.txt"), "");
            Directory.CreateDirectory(Path.Combine(folder, ".stversions"));
            File.WriteAllText(Path.Combine(folder, ".stversions", "c.sync-conflict-20261022-140102-ABCDEFG.txt"), "");

            var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(SyncthingScripts.ListConflicts("~/pasta"));
            psi.Environment["HOME"] = home;
            using var p = Process.Start(psi)!;
            var lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            p.WaitForExit();

            Assert.Equal([Path.Combine(folder, "sub", "b.sync-conflict-20261022-140102-ABCDEFG.txt")], lines);
        }
        finally { Directory.Delete(root, true); }
    }
}
