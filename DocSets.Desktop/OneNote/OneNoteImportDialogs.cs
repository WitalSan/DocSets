namespace DocSets.Desktop.OneNote;

internal sealed class OneNoteNotebookDialog : Form
{
    private readonly ListBox notebooks = new ListBox
        { Dock = DockStyle.Fill, DisplayMember = nameof(OneNoteNotebook.Name) };

    private OneNoteNotebookDialog(IReadOnlyList<OneNoteNotebook> values)
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = SystemFonts.MessageBoxFont;
        Text = "Импорт из OneNote — выбор записной книжки";
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false; MaximizeBox = false; ShowInTaskbar = false;
        Width = 560; Height = 380;
        foreach (var notebook in values) notebooks.Items.Add(notebook);
        if (notebooks.Items.Count > 0) notebooks.SelectedIndex = 0;
        var label = new Label { Text = "Выберите записную книжку OneNote:", Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(8) };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        var import = new Button { Text = "Импортировать", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        buttons.Controls.Add(import); buttons.Controls.Add(cancel);
        Controls.Add(notebooks); Controls.Add(label); Controls.Add(buttons);
        AcceptButton = import; CancelButton = cancel;
        import.Enabled = notebooks.Items.Count > 0;
        notebooks.SelectedIndexChanged += (_, __) => import.Enabled = notebooks.SelectedItem != null;
        notebooks.DoubleClick += (_, __) => { if (notebooks.SelectedItem != null) DialogResult = DialogResult.OK; };
    }

    public static OneNoteNotebook Select(IWin32Window owner, IReadOnlyList<OneNoteNotebook> values)
    {
        using var dialog = new OneNoteNotebookDialog(values);
        return dialog.ShowDialog(owner) == DialogResult.OK
            ? dialog.notebooks.SelectedItem as OneNoteNotebook : null;
    }
}
