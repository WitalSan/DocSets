using System.Diagnostics;

namespace DocSets.Desktop.Panels;

internal sealed class LogToolWindow : DesktopDockContent
{
    private readonly ToolStrip toolbar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ListView list = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false
    };

    public LogToolWindow() : base("log", "Лог")
    {
        var clear = new ToolStripButton("Очистить");
        var copy = new ToolStripButton("Копировать");
        var save = new ToolStripButton("Сохранить лог");
        toolbar.Items.AddRange(new ToolStripItem[] { clear, copy, save });
        list.Columns.Add("Время", 105);
        list.Columns.Add("Уровень", 85);
        list.Columns.Add("Категория", 120);
        list.Columns.Add("Сообщение", 650);
        Controls.Add(list);
        Controls.Add(toolbar);
        toolbar.Dock = DockStyle.Top;
        clear.Click += (_, __) => DocSetsLog.Current.Clear();
        copy.Click += (_, __) => Copy();
        save.Click += (_, __) => SaveLog();
        DocSetsLog.Current.EntryAdded += EntryAdded;
        DocSetsLog.Current.Cleared += (_, __) => BeginInvoke(() => list.Items.Clear());
        foreach (var entry in DocSetsLog.Current.Snapshot()) Add(entry);
        FormClosed += (_, __) => DocSetsLog.Current.EntryAdded -= EntryAdded;
    }

    private void EntryAdded(object sender, DocSetsLogEntry entry)
    {
        if (IsDisposed) return;
        if (InvokeRequired) BeginInvoke(() => Add(entry)); else Add(entry);
    }

    private void Add(DocSetsLogEntry entry)
    {
        var row = new ListViewItem(entry.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff")) { Tag = entry };
        row.SubItems.Add(entry.Level.ToString().ToUpperInvariant());
        row.SubItems.Add(entry.Category);
        row.SubItems.Add(entry.Message);
        if (entry.Level == DocSetsLogLevel.Error) row.ForeColor = Color.Firebrick;
        if (entry.Level == DocSetsLogLevel.Warning) row.ForeColor = Color.DarkOrange;
        list.Items.Add(row);
        row.EnsureVisible();
    }

    private void Copy()
    {
        var rows = list.SelectedItems.Count > 0
            ? list.SelectedItems.Cast<ListViewItem>()
            : list.Items.Cast<ListViewItem>();
        var text = string.Join(Environment.NewLine, rows.Select(row =>
            row.Tag is DocSetsLogEntry entry
                ? $"{entry.Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{entry.Level.ToString().ToUpperInvariant()}] {entry.Category}: {entry.Message}"
                : row.Text));
        if (text.Length > 0) Clipboard.SetText(text);
    }

    private void SaveLog()
    {
        using var dialog = new SaveFileDialog { Filter = "Log files (*.log)|*.log|All files (*.*)|*.*", FileName = "DocSets.log" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.Copy(DocSetsLog.Current.CurrentFilePath, dialog.FileName, true);
    }
}
