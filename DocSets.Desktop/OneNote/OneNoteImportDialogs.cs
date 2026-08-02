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
    private readonly Label _message = new() { Dock = DockStyle.Top, Height = 32, Padding = new Padding(8, 6, 8, 0) };
    private readonly ProgressBar _progress = new() { Dock = DockStyle.Top, Height = 22 };
    private readonly Button _cancel = new() { Text = "Отмена", AutoSize = true };
    private readonly CancellationTokenSource _cancellation = new();
    private readonly OneNoteImportReportDialog _reportView;
    private OneNoteImportResult _result;
    private Exception _error;
    private bool _completed;

    private OneNoteProgressDialog()
    {
        Text = "Импорт из OneNote";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; ShowInTaskbar = false; ControlBox = false;
        Width = 1100; Height = 760;
        _reportView = new OneNoteImportReportDialog(new OneNoteImportReport(), "", "", null)
        {
            TopLevel = false, FormBorderStyle = FormBorderStyle.None, Dock = DockStyle.Fill
        };
        _reportView.SetActionsEnabled(false);
        _reportView.CloseRequested += (_, __) => Close();
        var status = new Panel { Dock = DockStyle.Bottom, Height = 90 };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 36, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(4) };
        buttons.Controls.Add(_cancel);
        status.Controls.Add(buttons); status.Controls.Add(_progress); status.Controls.Add(_message);
        Controls.Add(_reportView);
        Controls.Add(status);
        _reportView.Show();
        _cancel.Click += (_, __) =>
        {
            if (_completed) { Close(); return; }
            _cancel.Enabled = false;
            _message.Text = "Отмена импорта…";
            _cancellation.Cancel();
        };
        FormClosing += (_, args) => { if (!_completed) args.Cancel = true; };
    }

    public static OneNoteImportResult Run(IWin32Window owner,
        Func<IProgress<OneNoteImportProgress>, CancellationToken, Task<OneNoteImportResult>> import,
        Func<OneNoteImportResult, Task<OneNoteImportPresentation>> complete)
    {
        using var dialog = new OneNoteProgressDialog();
        dialog.Shown += async (_, __) => await dialog.StartAsync(import, complete);
        dialog.ShowDialog(owner);
        if (dialog._error != null) throw dialog._error;
        return dialog._result;
    }

    private async Task StartAsync(
        Func<IProgress<OneNoteImportProgress>, CancellationToken, Task<OneNoteImportResult>> import,
        Func<OneNoteImportResult, Task<OneNoteImportPresentation>> complete)
    {
        var progress = new Progress<OneNoteImportProgress>(value =>
        {
            _progress.Maximum = Math.Max(1, value.Total);
            _progress.Value = Math.Min(_progress.Maximum, Math.Max(0, value.Current));
            _message.Text = value.Total > 0 ? $"{value.Current} из {value.Total}: {value.Message}" : value.Message;
            if (value.ReportSnapshot != null) _reportView.UpdateReport(value.ReportSnapshot);
        });
        try
        {
            _result = await import(progress, _cancellation.Token);
            if (!_result.Cancelled && _result.Root != null && complete != null)
            {
                _message.Text = "Сохранение DocSet и обновление дерева…";
                var presentation = await complete(_result);
                _reportView.Complete(presentation);
                _progress.Value = _progress.Maximum;
                _message.Text = "Импорт завершён.";
            }
        }
        catch (OperationCanceledException) { _result = new OneNoteImportResult { Cancelled = true }; }
        catch (Exception exception) { _error = exception; }
        finally
        {
            _completed = true;
            ControlBox = true;
            _cancel.Enabled = true;
            _cancel.Text = "Закрыть";
            if (_error != null || _result?.Cancelled == true) Close();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancellation.Dispose();
        base.Dispose(disposing);
    }
}

internal sealed class OneNoteImportPresentation
{
    public OneNoteImportResult Result { get; set; }
    public string ReportPath { get; set; } = "";
    public string ProfilePath { get; set; } = "";
    public Func<OneNoteImportReportEntry, Task> OpenDocSets { get; set; }
}

