using System;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class DocSetsLogControl : UserControl
    {
        private readonly DocSetsLog log;
        private readonly ToolStrip toolbar = new ToolStrip();
        private readonly ToolStripButton clearButton = new ToolStripButton("Очистить");
        private readonly ToolStripButton copyButton = new ToolStripButton("Копировать");
        private readonly ToolStripButton openFileButton = new ToolStripButton("Открыть файл");
        private readonly ToolStripButton autoScrollButton = new ToolStripButton("Автопрокрутка") { CheckOnClick = true, Checked = true };
        private readonly ToolStripComboBox levelBox = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ToolStripComboBox categoryBox = new ToolStripComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly ListView list = new ListView();

        public DocSetsLogControl() : this(DocSetsLog.Current)
        {
        }

        internal DocSetsLogControl(DocSetsLog log)
        {
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            Dock = DockStyle.Fill;
            BuildLayout();
            this.log.EntryAdded += Log_EntryAdded;
            this.log.Cleared += Log_Cleared;
            Disposed += (_, __) =>
            {
                this.log.EntryAdded -= Log_EntryAdded;
                this.log.Cleared -= Log_Cleared;
            };
            RefreshEntries();
        }

        private void BuildLayout()
        {
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Dock = DockStyle.Top;
            levelBox.Items.AddRange(new object[] { "Все уровни", "Трассировка", "Информация", "Предупреждение", "Ошибка" });
            levelBox.SelectedIndex = 0;
            categoryBox.Items.Add("Все категории");
            categoryBox.SelectedIndex = 0;
            categoryBox.AutoSize = false;
            categoryBox.Width = DpiService.Scale(this, 130);
            toolbar.Items.Add(clearButton);
            toolbar.Items.Add(copyButton);
            toolbar.Items.Add(openFileButton);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(autoScrollButton);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(levelBox);
            toolbar.Items.Add(categoryBox);

            clearButton.Click += (_, __) => log.Clear();
            copyButton.Click += (_, __) => CopySelection();
            openFileButton.Click += (_, __) => OpenLogFile();
            levelBox.SelectedIndexChanged += (_, __) => RefreshEntries();
            categoryBox.SelectedIndexChanged += (_, __) => RefreshEntries();

            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = false;
            list.HideSelection = false;
            list.Columns.Add("Время", DpiService.Scale(this, 95));
            list.Columns.Add("Уровень", DpiService.Scale(this, 80));
            list.Columns.Add("Категория", DpiService.Scale(this, 110));
            list.Columns.Add("Сообщение", DpiService.Scale(this, 600));
            list.DoubleClick += (_, __) => ShowSelectedDetails();
            list.KeyDown += List_KeyDown;

            Controls.Add(list);
            Controls.Add(toolbar);
        }

        private void List_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.Insert))
            {
                CopySelection();
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }

            if (e.Control && e.KeyCode == Keys.A)
            {
                foreach (ListViewItem item in list.Items) item.Selected = true;
                e.Handled = true;
                e.SuppressKeyPress = true;
            }
        }

        private void Log_EntryAdded(object sender, DocSetsLogEntry entry)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => Log_EntryAdded(sender, entry))); }
                catch (InvalidOperationException) { }
                return;
            }

            EnsureCategory(entry.Category);
            if (!MatchesFilter(entry)) return;
            AddEntry(entry);
        }

        private void Log_Cleared(object sender, EventArgs e)
        {
            if (IsDisposed) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => Log_Cleared(sender, e))); }
                catch (InvalidOperationException) { }
                return;
            }
            list.Items.Clear();
        }

        private void RefreshEntries()
        {
            if (list.IsDisposed) return;
            var entries = log.Snapshot();
            var selectedCategory = categoryBox.SelectedItem as string ?? "Все категории";
            categoryBox.BeginUpdate();
            foreach (var category in entries.Select(x => x.Category).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x))
                EnsureCategory(category);
            categoryBox.EndUpdate();
            if (categoryBox.Items.Contains(selectedCategory)) categoryBox.SelectedItem = selectedCategory;

            list.BeginUpdate();
            list.Items.Clear();
            foreach (var entry in entries.Where(MatchesFilter)) AddEntry(entry, false);
            list.EndUpdate();
            ScrollToEnd();
        }

        private bool MatchesFilter(DocSetsLogEntry entry)
        {
            var level = levelBox.SelectedItem as string;
            if (!string.IsNullOrWhiteSpace(level) && level != "Все уровни" &&
                !string.Equals(level, FormatLevel(entry.Level), StringComparison.OrdinalIgnoreCase)) return false;
            var category = categoryBox.SelectedItem as string;
            return string.IsNullOrWhiteSpace(category) || category == "Все категории" ||
                   string.Equals(category, entry.Category, StringComparison.OrdinalIgnoreCase);
        }

        private void AddEntry(DocSetsLogEntry entry, bool scroll = true)
        {
            var item = new ListViewItem(entry.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff")) { Tag = entry };
            item.SubItems.Add(FormatLevel(entry.Level));
            item.SubItems.Add(entry.Category);
            item.SubItems.Add(entry.Message);
            if (entry.Level == DocSetsLogLevel.Warning) item.ForeColor = Color.DarkOrange;
            if (entry.Level == DocSetsLogLevel.Error) item.ForeColor = Color.Firebrick;
            list.Items.Add(item);
            if (scroll) ScrollToEnd();
        }

        private void EnsureCategory(string category)
        {
            if (string.IsNullOrWhiteSpace(category)) return;
            if (!categoryBox.Items.Cast<object>().Any(x => string.Equals(x?.ToString(), category, StringComparison.OrdinalIgnoreCase)))
                categoryBox.Items.Add(category);
        }

        private void ScrollToEnd()
        {
            if (autoScrollButton.Checked && list.Items.Count > 0) list.EnsureVisible(list.Items.Count - 1);
        }

        private void CopySelection()
        {
            var items = list.SelectedItems.Count > 0
                ? list.SelectedItems.Cast<ListViewItem>()
                : list.Items.Cast<ListViewItem>();
            var builder = new StringBuilder();
            foreach (var item in items)
            {
                var entry = item.Tag as DocSetsLogEntry;
                if (entry == null) continue;
                builder.Append(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(" | ").Append(FormatLevel(entry.Level))
                    .Append(" | ").Append(entry.Category)
                    .Append(" | ").AppendLine(entry.Message);
                if (entry.Exception != null) builder.AppendLine(entry.Exception.ToString());
            }
            if (builder.Length > 0) Clipboard.SetText(builder.ToString());
        }

        private void OpenLogFile()
        {
            try
            {
                var path = log.CurrentFilePath;
                if (!System.IO.File.Exists(path)) log.Info("Журнал", "Файл журнала создан.");
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось открыть файл журнала.\n" + ex.Message,
                    "DocSets", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ShowSelectedDetails()
        {
            if (list.SelectedItems.Count == 0) return;
            var entry = list.SelectedItems[0].Tag as DocSetsLogEntry;
            if (entry == null) return;
            var text = entry.Message + (entry.Exception == null ? "" : Environment.NewLine + Environment.NewLine + entry.Exception);
            MessageBox.Show(this, text, entry.Category, MessageBoxButtons.OK,
                entry.Level == DocSetsLogLevel.Error ? MessageBoxIcon.Error : MessageBoxIcon.Information);
        }

        private static string FormatLevel(DocSetsLogLevel level)
        {
            switch (level)
            {
                case DocSetsLogLevel.Trace: return "Трассировка";
                case DocSetsLogLevel.Info: return "Информация";
                case DocSetsLogLevel.Warning: return "Предупреждение";
                case DocSetsLogLevel.Error: return "Ошибка";
                default: return level.ToString();
            }
        }
    }
}
