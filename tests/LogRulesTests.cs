using WslSyncMonitor;

namespace WslSyncMonitor.Tests;

public class LogRulesTests
{
    static readonly DateTime At = new(2026, 10, 22, 14, 5, 9, 123);

    [Fact]
    public void Linha_tem_hora_nivel_area_e_mensagem() =>
        Assert.Equal("2026-10-22 14:05:09.123 AVISO [wsl] wsl.exe saiu com 1",
            LogRules.Format(At, LogLevel.Warn, "wsl", "wsl.exe saiu com 1"));

    [Fact]
    public void Mensagem_e_excecao_em_varias_linhas_ficam_recuadas()
    {
        var text = LogRules.Format(At, LogLevel.Error, "app", "falhou\nsegunda linha", new InvalidOperationException("detalhe"));
        var lines = text.Split('\n');
        Assert.Equal("2026-10-22 14:05:09.123 ERRO  [app] falhou", lines[0]);
        Assert.Equal("    segunda linha", lines[1]);
        Assert.StartsWith("    System.InvalidOperationException: detalhe", lines[2]);
        Assert.All(lines.Skip(1), l => Assert.StartsWith("    ", l));
    }

    [Fact]
    public void Segredos_nunca_vao_para_o_log()
    {
        var key = "abcDEF1234567890abcDEF1234567890";
        Assert.Equal("chave ••• lida", LogRules.Redact($"chave {key} lida", [key]));
        Assert.Equal("nada a esconder", LogRules.Redact("nada a esconder", ["", "ab"])); // curtos demais: ignorados
    }

    [Fact]
    public void Um_arquivo_por_dia_e_apaga_os_antigos()
    {
        Assert.Equal("app-20261022.log", LogRules.FileName(At));
        var files = new[] { "app-20261022.log", "app-20261016.log", "app-20261015.log", "app-20261001.log", "outro.txt", "app-xx.log" };
        Assert.Equal(["app-20261015.log", "app-20261001.log"], LogRules.ToDelete(files, At, keepDays: 7));
    }

    [Theory]
    [InlineData(LogLevel.Info, 100, true)]
    [InlineData(LogLevel.Info, 1000, false)]   // arquivo do dia cheio: informações param
    [InlineData(LogLevel.Warn, 1000, true)]    // avisos e erros continuam até o dobro
    [InlineData(LogLevel.Error, 2000, false)]
    public void Limite_de_tamanho_por_dia(LogLevel level, long size, bool write) =>
        Assert.Equal(write, LogRules.ShouldWrite(level, size, maxSize: 1000));

    [Fact]
    public void Falha_repetida_em_loop_e_registrada_uma_vez_e_a_volta_ao_normal_tambem()
    {
        var d = new LogDedup();
        Assert.True(d.ShouldLog("api", "sem resposta"));
        Assert.False(d.ShouldLog("api", "sem resposta"));
        Assert.True(d.ShouldLog("api", "chave recusada")); // mudou: registra
        Assert.True(d.Recovered("api"));
        Assert.False(d.Recovered("api"));                  // já estava normal
        Assert.True(d.ShouldLog("api", "sem resposta"));   // voltou a falhar
    }
}

public class WslExitCodesTests
{
    [Fact]
    public void Falha_de_inicializacao_no_desligamento_e_explicada()
    {
        var text = WslExitCodes.Describe(unchecked((int)0xC0000142));
        Assert.Contains("0xc0000142", text);
        Assert.Contains("encerrando", text);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "código 1")]
    [InlineData(-1, "código -1")]
    public void Outros_codigos(int code, string? expected) => Assert.Equal(expected, WslExitCodes.Describe(code));

    [Fact]
    public void Outros_erros_do_windows_aparecem_em_hexadecimal() =>
        Assert.Equal("código 0xc0000005", WslExitCodes.Describe(unchecked((int)0xC0000005)));
}

public class WslArgsTests
{
    [Fact]
    public void Script_nao_vai_inteiro_para_o_log() =>
        Assert.Equal("-d Ubuntu --exec sh -c (script de 42 caracteres)",
            WslExitCodes.DescribeArgs(["-d", "Ubuntu", "--exec", "sh", "-c", new string('x', 42)]));

    [Fact]
    public void Comandos_do_wsl_aparecem_como_sao() =>
        Assert.Equal("--terminate Ubuntu", WslExitCodes.DescribeArgs(["--terminate", "Ubuntu"]));
}
