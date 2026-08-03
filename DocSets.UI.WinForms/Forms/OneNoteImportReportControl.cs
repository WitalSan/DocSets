using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace DocSets
{
    public enum ImportSessionCommand { Open, Resume, Pause, Delete }

    public sealed class ImportSessionCommandEventArgs : EventArgs
    {
        public ImportSessionCommandEventArgs(ImportSessionCommand command, string sessionId)
        { Command = command; SessionId = sessionId ?? ""; }
        public ImportSessionCommand Command { get; }
        public string SessionId { get; }
    }

    /// <summary>
    /// Existing OneNote import report UI, extended for a persistent import session.
    /// Desktop hosts it in a DockContent document and owns no report implementation.
    /// </summary>
    public sealed class OneNoteImportReportDialog : Form
    {
        private ImportSessionState session;
        private OneNoteImportReport report = new OneNoteImportReport();
        private Func<OneNoteImportReportEntry, Task> openDocSets;
        private readonly ToolStripButton resume = new ToolStripButton("Продолжить");
        private readonly ToolStripButton pause = new ToolStripButton("Остановить");
        private readonly ToolStripButton delete = new ToolStripButton("Удалить");
        private readonly ToolStripLabel status = new ToolStripLabel { Alignment = ToolStripItemAlignment.Right };
        private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Top, Height = 18 };
        private readonly Label general = new Label { Dock = DockStyle.Top, Height = 66, Padding = new Padding(8, 5, 8, 0) };
        private readonly ComboBox filter = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 215 };
        private readonly Label summary = new Label { AutoSize = true, Padding = new Padding(8, 7, 8, 0) };
        private readonly BindingList<OneNoteImportReportEntry> resultRows = new BindingList<OneNoteImportReportEntry>();
        private readonly BindingList<TimingRow> timingRows = new BindingList<TimingRow>();
        private readonly BindingList<PageRow> pageRows = new BindingList<PageRow>();
        private readonly DataGridView results = Grid();
        private readonly DataGridView timings = Grid();
        private readonly DataGridView pages = Grid();
        private readonly DataGridView statistics = StatisticsGrid();
        private readonly ListBox warnings = new ListBox { Dock = DockStyle.Fill };
        private readonly ListBox errors = new ListBox { Dock = DockStyle.Fill };
        private readonly Label profileSummary = new Label { Dock = DockStyle.Top, Height = 52, Padding = new Padding(8, 6, 8, 0) };
        private readonly Button details = new Button { Text = "Показать подробности", AutoSize = true };
        private readonly Button openDocSetsButton = new Button { Text = "Открыть в DocSets", AutoSize = true };
        private readonly Button openOneNoteButton = new Button { Text = "Открыть в OneNote", AutoSize = true };
        private readonly Timer refreshTimer = new Timer { Interval = 500 };
        private string lastReportJson;
        private string lastProfileJson;
        private string lastStatistics;
        private string lastWarnings;
        private string lastErrors;

        public event EventHandler<ImportSessionCommandEventArgs> CommandRequested;

        public OneNoteImportReportDialog()
        {
            Dock = DockStyle.Fill;
            var toolbar = new ToolStrip { Dock = DockStyle.Top, GripStyle = ToolStripGripStyle.Hidden };
            resume.Click += (_, __) => Request(ImportSessionCommand.Resume);
            pause.Click += (_, __) => Request(ImportSessionCommand.Pause);
            delete.Click += (_, __) => RequestDelete();
            toolbar.Items.Add(resume);
            toolbar.Items.Add(pause);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(delete);
            toolbar.Items.Add(status);

            results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Статус", DataPropertyName = "Status", Width = 155 });
            results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Тип", DataPropertyName = "ObjectType", Width = 125 });
            results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Объект", DataPropertyName = "Name", Width = 220 });
            results.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Причина", DataPropertyName = "Reason", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            timings.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Стадия", DataPropertyName = "Stage", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            timings.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Вызовы", DataPropertyName = "Calls", Width = 75 });
            timings.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Всего, мс", DataPropertyName = "Total", Width = 105 });
            timings.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Среднее, мс", DataPropertyName = "Average", Width = 110 });
            timings.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Доля", DataPropertyName = "Share", Width = 75 });
            pages.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Страница", DataPropertyName = "Page", Width = 210 });
            pages.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Page ID", DataPropertyName = "PageId", Width = 235 });
            pages.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "XML", DataPropertyName = "XmlSize", Width = 85 });
            pages.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Изобр.", DataPropertyName = "Images", Width = 65 });
            pages.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Влож.", DataPropertyName = "Attachments", Width = 65 });
            pages.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Всего, мс", DataPropertyName = "Total", Width = 90 });
            pages.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Разбивка по стадиям", DataPropertyName = "Breakdown", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill });
            results.Columns.Add(ResultActionColumn("OpenOneNote", "OneNote", 82));
            results.Columns.Add(ResultActionColumn("OpenDocSets", "DocSets", 82));
            results.Columns.Add(ResultActionColumn("ShowDetails", "Подробнее", 96));
            results.DataSource = resultRows;
            timings.DataSource = timingRows;
            pages.DataSource = pageRows;

            filter.Items.AddRange(new object[] { "Проблемы (первичные)",
                "Все предупреждения (с родителями)", "Не импортировано",
                "Преобразовано с потерями", "Затронутые страницы и блоки",
                "Импортировано", "Все объекты" });
            filter.SelectedIndex = 0;
            filter.SelectedIndexChanged += (_, __) => RefreshResults();
            results.CellDoubleClick += (_, __) => ShowDetails(CurrentEntry);
            results.CellContentClick += ResultsCellContentClick;
            results.CellFormatting += ResultsCellFormatting;
            results.SelectionChanged += (_, __) => UpdateActionButtons();
            details.Click += (_, __) => ShowDetails(CurrentEntry);
            openOneNoteButton.Click += (_, __) => OpenOneNote(CurrentEntry);
            openDocSetsButton.Click += async (_, __) =>
            {
                var entry = CurrentEntry;
                if (entry != null && openDocSets != null) await openDocSets(entry);
            };

            var resultTop = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 43, Padding = new Padding(5) };
            resultTop.Controls.Add(filter);
            resultTop.Controls.Add(summary);
            var resultPage = new TabPage("Результаты");
            resultPage.Controls.Add(results);
            resultPage.Controls.Add(resultTop);

            var statisticsPage = new TabPage("Статистика");
            statisticsPage.Controls.Add(statistics);

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 245 };
            split.Panel1.Controls.Add(timings);
            split.Panel2.Controls.Add(pages);
            var profilePage = new TabPage("Производительность");
            profilePage.Controls.Add(split);
            profilePage.Controls.Add(profileSummary);

            var messages = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal, SplitterDistance = 220 };
            messages.Panel1.Controls.Add(warnings);
            messages.Panel1.Controls.Add(new Label { Text = "Предупреждения", Dock = DockStyle.Top, Height = 25, Padding = new Padding(5) });
            messages.Panel2.Controls.Add(errors);
            messages.Panel2.Controls.Add(new Label { Text = "Ошибки", Dock = DockStyle.Top, Height = 25, Padding = new Padding(5) });
            var messagesPage = new TabPage("Предупреждения и ошибки");
            messagesPage.Controls.Add(messages);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(resultPage);
            tabs.TabPages.Add(statisticsPage);
            tabs.TabPages.Add(profilePage);
            tabs.TabPages.Add(messagesPage);

            var actions = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(7),
                FlowDirection = FlowDirection.RightToLeft
            };
            actions.Controls.Add(details);
            actions.Controls.Add(openDocSetsButton);
            actions.Controls.Add(openOneNoteButton);

            toolbar.Dock = DockStyle.Fill;
            progress.Dock = DockStyle.Fill;
            general.Dock = DockStyle.Fill;
            tabs.Dock = DockStyle.Fill;
            actions.Dock = DockStyle.Fill;
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Margin = Padding.Empty,
                Padding = Padding.Empty
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 18));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
            layout.Controls.Add(toolbar, 0, 0);
            layout.Controls.Add(progress, 0, 1);
            layout.Controls.Add(general, 0, 2);
            layout.Controls.Add(tabs, 0, 3);
            layout.Controls.Add(actions, 0, 4);
            Controls.Add(layout);
            refreshTimer.Tick += (_, __) => RefreshFromSession();
            refreshTimer.Start();
            Disposed += (_, __) => refreshTimer.Dispose();
            UpdateActionButtons();
        }

        public void Attach(ImportSessionState value, Func<OneNoteImportReportEntry, Task> openEntry)
        {
            session = value ?? throw new ArgumentNullException(nameof(value));
            openDocSets = openEntry;
            RefreshFromSession();
        }

        private OneNoteImportReportEntry CurrentEntry
            => results.CurrentRow?.DataBoundItem as OneNoteImportReportEntry;

        private void RefreshFromSession()
        {
            if (session == null) return;
            var maximum = 100;
            var value = Math.Min(maximum, Math.Max(0, session.OverallProgressPercent));
            if (progress.Maximum != maximum) progress.Maximum = maximum;
            if (progress.Value != value) progress.Value = value;
            resume.Enabled = session.Status == ImportSessionStatus.Paused ||
                session.Status == ImportSessionStatus.Interrupted || session.Status == ImportSessionStatus.Failed ||
                session.Status == ImportSessionStatus.Completed && !session.LinkResolutionCompleted;
            pause.Enabled = session.Status == ImportSessionStatus.Running;
            var statusText = session.Status + " | " + session.Stage + " | " +
                session.OverallProgressPercent + "% | страницы " +
                session.ProgressCurrent + "/" + session.ProgressTotal;
            if (!string.Equals(status.Text, statusText, StringComparison.Ordinal)) status.Text = statusText;

            var reportJson = session.ReportJson ?? "";
            var profileJson = session.ProfileJson ?? "";
            var reportChanged = !string.Equals(lastReportJson, reportJson, StringComparison.Ordinal);
            var profileChanged = !string.Equals(lastProfileJson, profileJson, StringComparison.Ordinal);
            if (reportChanged)
            {
                try { report = JsonConvert.DeserializeObject<OneNoteImportReport>(reportJson) ?? new OneNoteImportReport(); }
                catch { report = new OneNoteImportReport(); }
                lastReportJson = reportJson;
            }
            if (profileChanged)
            {
                try { report.Profile = JsonConvert.DeserializeObject<OneNoteImportProfile>(profileJson) ?? report.Profile; }
                catch { }
                lastProfileJson = profileJson;
            }

            var isRunning = session.Status == ImportSessionStatus.Running ||
                session.Status == ImportSessionStatus.Pausing;
            var profileMilliseconds = report.Profile?.Timings?.FirstOrDefault(x =>
                x.Path == OneNoteImportProfile.RootPath)?.ElapsedMilliseconds ?? 0;
            var finish = session.CompletedAtUtc ?? (isRunning ? DateTimeOffset.UtcNow : session.StartedAtUtc);
            var duration = session.StartedAtUtc.HasValue && finish.HasValue
                ? finish.Value - session.StartedAtUtc.Value
                : TimeSpan.FromMilliseconds(profileMilliseconds);
            if (!isRunning && !session.CompletedAtUtc.HasValue && profileMilliseconds > 0)
                duration = TimeSpan.FromMilliseconds(profileMilliseconds);
            var generalText = "Источник: " + session.SourceType + " — " + session.SourceName +
                "   Создано: " + LocalTime(session.CreatedAtUtc) +
                "   Начато: " + LocalTime(session.StartedAtUtc) + "\r\n" +
                "Окончено: " + LocalTime(session.CompletedAtUtc) +
                "   Длительность: " + FormatDuration(duration) +
                "   Этап: " + session.Stage + "   Статус: " + session.Status;
            if (!string.Equals(general.Text, generalText, StringComparison.Ordinal)) general.Text = generalText;

            if (reportChanged) RefreshResults();
            if (reportChanged || profileChanged) RefreshProfile();
            var statisticsSignature = StatisticsSignature(session.Statistics);
            if (!string.Equals(lastStatistics, statisticsSignature, StringComparison.Ordinal))
            {
                RefreshStatistics();
                lastStatistics = statisticsSignature;
            }
            var warningsSignature = ListSignature(session.Warnings);
            if (!string.Equals(lastWarnings, warningsSignature, StringComparison.Ordinal))
            {
                SyncList(warnings, session.Warnings);
                lastWarnings = warningsSignature;
            }
            var errorsSignature = ListSignature(session.Errors);
            if (!string.Equals(lastErrors, errorsSignature, StringComparison.Ordinal))
            {
                SyncList(errors, session.Errors);
                lastErrors = errorsSignature;
            }
        }

        private void RefreshResults()
        {
            IEnumerable<OneNoteImportReportEntry> source = report.Entries ?? new List<OneNoteImportReportEntry>();
            source = filter.SelectedIndex switch
            {
                0 => source.Where(x => x.Status != OneNoteImportStatus.Imported && !x.IsAggregate),
                1 => source.Where(x => x.Status != OneNoteImportStatus.Imported),
                2 => source.Where(x => x.Status == OneNoteImportStatus.NotImported && !x.IsAggregate),
                3 => source.Where(x => x.Status == OneNoteImportStatus.ConvertedWithLoss && !x.IsAggregate),
                4 => source.Where(x => x.IsAggregate),
                5 => source.Where(x => x.Status == OneNoteImportStatus.Imported),
                _ => source
            };
            SyncRows(resultRows, source.ToList(), x => x.Id, null, results);
            var entries = report.Entries ?? new List<OneNoteImportReportEntry>();
            summary.Text = "Первичных проблем: " + report.PrimaryProblems +
                "   Потери: " + entries.Count(x => x.Status == OneNoteImportStatus.ConvertedWithLoss && !x.IsAggregate) +
                "   Не импортировано: " + entries.Count(x => x.Status == OneNoteImportStatus.NotImported && !x.IsAggregate) +
                "   Затронуто страниц/блоков: " + entries.Count(x => x.IsAggregate) +
                "   Всего объектов: " + entries.Count;
            UpdateActionButtons();
        }

        private void RefreshProfile()
        {
            var profile = report.Profile ?? new OneNoteImportProfile();
            var total = profile.Timings.FirstOrDefault(x => x.Path == OneNoteImportProfile.RootPath)?.ElapsedMilliseconds ?? 0;
            var desiredTimings = profile.Timings.OrderBy(x => ProfileOrder(x.Path)).Select(x => new TimingRow
            {
                Key = x.Path,
                Stage = new string(' ', Math.Max(0, (x.Path ?? "").Count(c => c == '/')) * 4) + (x.Path ?? "").Split('/').Last(),
                Calls = x.Calls,
                Total = x.ElapsedMilliseconds.ToString("N1"),
                Average = x.AverageMilliseconds.ToString("N1"),
                Share = total <= 0 ? "—" : (x.ElapsedMilliseconds / total).ToString("P1")
            }).ToList();
            SyncRows(timingRows, desiredTimings, x => x.Key, CopyTimingRow, timings);

            var pageTimes = profile.Pages.Select(x => x.TotalMilliseconds).OrderBy(x => x).ToList();
            var average = pageTimes.Count == 0 ? 0 : pageTimes.Average();
            var median = pageTimes.Count == 0 ? 0 : pageTimes.Count % 2 == 1
                ? pageTimes[pageTimes.Count / 2]
                : (pageTimes[pageTimes.Count / 2 - 1] + pageTimes[pageTimes.Count / 2]) / 2;
            profileSummary.Text = "Общее время: " + total.ToString("N1") + " мс   Страниц: " + pageTimes.Count +
                "   Среднее: " + average.ToString("N1") + " мс   Медиана: " + median.ToString("N1") + " мс\r\n" +
                "Сохранений DocSet: " + profile.DocSetSaveCalls +
                " (после каждой страницы: " + (profile.DocSetSavedAfterEachPage ? "ДА" : "нет") + ")   " +
                "Полных обновлений дерева/UI: " + profile.TreeUpdateCalls +
                " (после каждой страницы: " + (profile.TreeUpdatedAfterEachPage ? "ДА" : "нет") + ")";
            var desiredPages = profile.Pages.OrderByDescending(x => x.TotalMilliseconds).Take(20).Select(x => new PageRow
            {
                Key = x.OneNotePageId + "\n" + x.PageName,
                Page = x.PageName,
                PageId = x.OneNotePageId,
                XmlSize = FormatBytes(x.XmlBytes),
                Images = x.Images,
                Attachments = x.Attachments,
                Total = x.TotalMilliseconds.ToString("N1"),
                Breakdown = string.Join("; ", x.Stages.OrderByDescending(y => y.Value).Select(y => y.Key + ": " + y.Value.ToString("N1") + " мс"))
            }).ToList();
            SyncRows(pageRows, desiredPages, x => x.Key, CopyPageRow, pages);
        }

        private void RefreshStatistics()
        {
            var s = session.Statistics ?? new ImportSessionStatistics();
            var values = new[] { Pair("Разделы", s.Sections), Pair("Страницы", s.Pages),
                Pair("Изображения", s.Images), Pair("Вложения", s.Attachments), Pair("Таблицы", s.Tables),
                Pair("Теги", s.Tags), Pair("Внутренние ссылки", s.InternalLinks),
                Pair("Внешние ссылки", s.ExternalLinks), Pair("Объектные ссылки", s.ObjectLinks),
                Pair("Ошибки страниц", s.FailedPages) };
            for (var index = 0; index < values.Length; index++)
            {
                if (index >= statistics.Rows.Count) statistics.Rows.Add(values[index].Key, values[index].Value);
                else
                {
                    statistics.Rows[index].Cells[0].Value = values[index].Key;
                    statistics.Rows[index].Cells[1].Value = values[index].Value;
                }
            }
            while (statistics.Rows.Count > values.Length) statistics.Rows.RemoveAt(statistics.Rows.Count - 1);
        }

        private void UpdateActionButtons()
        {
            var entry = CurrentEntry;
            details.Enabled = entry != null;
            openDocSetsButton.Enabled = entry != null && openDocSets != null && !string.IsNullOrWhiteSpace(entry.DocSetsNodeId);
            openOneNoteButton.Enabled = entry != null && !string.IsNullOrWhiteSpace(entry.OneNoteLink);
        }

        private async void ResultsCellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 ||
                results.Rows[e.RowIndex].DataBoundItem is not OneNoteImportReportEntry entry)
                return;
            var columnName = results.Columns[e.ColumnIndex].Name;
            if (columnName == "OpenOneNote") OpenOneNote(entry);
            else if (columnName == "OpenDocSets")
            {
                if (openDocSets != null && !string.IsNullOrWhiteSpace(entry.DocSetsNodeId))
                    await openDocSets(entry);
            }
            else if (columnName == "ShowDetails") ShowDetails(entry);
        }

        private void ResultsCellFormatting(object sender, DataGridViewCellFormattingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex < 0 ||
                results.Rows[e.RowIndex].DataBoundItem is not OneNoteImportReportEntry entry)
                return;
            var columnName = results.Columns[e.ColumnIndex].Name;
            if (columnName is not ("OpenOneNote" or "OpenDocSets" or "ShowDetails")) return;
            var enabled = columnName switch
            {
                "OpenOneNote" => !string.IsNullOrWhiteSpace(entry.OneNoteLink),
                "OpenDocSets" => openDocSets != null && !string.IsNullOrWhiteSpace(entry.DocSetsNodeId),
                _ => true
            };
            e.CellStyle.ForeColor = enabled ? results.DefaultCellStyle.ForeColor : SystemColors.GrayText;
            e.CellStyle.SelectionForeColor = enabled
                ? results.DefaultCellStyle.SelectionForeColor : SystemColors.GrayText;
        }

        private void OpenOneNote(OneNoteImportReportEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.OneNoteLink)) return;
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
                "Статус: " + entry.Status + "\r\nТип: " + entry.ObjectType + "\r\nИмя: " + entry.Name +
                "\r\nПричина: " + entry.Reason + "\r\n\r\nOneNote Page ID: " + entry.OneNotePageId +
                "\r\nOneNote Object ID: " + entry.OneNoteObjectId + "\r\nOneNote link: " + entry.OneNoteLink +
                "\r\n\r\nDocSets Node ID: " + entry.DocSetsNodeId + "\r\nDocSets Anchor ID: " + entry.DocSetsAnchorId +
                "\r\nАгрегированное предупреждение: " + (entry.IsAggregate ? "да" : "нет") +
                "\r\nСвязанные проблемы: " + string.Join(", ", entry.RelatedProblemIds ?? new List<string>()) +
                "\r\nReport ID: " + entry.Id,
                "Подробности объекта", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Request(ImportSessionCommand command)
            => CommandRequested?.Invoke(this, new ImportSessionCommandEventArgs(command, session?.Id));

        private void RequestDelete()
            => Request(ImportSessionCommand.Delete);

        private static DataGridView Grid() => new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
            AllowUserToDeleteRows = false, AutoGenerateColumns = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, MultiSelect = false
        };

        private static DataGridViewButtonColumn ResultActionColumn(string name, string text, int width)
            => new DataGridViewButtonColumn
            {
                Name = name,
                HeaderText = "",
                Text = text,
                UseColumnTextForButtonValue = true,
                Width = width,
                FlatStyle = FlatStyle.Standard,
                SortMode = DataGridViewColumnSortMode.NotSortable
            };

        private static DataGridView StatisticsGrid()
        {
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            grid.Columns.Add("name", "Показатель");
            grid.Columns.Add("value", "Значение");
            return grid;
        }

        private static void SyncRows<T>(BindingList<T> target, IReadOnlyList<T> desired,
            Func<T, string> key, Action<T, T> update, DataGridView grid)
        {
            var selectedKey = grid.CurrentRow?.DataBoundItem is T selected ? key(selected) : null;
            var selectedColumn = grid.CurrentCell?.ColumnIndex ?? 0;
            string firstVisibleKey = null;
            if (grid.FirstDisplayedScrollingRowIndex >= 0 &&
                grid.FirstDisplayedScrollingRowIndex < grid.Rows.Count &&
                grid.Rows[grid.FirstDisplayedScrollingRowIndex].DataBoundItem is T firstVisible)
                firstVisibleKey = key(firstVisible);
            grid.SuspendLayout();
            try
            {
                for (var index = 0; index < desired.Count; index++)
                {
                    var desiredRow = desired[index];
                    var desiredKey = key(desiredRow);
                    if (index < target.Count && string.Equals(key(target[index]), desiredKey, StringComparison.Ordinal))
                    {
                        if (update != null) { update(target[index], desiredRow); target.ResetItem(index); }
                        continue;
                    }
                    var existingIndex = -1;
                    for (var candidate = index + 1; candidate < target.Count; candidate++)
                        if (string.Equals(key(target[candidate]), desiredKey, StringComparison.Ordinal))
                        { existingIndex = candidate; break; }
                    if (existingIndex >= 0)
                    {
                        var existing = target[existingIndex];
                        target.RemoveAt(existingIndex);
                        target.Insert(index, existing);
                        if (update != null) { update(existing, desiredRow); target.ResetItem(index); }
                    }
                    else target.Insert(index, desiredRow);
                }
                while (target.Count > desired.Count) target.RemoveAt(target.Count - 1);
                RestoreGridPosition(grid, target, key, selectedKey, selectedColumn, firstVisibleKey);
            }
            finally { grid.ResumeLayout(); }
        }

        private static void RestoreGridPosition<T>(DataGridView grid, BindingList<T> rows,
            Func<T, string> key, string selectedKey, int selectedColumn, string firstVisibleKey)
        {
            if (!string.IsNullOrWhiteSpace(selectedKey))
            {
                var selectedIndex = FindRowIndex(rows, key, selectedKey);
                if (selectedIndex >= 0 && grid.ColumnCount > 0)
                    grid.CurrentCell = grid[Math.Min(Math.Max(0, selectedColumn), grid.ColumnCount - 1), selectedIndex];
            }
            if (!string.IsNullOrWhiteSpace(firstVisibleKey))
            {
                var firstIndex = FindRowIndex(rows, key, firstVisibleKey);
                if (firstIndex >= 0 && firstIndex < grid.RowCount)
                    try { grid.FirstDisplayedScrollingRowIndex = firstIndex; }
                    catch (InvalidOperationException) { }
            }
        }

        private static int FindRowIndex<T>(BindingList<T> rows, Func<T, string> key, string value)
        {
            for (var index = 0; index < rows.Count; index++)
                if (string.Equals(key(rows[index]), value, StringComparison.Ordinal)) return index;
            return -1;
        }

        private static void SyncList(ListBox target, IEnumerable<string> values)
        {
            var desired = (values ?? Enumerable.Empty<string>()).ToArray();
            if (target.Items.Cast<string>().SequenceEqual(desired)) return;
            target.BeginUpdate();
            try { target.Items.Clear(); target.Items.AddRange(desired.Cast<object>().ToArray()); }
            finally { target.EndUpdate(); }
        }

        private static string StatisticsSignature(ImportSessionStatistics value)
        {
            var s = value ?? new ImportSessionStatistics();
            return string.Join("|", s.Sections, s.Pages, s.Images, s.Attachments, s.Tables,
                s.Tags, s.InternalLinks, s.ExternalLinks, s.ObjectLinks, s.FailedPages);
        }

        private static string ListSignature(IEnumerable<string> values)
            => string.Join("\u001f", values ?? Enumerable.Empty<string>());

        private static int ProfileOrder(string path)
        {
            if (string.Equals(path, OneNoteImportProfile.RootPath, StringComparison.Ordinal)) return 0;
            var stage = (path ?? "").Split('/').LastOrDefault() ?? "";
            var order = new[] { "Весь импорт", "Загрузка иерархии", "Импорт страниц",
                "GetPageContent", "Разбор XML", "Конвертация", "внутренних ссылок",
                "Импорт тегов", "изображений", "вложений", "Создание узлов",
                "Сохранение assets", "Сохранение DocSet", "Обновление дерева" };
            for (var index = 0; index < order.Length; index++)
                if (stage.IndexOf(order[index], StringComparison.OrdinalIgnoreCase) >= 0) return index;
            return order.Length;
        }

        private static void CopyTimingRow(TimingRow target, TimingRow source)
        { target.Stage = source.Stage; target.Calls = source.Calls; target.Total = source.Total; target.Average = source.Average; target.Share = source.Share; }
        private static void CopyPageRow(PageRow target, PageRow source)
        { target.Page = source.Page; target.PageId = source.PageId; target.XmlSize = source.XmlSize; target.Images = source.Images; target.Attachments = source.Attachments; target.Total = source.Total; target.Breakdown = source.Breakdown; }
        private static KeyValuePair<string, int> Pair(string name, int value) => new KeyValuePair<string, int>(name, value);
        private static string LocalTime(DateTimeOffset value) => value.ToLocalTime().ToString("g");
        private static string LocalTime(DateTimeOffset? value) => value.HasValue ? LocalTime(value.Value) : "—";
        private static string FormatDuration(TimeSpan value) => value < TimeSpan.Zero ? "—" : value.ToString(@"d\.hh\:mm\:ss");
        private static string FormatBytes(long value) => value >= 1048576 ? (value / 1048576d).ToString("N1") + " MB" : value >= 1024 ? (value / 1024d).ToString("N1") + " KB" : value + " B";

        private sealed class TimingRow
        { public string Key { get; set; } public string Stage { get; set; } public long Calls { get; set; } public string Total { get; set; } public string Average { get; set; } public string Share { get; set; } }
        private sealed class PageRow
        { public string Key { get; set; } public string Page { get; set; } public string PageId { get; set; } public string XmlSize { get; set; } public int Images { get; set; } public int Attachments { get; set; } public string Total { get; set; } public string Breakdown { get; set; } }
    }
}
