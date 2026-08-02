namespace DocSets.Desktop.OneNote;

using System.Diagnostics;

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

internal sealed class OneNoteImportReportDialog : Form
{
    private readonly OneNoteImportReport _report;
    private readonly Func<OneNoteImportReportEntry, Task> _openDocSets;
    private readonly DataGridView _grid = new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        AllowUserToAddRows = false,
        AllowUserToDeleteRows = false,
        AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        MultiSelect = false
    };
    private readonly ComboBox _filter = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 190 };
    private readonly Label _summary = new() { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };

    private OneNoteImportReportDialog(OneNoteImportReport report, string reportPath,
        Func<OneNoteImportReportEntry, Task> openDocSets)
    {
        _report = report ?? new OneNoteImportReport();
        _openDocSets = openDocSets;
        Text = "Отчёт импорта OneNote";
        StartPosition = FormStartPosition.CenterParent;
        Width = 1050;
        Height = 650;
        MinimizeBox = false;

        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Статус", DataPropertyName = "Status", Width = 155 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Тип", DataPropertyName = "ObjectType", Width = 125 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Объект", DataPropertyName = "Name", Width = 220 });
        _grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Причина", DataPropertyName = "Reason", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

        _filter.Items.AddRange(new object[] { "Проблемы (первичные)",
            "Все предупреждения (с родителями)", "Не импортировано",
            "Преобразовано с потерями", "Затронутые страницы и блоки",
            "Импортировано", "Все объекты" });
        _filter.SelectedIndex = 0;
        _filter.SelectedIndexChanged += (_, __) => RefreshRows();

        var top = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(5) };
        top.Controls.Add(_filter);
        top.Controls.Add(_summary);
        var bottom = new FlowLayoutPanel
        {
            Dock = DockStyle.Bottom, Height = 50, Padding = new Padding(7),
            FlowDirection = FlowDirection.RightToLeft
        };
        var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.OK };
        var details = new Button { Text = "Показать подробности", AutoSize = true };
        var openDocSetsButton = new Button { Text = "Открыть в DocSets", AutoSize = true };
        var openOneNoteButton = new Button { Text = "Открыть в OneNote", AutoSize = true };
        details.Click += (_, __) => ShowDetails(CurrentEntry);
        _grid.CellDoubleClick += (_, __) => ShowDetails(CurrentEntry);
        openOneNoteButton.Click += (_, __) => OpenOneNote(CurrentEntry);
        openDocSetsButton.Click += async (_, __) =>
        {
            var entry = CurrentEntry;
            if (entry != null && _openDocSets != null) await _openDocSets(entry);
        };
        bottom.Controls.Add(close);
        bottom.Controls.Add(details);
        bottom.Controls.Add(openDocSetsButton);
        bottom.Controls.Add(openOneNoteButton);
        var path = new Label
        {
            Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 5, 8, 0),
            Text = "JSON: " + (reportPath ?? "")
        };
        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(path);
        Controls.Add(bottom);
        AcceptButton = close;
        RefreshRows();
    }

    private OneNoteImportReportEntry CurrentEntry
        => _grid.CurrentRow?.DataBoundItem as OneNoteImportReportEntry;

    private void RefreshRows()
    {
        IEnumerable<OneNoteImportReportEntry> entries = _report.Entries;
        entries = _filter.SelectedIndex switch
        {
            0 => entries.Where(entry => entry.Status != OneNoteImportStatus.Imported && !entry.IsAggregate),
            1 => entries.Where(entry => entry.Status != OneNoteImportStatus.Imported),
            2 => entries.Where(entry => entry.Status == OneNoteImportStatus.NotImported && !entry.IsAggregate),
            3 => entries.Where(entry => entry.Status == OneNoteImportStatus.ConvertedWithLoss && !entry.IsAggregate),
            4 => entries.Where(entry => entry.IsAggregate),
            5 => entries.Where(entry => entry.Status == OneNoteImportStatus.Imported),
            _ => entries
        };
        _grid.DataSource = entries.ToList();
        _summary.Text = $"Первичных проблем: {_report.PrimaryProblems}   " +
            $"Потери: {_report.Entries.Count(entry => entry.Status == OneNoteImportStatus.ConvertedWithLoss && !entry.IsAggregate)}   " +
            $"Не импортировано: {_report.Entries.Count(entry => entry.Status == OneNoteImportStatus.NotImported && !entry.IsAggregate)}   " +
            $"Затронуто страниц/блоков: {_report.Entries.Count(entry => entry.IsAggregate)}   " +
            $"Всего объектов: {_report.Entries.Count}";
    }

    private void OpenOneNote(OneNoteImportReportEntry entry)
    {
        if (entry == null || string.IsNullOrWhiteSpace(entry.OneNoteLink))
        {
            MessageBox.Show(this, "Для объекта отсутствует ссылка OneNote.", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try { Process.Start(new ProcessStartInfo(entry.OneNoteLink) { UseShellExecute = true }); }
        catch (Exception exception)
        {
            MessageBox.Show(this, "Не удалось открыть объект в OneNote.\r\n\r\n" + exception.Message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ShowDetails(OneNoteImportReportEntry entry)
    {
        if (entry == null) return;
        MessageBox.Show(this,
            $"Статус: {entry.Status}\r\nТип: {entry.ObjectType}\r\nИмя: {entry.Name}\r\n" +
            $"Причина: {entry.Reason}\r\n\r\nOneNote Page ID: {entry.OneNotePageId}\r\n" +
            $"OneNote Object ID: {entry.OneNoteObjectId}\r\nOneNote link: {entry.OneNoteLink}\r\n\r\n" +
            $"DocSets Node ID: {entry.DocSetsNodeId}\r\nDocSets Anchor ID: {entry.DocSetsAnchorId}\r\n" +
            $"Агрегированное предупреждение: {(entry.IsAggregate ? "да" : "нет")}\r\n" +
            $"Связанные проблемы: {string.Join(", ", entry.RelatedProblemIds ?? new List<string>())}\r\n" +
            $"Report ID: {entry.Id}",
            "Подробности объекта", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static void Show(IWin32Window owner, OneNoteImportReport report, string reportPath,
        Func<OneNoteImportReportEntry, Task> openDocSets)
    {
        using var dialog = new OneNoteImportReportDialog(report, reportPath, openDocSets);
        dialog.ShowDialog(owner);
    }
}
