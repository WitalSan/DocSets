namespace DocSets.Desktop.OneNote;

internal sealed class OneNoteNotebookDialog : Form
{
    private readonly ListBox _notebooks = new() { Dock = DockStyle.Fill, DisplayMember = nameof(OneNoteNotebook.Name) };

    private OneNoteNotebookDialog(IReadOnlyList<OneNoteNotebook> notebooks)
    {
        Text = "Импорт из OneNote — выбор записной книжки";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        Width = 560; Height = 380;
        foreach (var notebook in notebooks) _notebooks.Items.Add(notebook);
        if (_notebooks.Items.Count > 0) _notebooks.SelectedIndex = 0;
        var label = new Label { Text = "Выберите записную книжку OneNote:", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var import = new Button { Text = "Импортировать", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(import); buttons.Controls.Add(cancel);
        Controls.Add(_notebooks); Controls.Add(label); Controls.Add(buttons);
        AcceptButton = import; CancelButton = cancel;
        import.Enabled = _notebooks.Items.Count > 0;
        _notebooks.SelectedIndexChanged += (_, __) => import.Enabled = _notebooks.SelectedItem != null;
        _notebooks.DoubleClick += (_, __) => { if (_notebooks.SelectedItem != null) DialogResult = DialogResult.OK; };
    }

    public static OneNoteNotebook Select(IWin32Window owner, IReadOnlyList<OneNoteNotebook> notebooks)
    {
        using var dialog = new OneNoteNotebookDialog(notebooks);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._notebooks.SelectedItem as OneNoteNotebook : null;
    }
}

internal sealed class OneNoteProgressDialog : Form
{
    private readonly Label _message = new() { Dock = DockStyle.Top, Height = 42, Padding = new Padding(8) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 24 };
    private readonly Button _cancel = new() { Text = "Отмена", AutoSize = true };
    private readonly CancellationTokenSource _cancellation = new();
    private OneNoteImportResult _result;
    private Exception _error;
    private bool _completed;

    private OneNoteProgressDialog()
    {
        Text = "Импорт из OneNote";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false; ControlBox = false;
        Width = 600; Height = 160;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        buttons.Controls.Add(_cancel);
        Controls.Add(buttons); Controls.Add(_progress); Controls.Add(_message);
        _cancel.Click += (_, __) => { _cancel.Enabled = false; _message.Text = "Отмена импорта…"; _cancellation.Cancel(); };
        FormClosing += (_, args) => { if (!_completed) args.Cancel = true; };
    }

    public static OneNoteImportResult Run(IWin32Window owner, Func<IProgress<OneNoteImportProgress>, CancellationToken, Task<OneNoteImportResult>> import)
    {
        using var dialog = new OneNoteProgressDialog();
        dialog.Shown += async (_, __) => await dialog.StartAsync(import);
        dialog.ShowDialog(owner);
        if (dialog._error != null) throw dialog._error;
        return dialog._result;
    }

    private async Task StartAsync(Func<IProgress<OneNoteImportProgress>, CancellationToken, Task<OneNoteImportResult>> import)
    {
        var progress = new Progress<OneNoteImportProgress>(value =>
        {
            _progress.Maximum = Math.Max(1, value.Total);
            _progress.Value = Math.Min(_progress.Maximum, Math.Max(0, value.Current));
            _message.Text = value.Total > 0 ? $"{value.Current} из {value.Total}: {value.Message}" : value.Message;
        });
        try { _result = await import(progress, _cancellation.Token); }
        catch (OperationCanceledException) { _result = new OneNoteImportResult { Cancelled = true }; }
        catch (Exception exception) { _error = exception; }
        finally { _completed = true; Close(); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancellation.Dispose();
        base.Dispose(disposing);
    }
}
