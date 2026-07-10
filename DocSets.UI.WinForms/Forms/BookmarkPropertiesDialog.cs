using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class BookmarkPropertiesDialog : Form
    {
        private readonly TextBox nameTextBox = new TextBox();
        private readonly ComboBox setComboBox = new ComboBox();
        private readonly ComboBox parentComboBox = new ComboBox();
        private readonly RadioButton emptyBookmarkTypeButton = new RadioButton();
        private readonly RadioButton symbolBookmarkTypeButton = new RadioButton();
        private readonly RadioButton fileBookmarkTypeButton = new RadioButton();
        private readonly TextBox pathTextBox = new TextBox();
        private readonly TextBox symbolTextBox = new TextBox();
        private readonly TextBox projectTextBox = new TextBox();
        private readonly NumericUpDown lineBox = new NumericUpDown();
        private readonly NumericUpDown columnBox = new NumericUpDown();
        private readonly CheckBox folderCheckBox = new CheckBox();
        private readonly TextBox commentTextBox = new TextBox();
        private readonly Button okButton = new Button();
        private readonly Button cancelButton = new Button();

        private readonly bool showDestination;
        private readonly IList<DocumentSet> availableSets;
        private readonly DocumentItem excludedParentRoot;
        private readonly BookmarkType initialBookmarkType;
        private DocumentSet initialSet;
        private DocumentItem initialParent;

        public BookmarkPropertiesDialog(DocumentItem item)
            : this(item, null, null, null)
        {
        }

        public BookmarkPropertiesDialog(
            DocumentItem item,
            IEnumerable<DocumentSet> sets,
            DocumentSet selectedSet,
            DocumentItem selectedParent,
            DocumentItem excludedParentRoot = null)
        {
            if (item == null)
            {
                throw new ArgumentNullException(nameof(item));
            }

            availableSets = (sets ?? Enumerable.Empty<DocumentSet>()).Where(x => x != null).ToList();
            showDestination = availableSets.Count > 0;
            initialSet = selectedSet ?? availableSets.FirstOrDefault();
            initialParent = selectedParent?.IsFolder == true ? selectedParent : null;
            this.excludedParentRoot = excludedParentRoot;
            initialBookmarkType = item.Type;

            Text = item.IsFolder ? "Свойства папки" : "Свойства закладки";
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            Font = new Font("Segoe UI", 10f);
            MinimumSize = new Size(560, showDestination ? 570 : 500);
            Size = new Size(720, showDestination ? 650 : 590);

            BuildLayout();
            LoadFrom(item);
            if (showDestination)
            {
                LoadDestination();
            }
        }

        public DocumentSet SelectedSet => showDestination ? (setComboBox.SelectedItem as SetComboItem)?.Set : null;

        public DocumentItem SelectedParent => showDestination ? (parentComboBox.SelectedItem as ParentComboItem)?.Parent : null;

        public void ApplyTo(DocumentItem item)
        {
            if (item == null)
            {
                return;
            }

            item.Name = nameTextBox.Text?.Trim() ?? string.Empty;
            item.IsFolder = folderCheckBox.Checked;
            item.Type = SelectedBookmarkType;
            item.Path = item.Type == BookmarkType.Empty ? string.Empty : pathTextBox.Text?.Trim() ?? string.Empty;
            item.Symbol = item.Type == BookmarkType.Symbol ? symbolTextBox.Text?.Trim() ?? string.Empty : string.Empty;
            item.Project = item.Type == BookmarkType.Symbol ? projectTextBox.Text?.Trim() ?? string.Empty : string.Empty;
            item.Line = (int)lineBox.Value;
            item.Column = (int)columnBox.Value;
            item.Comment = commentTextBox.Text ?? string.Empty;

            if (item.Type == BookmarkType.File && string.IsNullOrWhiteSpace(item.Name))
            {
                item.Name = CreateFileBookmarkName(item.Path, item.Line);
            }
        }

        private BookmarkType SelectedBookmarkType
            => emptyBookmarkTypeButton.Checked
                ? BookmarkType.Empty
                : fileBookmarkTypeButton.Checked ? BookmarkType.File : BookmarkType.Symbol;

        private void BuildLayout()
        {
            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = showDestination ? 12 : 10,
                Padding = new Padding(10)
            };

            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var row = 0;

            if (showDestination)
            {
                AddAutoRow(root);
                AddLabel(root, row, "Группа:");
                setComboBox.Dock = DockStyle.Fill;
                setComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
                setComboBox.SelectedIndexChanged += (_, __) => RefreshParentCombo();
                root.Controls.Add(setComboBox, 1, row++);

                AddAutoRow(root);
                AddLabel(root, row, "Папка:");
                parentComboBox.Dock = DockStyle.Fill;
                parentComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
                root.Controls.Add(parentComboBox, 1, row++);
            }

            AddAutoRow(root);
            AddLabel(root, row, "Название:");
            nameTextBox.Dock = DockStyle.Fill;
            root.Controls.Add(nameTextBox, 1, row++);

            AddAutoRow(root);
            AddLabel(root, row, "Тип:");
            folderCheckBox.Text = "Папка";
            folderCheckBox.AutoSize = true;
            folderCheckBox.CheckedChanged += (_, __) => UpdateEnabledState();
            root.Controls.Add(folderCheckBox, 1, row++);

            AddAutoRow(root);
            AddLabel(root, row, "Тип ссылки:");
            var bookmarkTypePanel = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Margin = Padding.Empty
            };

            emptyBookmarkTypeButton.Text = "Empty";
            emptyBookmarkTypeButton.Appearance = Appearance.Button;
            emptyBookmarkTypeButton.TextAlign = ContentAlignment.MiddleCenter;
            emptyBookmarkTypeButton.AutoSize = false;
            emptyBookmarkTypeButton.Width = 90;
            emptyBookmarkTypeButton.Height = 28;
            emptyBookmarkTypeButton.Margin = new Padding(0, 0, 4, 0);
            emptyBookmarkTypeButton.CheckedChanged += (_, __) => BookmarkTypeChanged();

            symbolBookmarkTypeButton.Text = "Symbol";
            symbolBookmarkTypeButton.Appearance = Appearance.Button;
            symbolBookmarkTypeButton.TextAlign = ContentAlignment.MiddleCenter;
            symbolBookmarkTypeButton.AutoSize = false;
            symbolBookmarkTypeButton.Width = 90;
            symbolBookmarkTypeButton.Height = 28;
            symbolBookmarkTypeButton.Margin = new Padding(0, 0, 4, 0);
            symbolBookmarkTypeButton.CheckedChanged += (_, __) => BookmarkTypeChanged();

            fileBookmarkTypeButton.Text = "File";
            fileBookmarkTypeButton.Appearance = Appearance.Button;
            fileBookmarkTypeButton.TextAlign = ContentAlignment.MiddleCenter;
            fileBookmarkTypeButton.AutoSize = false;
            fileBookmarkTypeButton.Width = 90;
            fileBookmarkTypeButton.Height = 28;
            fileBookmarkTypeButton.Margin = new Padding(0);
            fileBookmarkTypeButton.CheckedChanged += (_, __) => BookmarkTypeChanged();

            bookmarkTypePanel.Controls.Add(emptyBookmarkTypeButton);
            bookmarkTypePanel.Controls.Add(symbolBookmarkTypeButton);
            bookmarkTypePanel.Controls.Add(fileBookmarkTypeButton);
            root.Controls.Add(bookmarkTypePanel, 1, row++);

            AddAutoRow(root);
            AddLabel(root, row, "Файл:");
            pathTextBox.Dock = DockStyle.Fill;
            root.Controls.Add(pathTextBox, 1, row++);

            AddAutoRow(root);
            AddLabel(root, row, "Символ:");
            symbolTextBox.Dock = DockStyle.Fill;
            root.Controls.Add(symbolTextBox, 1, row++);

            AddAutoRow(root);
            AddLabel(root, row, "Проект:");
            projectTextBox.Dock = DockStyle.Fill;
            root.Controls.Add(projectTextBox, 1, row++);

            AddAutoRow(root);
            AddLabel(root, row, "Позиция:");
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
            root.Controls.Add(positionPanel, 1, row++);

            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            AddLabel(root, row, "Комментарий:");
            commentTextBox.Dock = DockStyle.Fill;
            commentTextBox.Multiline = true;
            commentTextBox.AcceptsReturn = true;
            commentTextBox.AcceptsTab = true;
            commentTextBox.ScrollBars = ScrollBars.Vertical;
            root.Controls.Add(commentTextBox, 1, row);
            root.SetRowSpan(commentTextBox, 2);
            row += 2;

            AddAutoRow(root);
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
            root.Controls.Add(buttons, 0, row);
            root.SetColumnSpan(buttons, 2);

            AcceptButton = okButton;
            CancelButton = cancelButton;
        }

        private static void AddAutoRow(TableLayoutPanel root)
        {
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        }

        private void LoadDestination()
        {
            setComboBox.Items.Clear();
            foreach (var set in availableSets)
            {
                setComboBox.Items.Add(new SetComboItem(set));
            }

            var selectedIndex = 0;
            for (var i = 0; i < setComboBox.Items.Count; i++)
            {
                if (ReferenceEquals(((SetComboItem)setComboBox.Items[i]).Set, initialSet))
                {
                    selectedIndex = i;
                    break;
                }
            }

            if (setComboBox.Items.Count > 0)
            {
                setComboBox.SelectedIndex = selectedIndex;
            }
        }

        private void RefreshParentCombo()
        {
            var selectedSet = SelectedSet;
            parentComboBox.Items.Clear();
            parentComboBox.Items.Add(new ParentComboItem(null, "<верхний уровень>"));

            if (selectedSet != null)
            {
                foreach (var folder in FlattenFolders(selectedSet.Files, 0, excludedParentRoot))
                {
                    parentComboBox.Items.Add(folder);
                }
            }

            var selectedIndex = 0;
            if (initialParent != null && selectedSet != null)
            {
                for (var i = 0; i < parentComboBox.Items.Count; i++)
                {
                    if (ReferenceEquals(((ParentComboItem)parentComboBox.Items[i]).Parent, initialParent))
                    {
                        selectedIndex = i;
                        break;
                    }
                }
            }

            parentComboBox.SelectedIndex = selectedIndex;
        }

        private static IEnumerable<ParentComboItem> FlattenFolders(IEnumerable<DocumentItem> nodes, int level, DocumentItem excludedRoot)
        {
            if (nodes == null)
            {
                yield break;
            }

            foreach (var node in nodes)
            {
                if (node == null || !node.IsFolder || IsSelfOrDescendant(node, excludedRoot))
                {
                    continue;
                }

                yield return new ParentComboItem(node, new string(' ', level * 2) + node.Name);

                foreach (var child in FlattenFolders(node.Children, level + 1, excludedRoot))
                {
                    yield return child;
                }
            }
        }

        private static bool IsSelfOrDescendant(DocumentItem item, DocumentItem root)
        {
            if (item == null || root == null)
            {
                return false;
            }

            if (ReferenceEquals(item, root))
            {
                return true;
            }

            return IsDescendant(item, root);
        }

        private static bool IsDescendant(DocumentItem item, DocumentItem parent)
        {
            if (item == null || parent == null || parent.Children == null)
            {
                return false;
            }

            foreach (var child in parent.Children)
            {
                if (ReferenceEquals(child, item) || IsDescendant(item, child))
                {
                    return true;
                }
            }

            return false;
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
            SetSelectedBookmarkType(item.Type);
            pathTextBox.Text = item.Path ?? string.Empty;
            symbolTextBox.Text = item.Symbol ?? string.Empty;
            projectTextBox.Text = item.Project ?? string.Empty;
            lineBox.Value = Math.Max(lineBox.Minimum, Math.Min(lineBox.Maximum, item.Line));
            columnBox.Value = Math.Max(columnBox.Minimum, Math.Min(columnBox.Maximum, item.Column));
            commentTextBox.Text = item.Comment ?? string.Empty;
            UpdateEnabledState();
        }

        private void SetSelectedBookmarkType(BookmarkType type)
        {
            if (type == BookmarkType.Empty)
            {
                emptyBookmarkTypeButton.Checked = true;
            }
            else if (type == BookmarkType.File)
            {
                fileBookmarkTypeButton.Checked = true;
            }
            else
            {
                symbolBookmarkTypeButton.Checked = true;
            }
        }

        private void BookmarkTypeChanged()
        {
            if (SelectedBookmarkType == BookmarkType.File)
            {
                symbolTextBox.Text = string.Empty;
                projectTextBox.Text = string.Empty;

                if (initialBookmarkType != BookmarkType.File && !string.IsNullOrWhiteSpace(pathTextBox.Text))
                {
                    nameTextBox.Text = CreateFileBookmarkName(pathTextBox.Text, (int)lineBox.Value);
                }
            }

            UpdateEnabledState();
        }

        private void UpdateEnabledState()
        {
            var isEmpty = SelectedBookmarkType == BookmarkType.Empty;
            var isFileBookmark = SelectedBookmarkType == BookmarkType.File;

            pathTextBox.Enabled = !isEmpty;
            lineBox.Enabled = !isEmpty;
            columnBox.Enabled = !isEmpty;
            symbolTextBox.Enabled = !isEmpty && !isFileBookmark;
            projectTextBox.Enabled = !isEmpty && !isFileBookmark;
        }

        private static string CreateFileBookmarkName(string path, int line)
        {
            var fileName = string.IsNullOrWhiteSpace(path) ? "File" : Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(fileName) ? $"File:{Math.Max(1, line)}" : $"{fileName}:{Math.Max(1, line)}";
        }

        private sealed class SetComboItem
        {
            public SetComboItem(DocumentSet set)
            {
                Set = set;
            }

            public DocumentSet Set { get; }

            public override string ToString() => Set?.Name ?? string.Empty;
        }

        private sealed class ParentComboItem
        {
            public ParentComboItem(DocumentItem parent, string text)
            {
                Parent = parent;
                Text = text ?? string.Empty;
            }

            public DocumentItem Parent { get; }

            public string Text { get; }

            public override string ToString() => Text;
        }
    }
}
