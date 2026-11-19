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

    [ShFact]
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

public class ProcessCheckTests
{
    [Theory]
    [InlineData("yes\n", true)]
    [InlineData("no\n", false)]
    [InlineData("", true)]                       // wsl.exe falhou: na dúvida, protege
    [InlineData("Erro: distro não encontrada", true)]
    public void So_um_no_explicito_conta_como_parado(string output, bool mayBeRunning) =>
        Assert.Equal(mayBeRunning, SyncthingParsers.ProcessMayBeRunning(output));

    // /rest/folder/errors: os arquivos que o Syncthing não conseguiu gravar e o motivo de cada um (antes, o app só
    // mostrava "8 erro(s)" e nenhum lugar dizia quais)

    [Fact]
    public void Arquivos_com_erro_da_pasta()
    {
        var list = SyncthingParsers.FolderErrors("""
            {"folder":"izdab-dr9wa","errors":[
              {"path":"projects/a/.lock","error":"opening temp file: permission denied"},
              {"path":"sessions/x:y.json","error":"invalid filename"}],"page":1,"perpage":65536}
            """);
        Assert.Equal([("projects/a/.lock", "opening temp file: permission denied"), ("sessions/x:y.json", "invalid filename")], list);
    }

    [Theory]
    [InlineData("""{"folder":"f","errors":null}""")]
    [InlineData("""{"folder":"f","errors":[]}""")]
    public void Pasta_sem_erros(string json) => Assert.Empty(SyncthingParsers.FolderErrors(json));

    [Fact]
    public void Resposta_ilegivel_nao_vira_lista_vazia()
    {
        // "nenhum erro" seria mentira: quem chama mostra a falha
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => SyncthingParsers.FolderErrors("<html>"));
    }

    [Theory]
    [InlineData("""{"errors":8,"pullErrors":8}""", 8)]   // versões em que "errors" repete "pullErrors"
    [InlineData("""{"errors":0,"pullErrors":3}""", 3)]
    [InlineData("""{"errors":2}""", 2)]
    public void Contagem_de_erros_da_pasta_sem_contar_duas_vezes(string json, int expected)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(expected, SyncthingApi.FolderErrorCount(doc.RootElement));
    }
}
