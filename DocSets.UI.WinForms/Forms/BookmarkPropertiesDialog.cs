using System;
using System.Drawing;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class BookmarkPropertiesDialog : Form
    {
        private readonly TextBox nameTextBox = new TextBox();
        private readonly TextBox pathTextBox = new TextBox();
        private readonly TextBox symbolTextBox = new TextBox();
        private readonly TextBox projectTextBox = new TextBox();
        private readonly NumericUpDown lineBox = new NumericUpDown();
        private readonly NumericUpDown columnBox = new NumericUpDown();
        private readonly CheckBox folderCheckBox = new CheckBox();
        private readonly TextBox commentTextBox = new TextBox();
        private readonly Button okButton = new Button();
        private readonly Button cancelButton = new Button();

        public BookmarkPropertiesDialog(DocumentItem item)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            Text = item.IsFolder ? "Свойства папки" : "Свойства закладки";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Font = new Font("Segoe UI", 10f);
            MinimumSize = new Size(560, 480);
            Size = new Size(720, 560);

            BuildLayout();
            LoadFrom(item);
        }

        public void ApplyTo(DocumentItem item)
        {
            if (item == null)
            {
                return;
            }

            item.Name = nameTextBox.Text?.Trim() ?? string.Empty;
            item.IsFolder = folderCheckBox.Checked;
            item.Path = pathTextBox.Text?.Trim() ?? string.Empty;
            item.Symbol = symbolTextBox.Text?.Trim() ?? string.Empty;
            item.Project = projectTextBox.Text?.Trim() ?? string.Empty;
            item.Line = (int)lineBox.Value;
            item.Column = (int)columnBox.Value;
            item.Comment = commentTextBox.Text ?? string.Empty;
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 9,
                Padding = new Padding(10)
            };

            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            AddLabel(root, 0, "Название:");
            nameTextBox.Dock = DockStyle.Fill;
            root.Controls.Add(nameTextBox, 1, 0);

            AddLabel(root, 1, "Тип:");
            folderCheckBox.Text = "Папка";
            folderCheckBox.AutoSize = true;
            folderCheckBox.CheckedChanged += (_, __) => UpdateEnabledState();
            root.Controls.Add(folderCheckBox, 1, 1);

            AddLabel(root, 2, "Файл:");
            pathTextBox.Dock = DockStyle.Fill;
            root.Controls.Add(pathTextBox, 1, 2);

            AddLabel(root, 3, "Символ:");
            symbolTextBox.Dock = DockStyle.Fill;
            root.Controls.Add(symbolTextBox, 1, 3);

            AddLabel(root, 4, "Проект:");
            projectTextBox.Dock = DockStyle.Fill;
            root.Controls.Add(projectTextBox, 1, 4);

            AddLabel(root, 5, "Позиция:");
            var positionPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            lineBox.Minimum = 1;
            lineBox.Maximum = 10000000;
            lineBox.Width = 90;
            columnBox.Minimum = 1;
            columnBox.Maximum = 10000000;
            columnBox.Width = 90;
            positionPanel.Controls.Add(new Label { Text = "Строка", AutoSize = true, Padding = new Padding(0, 4, 4, 0) });
            positionPanel.Controls.Add(lineBox);
            positionPanel.Controls.Add(new Label { Text = "Колонка", AutoSize = true, Padding = new Padding(12, 4, 4, 0) });
            positionPanel.Controls.Add(columnBox);
            root.Controls.Add(positionPanel, 1, 5);

            AddLabel(root, 6, "Комментарий:");
            commentTextBox.Dock = DockStyle.Fill;
            commentTextBox.Multiline = true;
            commentTextBox.AcceptsReturn = true;
            commentTextBox.AcceptsTab = true;
            commentTextBox.ScrollBars = ScrollBars.Vertical;
            root.Controls.Add(commentTextBox, 1, 6);
            root.SetRowSpan(commentTextBox, 2);

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false
            };

            okButton.Text = "OK";
            okButton.DialogResult = DialogResult.OK;
            okButton.Width = 90;
            cancelButton.Text = "Отмена";
            cancelButton.DialogResult = DialogResult.Cancel;
            cancelButton.Width = 90;
            buttons.Controls.Add(cancelButton);
            buttons.Controls.Add(okButton);
            root.Controls.Add(buttons, 0, 8);
            root.SetColumnSpan(buttons, 2);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private static void AddLabel(TableLayoutPanel root, int row, string text)
        {
            root.Controls.Add(new Label
            {
                Text = text,
                AutoSize = true,
                Padding = new Padding(0, 4, 10, 4),
                Anchor = AnchorStyles.Left | AnchorStyles.Top
            }, 0, row);
        }

        private void LoadFrom(DocumentItem item)
        {
            nameTextBox.Text = item.Name ?? string.Empty;
            folderCheckBox.Checked = item.IsFolder;
            pathTextBox.Text = item.Path ?? string.Empty;
            symbolTextBox.Text = item.Symbol ?? string.Empty;
            projectTextBox.Text = item.Project ?? string.Empty;
            lineBox.Value = Math.Max(lineBox.Minimum, Math.Min(lineBox.Maximum, item.Line));
            columnBox.Value = Math.Max(columnBox.Minimum, Math.Min(columnBox.Maximum, item.Column));
            commentTextBox.Text = item.Comment ?? string.Empty;
            UpdateEnabledState();
        }

        private void UpdateEnabledState()
        {
            var enabled = !folderCheckBox.Checked;
            pathTextBox.Enabled = enabled;
            symbolTextBox.Enabled = enabled;
            projectTextBox.Enabled = enabled;
            lineBox.Enabled = enabled;
            columnBox.Enabled = enabled;
        }
    }
}
