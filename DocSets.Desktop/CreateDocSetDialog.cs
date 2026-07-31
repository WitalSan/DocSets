namespace DocSets.Desktop;

internal sealed class CreateDocSetDialog : Form
{
    private readonly TextBox nameText = new() { Dock = DockStyle.Fill };
    private readonly TextBox pathText = new() { Dock = DockStyle.Fill };

    public CreateDocSetDialog(string initialDirectory)
    {
        Text = "Создание DocSet";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(680, 155);

        nameText.Text = "Новый DocSet";
        pathText.Text = Path.Combine(initialDirectory ?? Environment.CurrentDirectory, "Новый.DocSets");

        var browse = new Button { Text = "Обзор…", AutoSize = true };
        browse.Click += (_, __) => Browse();
        var ok = new Button { Text = "Создать", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        ok.Click += (_, e) =>
        {
            if (!ValidateInput()) DialogResult = DialogResult.None;
        };

        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 3,
            RowCount = 3
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.Controls.Add(new Label { Text = "Название:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        table.Controls.Add(nameText, 1, 0);
        table.SetColumnSpan(nameText, 2);
        table.Controls.Add(new Label { Text = "Каталог:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        table.Controls.Add(pathText, 1, 1);
        table.Controls.Add(browse, 2, 1);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        table.Controls.Add(buttons, 0, 2);
        table.SetColumnSpan(buttons, 3);
        Controls.Add(table);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    public string DocSetName => nameText.Text.Trim();
    public string DirectoryPath => Path.GetFullPath(pathText.Text.Trim());

    private void Browse()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Выберите родительский каталог DocSet",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(Path.GetDirectoryName(pathText.Text))
                ? Path.GetDirectoryName(pathText.Text)
                : Environment.CurrentDirectory,
            ShowNewFolderButton = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var leaf = Path.GetFileName(pathText.Text.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(leaf)) leaf = SanitizeName(nameText.Text) + ".DocSets";
        pathText.Text = Path.Combine(dialog.SelectedPath, leaf);
    }

    private bool ValidateInput()
    {
        if (string.IsNullOrWhiteSpace(nameText.Text) || string.IsNullOrWhiteSpace(pathText.Text))
        {
            MessageBox.Show(this, "Укажите название и каталог DocSet.", "DocSets",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        return true;
    }

    private static string SanitizeName(string value) => string.Concat((value ?? "DocSet")
        .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character));
}
