namespace WslSyncMonitor;

/// <summary>
/// Grava um arquivo de uma vez: escreve ao lado e troca pelo nome final. Quem lê vê o conteúdo antigo ou o novo,
/// nunca um arquivo vazio ou pela metade, e um fechamento no meio da gravação não estraga o arquivo.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temp, text);
            // no Windows, a troca falha se alguém está com o arquivo aberto naquele instante (editor, antivírus,
            // a releitura do próprio app): tenta de novo um pouco depois
            for (int attempt = 1; ; attempt++)
            {
                try { File.Move(temp, path, overwrite: true); return; }
                catch (Exception ex) when (attempt < 10 && ex is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
            }
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
