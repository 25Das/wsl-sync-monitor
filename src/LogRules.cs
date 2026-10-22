using System.Globalization;
using System.Text.RegularExpressions;

namespace WslSyncMonitor;

public enum LogLevel { Info, Warn, Error }

/// <summary>Regras do registro (log): formato da linha, segredos ocultos, um arquivo por dia e limite de tamanho.</summary>
public static partial class LogRules
{
    const string Indent = "    ";

    static string Label(LogLevel level) => level switch
    {
        LogLevel.Warn => "AVISO",
        LogLevel.Error => "ERRO ",
        _ => "INFO ",
    };

    /// <summary>"2026-10-22 14:05:09.123 AVISO [área] mensagem"; linhas seguintes e a exceção recuadas.</summary>
    public static string Format(DateTime at, LogLevel level, string area, string message, Exception? ex = null)
    {
        var lines = message.Replace("\r\n", "\n").Split('\n').ToList();
        if (ex != null) lines.AddRange(ex.ToString().Replace("\r\n", "\n").Split('\n'));
        var head = $"{at.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)} {Label(level)} [{area}] {lines[0]}";
        return string.Join("\n", lines.Skip(1).Select(l => Indent + l).Prepend(head));
    }

    /// <summary>Troca os segredos (como a chave da API) por "•••". Valores curtos são ignorados (falsos positivos).</summary>
    public static string Redact(string text, IEnumerable<string> secrets)
    {
        foreach (var s in secrets)
            if (s.Length >= 8) text = text.Replace(s, "•••", StringComparison.Ordinal);
        return text;
    }

    public static string FileName(DateTime day) => $"app-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log";

    [GeneratedRegex(@"^app-(\d{8})\.log$")]
    private static partial Regex LogFile();

    /// <summary>Arquivos de log mais antigos que <paramref name="keepDays"/> dias (só os do app).</summary>
    public static IReadOnlyList<string> ToDelete(IEnumerable<string> fileNames, DateTime today, int keepDays) =>
        fileNames.Where(name => LogFile().Match(name) is { Success: true } m &&
                                DateTime.TryParseExact(m.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out var day) &&
                                day < today.Date.AddDays(-keepDays + 1))
                 .ToList();

    /// <summary>Com o arquivo do dia cheio, informações param; avisos e erros continuam até o dobro do limite.</summary>
    public static bool ShouldWrite(LogLevel level, long currentSize, long maxSize) =>
        currentSize < (level == LogLevel.Info ? maxSize : 2 * maxSize);
}

/// <summary>
/// Evita encher o log com a mesma falha repetida num loop (ex.: a API sem resposta a cada atualização):
/// registra quando a falha aparece ou muda, e uma vez quando volta ao normal.
/// </summary>
public sealed class LogDedup
{
    readonly Dictionary<string, string> _failing = new();

    public bool ShouldLog(string key, string message)
    {
        lock (_failing)
        {
            if (_failing.TryGetValue(key, out var last) && last == message) return false;
            _failing[key] = message;
            return true;
        }
    }

    /// <summary>true se estava falhando (registre "voltou ao normal" uma vez).</summary>
    public bool Recovered(string key)
    {
        lock (_failing) return _failing.Remove(key);
    }
}

/// <summary>Explica o código de saída do wsl.exe; os de erro do Windows (NTSTATUS) em hexadecimal.</summary>
public static class WslExitCodes
{
    const uint DllInitFailed = 0xC0000142;

    public static string? Describe(int exitCode)
    {
        if (exitCode == 0) return null;
        uint u = unchecked((uint)exitCode);
        if (u == DllInitFailed)
            return "código 0xc0000142: o wsl.exe não pôde iniciar (o Windows estava encerrando a sessão)";
        return (u & 0xF0000000) == 0xC0000000 ? $"código 0x{u:x8}" : $"código {exitCode}";
    }

    /// <summary>Argumentos do wsl.exe para o log: o script de "sh -c" só pelo tamanho (pode ser longo).</summary>
    public static string DescribeArgs(IReadOnlyList<string> args)
    {
        var parts = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == "-c" && i > 0 && args[i - 1] == "sh" && i + 1 < args.Count)
            {
                parts.Add($"-c (script de {args[i + 1].Length} caracteres)");
                i++;
            }
            else parts.Add(args[i]);
        }
        return string.Join(" ", parts);
    }
}
