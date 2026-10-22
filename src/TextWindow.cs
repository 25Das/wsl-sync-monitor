namespace WslSyncMonitor;

/// <summary>Janela de texto só de leitura com Atualizar e Copiar (ficha técnica, erros, conflitos, listas).</summary>
public sealed class TextWindow : Form
{
    readonly TextBox _text;
    readonly Func<Task<string>> _build;

    public TextWindow(string title, Func<Task<string>> build)
    {
        _build = build;
        Text = $"WSL Sync Monitor - {title}";
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(720, 520);
        MinimumSize = new Size(480, 320);
        ShowInTaskbar = true;
        Font = new Font("Segoe UI", 9f);

        _text = new TextBox
        {
            Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
            Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f),
        };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(6) };
        var close = new Button { Text = "Fechar", AutoSize = true };
        var copy = new Button { Text = "Copiar", AutoSize = true };
        var refresh = new Button { Text = "Atualizar", AutoSize = true };
        close.Click += (_, _) => Close();
        copy.Click += (_, _) => { try { Clipboard.SetText(_text.Text); } catch { } };
        refresh.Click += async (_, _) => await LoadAsync();
        buttons.Controls.AddRange(new Control[] { close, copy, refresh });
        Controls.Add(_text);
        Controls.Add(buttons);
        CancelButton = close;
    }

    public async Task LoadAsync()
    {
        _text.Text = "Carregando…";
        try { _text.Text = (await _build()).Replace("\n", Environment.NewLine); }
        catch (Exception ex) { _text.Text = ex.ToString(); }
    }
}
