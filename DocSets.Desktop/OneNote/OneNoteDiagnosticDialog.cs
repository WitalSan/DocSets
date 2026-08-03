namespace DocSets.Desktop.OneNote;

internal sealed class OneNoteDiagnosticDialog : Form
{
    private readonly Label _message = new() { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 22 };
    private readonly Button _cancel = new() { Text = "Отмена", AutoSize = true };
    private readonly CancellationTokenSource _cancellation = new();
    private OneNoteObjectIdDiagnosticReport _result;
    private Exception _error;
    private bool _completed;

    private OneNoteDiagnosticDialog()
    {
        Text = "OneNote Test-1 — исследование object-id";
        StartPosition = FormStartPosition.CenterParent;
        Width = 720; Height = 150;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 45, FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(7)
        };
        buttons.Controls.Add(_cancel);
        Controls.Add(buttons); Controls.Add(_progress); Controls.Add(_message);
        _cancel.Click += (_, __) =>
        {
            _cancel.Enabled = false;
            _message.Text = "Отмена исследования…";
            _cancellation.Cancel();
        };
        FormClosing += (_, args) =>
        {
            if (_completed) return;
            args.Cancel = true;
            _cancellation.Cancel();
        };
    }

    public static OneNoteObjectIdDiagnosticReport Run(IWin32Window owner,
        Func<IProgress<OneNoteDiagnosticProgress>, CancellationToken,
            Task<OneNoteObjectIdDiagnosticReport>> operation)
    {
        using var dialog = new OneNoteDiagnosticDialog();
        dialog.Shown += async (_, __) => await dialog.StartAsync(operation);
        dialog.ShowDialog(owner);
        if (dialog._error != null) throw dialog._error;
        return dialog._result;
    }

    private async Task StartAsync(Func<IProgress<OneNoteDiagnosticProgress>, CancellationToken,
        Task<OneNoteObjectIdDiagnosticReport>> operation)
    {
        var progress = new Progress<OneNoteDiagnosticProgress>(value =>
        {
            _progress.Maximum = Math.Max(1, value.Total);
            _progress.Value = Math.Min(_progress.Maximum, Math.Max(0, value.Current));
            _message.Text = value.Total > 0
                ? $"{value.Current} из {value.Total}: {value.Message}" : value.Message;
        });
        try
        {
            _result = await operation(progress, _cancellation.Token);
            _progress.Value = _progress.Maximum;
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _error = exception; }
        finally
        {
            _completed = true;
            Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancellation.Dispose();
        base.Dispose(disposing);
    }
}
