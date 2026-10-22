namespace WslSyncMonitor.WindowsTests.E2E;

/// <summary>
/// Teste de ponta a ponta: abre o app de verdade e usa o mouse. Só roda com WSM_E2E=1 (o CI define), para não
/// atrapalhar quem roda os testes no próprio PC com o app aberto.
/// </summary>
public sealed class E2EFactAttribute : FactAttribute
{
    public E2EFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("WSM_E2E") != "1") Skip = "teste de ponta a ponta: defina WSM_E2E=1";
    }
}