internal sealed class OneNoteImportReportDialog : Form
{
    private OneNoteImportReport _report;
    private Func<OneNoteImportReportEntry, Task> _openDocSets;
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
    private readonly DataGridView _timingGrid = CreateReadOnlyGrid();
    private readonly DataGridView _slowPagesGrid = CreateReadOnlyGrid();
    private readonly Label _profileSummary = new()
    {
        Dock = DockStyle.Top, Height = 58, Padding = new Padding(8, 6, 8, 0)
    };
    private readonly Label _reportPath = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 5, 8, 0) };
    private readonly Label _profilePath = new() { Dock = DockStyle.Bottom, Height = 30, Padding = new Padding(8, 5, 8, 0) };
    private readonly Button _close = new() { Text = "Закрыть", AutoSize = true };
    private readonly Button _details = new() { Text = "Показать подробности", AutoSize = true };
    private readonly Button _openDocSetsButton = new() { Text = "Открыть в DocSets", AutoSize = true };
    private readonly Button _openOneNoteButton = new() { Text = "Открыть в OneNote", AutoSize = true };
    internal event EventHandler CloseRequested;

    internal OneNoteImportReportDialog(OneNoteImportReport report, string reportPath,
        string profilePath, Func<OneNoteImportReportEntry, Task> openDocSets)
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
        _timingGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Стадия", DataPropertyName = "Stage", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
        _timingGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Вызовы", DataPropertyName = "Calls", Width = 75 });
        _timingGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Всего, мс", DataPropertyName = "Total", Width = 105 });
        _timingGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Среднее, мс", DataPropertyName = "Average", Width = 110 });
        _timingGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Доля", DataPropertyName = "Share", Width = 75 });
        _slowPagesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Страница", DataPropertyName = "Page", Width = 210 });
        _slowPagesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Page ID", DataPropertyName = "PageId", Width = 235 });
        _slowPagesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "XML", DataPropertyName = "XmlSize", Width = 85 });
        _slowPagesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Изобр.", DataPropertyName = "Images", Width = 65 });
        _slowPagesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Влож.", DataPropertyName = "Attachments", Width = 65 });
        _slowPagesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Всего, мс", DataPropertyName = "Total", Width = 90 });
        _slowPagesGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Разбивка по стадиям", DataPropertyName = "Breakdown", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });

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
        _details.Click += (_, __) => ShowDetails(CurrentEntry);
        _grid.CellDoubleClick += (_, __) => ShowDetails(CurrentEntry);
        _openOneNoteButton.Click += (_, __) => OpenOneNote(CurrentEntry);
        _openDocSetsButton.Click += async (_, __) =>
        {
            var entry = CurrentEntry;
            if (entry != null && _openDocSets != null) await _openDocSets(entry);
        };
        _close.Click += (_, __) =>
        {
            if (TopLevel) Close(); else CloseRequested?.Invoke(this, EventArgs.Empty);
        };
        bottom.Controls.Add(_close);
        bottom.Controls.Add(_details);
        bottom.Controls.Add(_openDocSetsButton);
        bottom.Controls.Add(_openOneNoteButton);
        _reportPath.Text = "JSON: " + (reportPath ?? "");
        var resultsPage = new TabPage("Результаты");
        resultsPage.Controls.Add(_grid);
        resultsPage.Controls.Add(top);
        resultsPage.Controls.Add(_reportPath);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, Orientation = Orientation.Horizontal,
            SplitterDistance = 245
        };
        split.Panel1.Controls.Add(_timingGrid);
        split.Panel2.Controls.Add(_slowPagesGrid);
        _profilePath.Text = "Профиль JSON: " + (profilePath ?? "");
        var profilePage = new TabPage("Производительность");
        profilePage.Controls.Add(split);
        profilePage.Controls.Add(_profileSummary);
        profilePage.Controls.Add(_profilePath);
        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(resultsPage);
        tabs.TabPages.Add(profilePage);
        Controls.Add(tabs);
        Controls.Add(bottom);
        AcceptButton = _close;
        RefreshRows();
        RefreshProfile();
    }

    internal void UpdateReport(OneNoteImportReport report)
    {
        _report = report ?? new OneNoteImportReport();
        RefreshRows();
        RefreshProfile();
    }

    internal void Complete(OneNoteImportPresentation presentation)
    {
        if (presentation?.Result?.Report != null) UpdateReport(presentation.Result.Report);
        _openDocSets = presentation?.OpenDocSets;
        _reportPath.Text = "JSON: " + (presentation?.ReportPath ?? "");
        _profilePath.Text = "Профиль JSON: " + (presentation?.ProfilePath ?? "");
        SetActionsEnabled(true);
    }

    internal void SetActionsEnabled(bool enabled)
    {
        _close.Enabled = enabled;
        _details.Enabled = enabled;
        _openDocSetsButton.Enabled = enabled;
        _openOneNoteButton.Enabled = enabled;
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

    private void RefreshProfile()
    {
        var profile = _report.Profile ?? new OneNoteImportProfile();
        var total = profile.Timings.FirstOrDefault(item =>
            string.Equals(item.Path, OneNoteImportService.ProfileRoot, StringComparison.Ordinal))
            ?.ElapsedMilliseconds ?? 0;
        _timingGrid.DataSource = profile.Timings
            .OrderBy(item => ProfileOrder(item.Path))
            .Select(item => new TimingRow
            {
                Stage = new string(' ', Math.Max(0, item.Path.Count(character => character == '/')) * 4) +
                    item.Path.Split('/').Last(),
                Calls = item.Calls,
                Total = item.ElapsedMilliseconds.ToString("N1"),
                Average = item.AverageMilliseconds.ToString("N1"),
                Share = total <= 0 ? "—" : (item.ElapsedMilliseconds / total).ToString("P1")
            }).ToList();

        var pageTimes = profile.Pages.Select(page => page.TotalMilliseconds).OrderBy(value => value).ToList();
        var average = pageTimes.Count == 0 ? 0 : pageTimes.Average();
        var median = pageTimes.Count == 0 ? 0 : pageTimes.Count % 2 == 1
            ? pageTimes[pageTimes.Count / 2]
            : (pageTimes[pageTimes.Count / 2 - 1] + pageTimes[pageTimes.Count / 2]) / 2;
        _profileSummary.Text =
            $"Общее время: {total:N1} мс   Страниц: {pageTimes.Count}   " +
            $"Среднее: {average:N1} мс   Медиана: {median:N1} мс\r\n" +
            $"Сохранений DocSet: {profile.DocSetSaveCalls} " +
            $"(после каждой страницы: {(profile.DocSetSavedAfterEachPage ? "ДА" : "нет")})   " +
            $"Полных обновлений дерева/UI: {profile.TreeUpdateCalls} " +
            $"(после каждой страницы: {(profile.TreeUpdatedAfterEachPage ? "ДА" : "нет")})";
        _slowPagesGrid.DataSource = profile.Pages.OrderByDescending(page => page.TotalMilliseconds)
            .Take(20).Select(page => new SlowPageRow
            {
                Page = page.PageName,
                PageId = page.OneNotePageId,
                XmlSize = FormatBytes(page.XmlBytes),
                Images = page.Images,
                Attachments = page.Attachments,
                Total = page.TotalMilliseconds.ToString("N1"),
                Breakdown = string.Join("; ", page.Stages.OrderByDescending(pair => pair.Value)
                    .Select(pair => pair.Key + ": " + pair.Value.ToString("N1") + " мс"))
            }).ToList();
    }

    private static DataGridView CreateReadOnlyGrid() => new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
        AllowUserToDeleteRows = false, AutoGenerateColumns = false,
        SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
    };

    private static int ProfileOrder(string path)
    {
        if (string.Equals(path, OneNoteImportService.ProfileRoot, StringComparison.Ordinal)) return 0;
        var stage = (path ?? "").Split('/').LastOrDefault() ?? "";
        var order = new[] { "Весь импорт", "Загрузка иерархии", "Импорт страниц",
            "GetPageContent", "Разбор XML", "Конвертация", "внутренних ссылок",
            "Импорт тегов", "изображений", "вложений", "Создание узлов",
            "Сохранение assets", "Сохранение DocSet", "Обновление дерева" };
        for (var index = 0; index < order.Length; index++)
            if (stage.IndexOf(order[index], StringComparison.OrdinalIgnoreCase) >= 0)
                return index;
        return order.Length;
    }

    private static string FormatBytes(long bytes)
        => bytes >= 1024 * 1024 ? (bytes / 1024d / 1024d).ToString("N1") + " MB" :
            bytes >= 1024 ? (bytes / 1024d).ToString("N1") + " KB" : bytes + " B";

    private sealed class TimingRow
    {
        public string Stage { get; set; }
        public long Calls { get; set; }
        public string Total { get; set; }
        public string Average { get; set; }
        public string Share { get; set; }
    }

    private sealed class SlowPageRow
    {
        public string Page { get; set; }
        public string PageId { get; set; }
        public string XmlSize { get; set; }
        public int Images { get; set; }
        public int Attachments { get; set; }
        public string Total { get; set; }
        public string Breakdown { get; set; }
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
        string profilePath, Func<OneNoteImportReportEntry, Task> openDocSets)
    {
        using var dialog = new OneNoteImportReportDialog(report, reportPath, profilePath, openDocSets);
        dialog.ShowDialog(owner);
    }
}
