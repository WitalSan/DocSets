using Aga.Controls.Tree;
using Aga.Controls.Tree.NodeControls;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using WpfCommand = System.Windows.Input.ICommand;

namespace DocSets
{
    internal sealed class DocSetsWinFormsControl : UserControl
    {
        private readonly DocSetsViewModel viewModel;
        private readonly ComboBox setsCombo = new ComboBox();
        private readonly ToolStrip groupsStrip = new ToolStrip();
        private readonly ToolStrip toolStrip = new ToolStrip();
        private readonly TreeViewAdv tree = new TreeViewAdv();
        private readonly TreeModel treeModel = new TreeModel();
        private readonly Label statusLabel = new Label();
        private readonly ContextMenuStrip nodeMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip headerMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip groupMenu = new ContextMenuStrip();
        private readonly Dictionary<string, TreeColumn> columnsByKey = new Dictionary<string, TreeColumn>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TreeColumn, string> columnKeys = new Dictionary<TreeColumn, string>();
        private bool refreshing;
        private bool suppressColumnSave;
        private ToolStripControlHost editingGroupHost;
        private DocumentSet editingGroupSet;
        private bool cancelGroupRename;

        public DocSetsWinFormsControl(DocSetsViewModel viewModel)
        {
            this.viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            Dock = DockStyle.Fill;
            BuildLayout();
            BuildTree();
            BuildMenus();
            WireEvents();
            RefreshAll();
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var font in boldFonts.Values)
                {
                    font.Dispose();
                }

                boldFonts.Clear();
            }

            base.Dispose(disposing);
        }

        private void BuildLayout()
        {
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 4, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(root);

            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(3) };
            top.Controls.Add(new Label { Text = "Группа:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            setsCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            setsCombo.Width = 220;
            top.Controls.Add(setsCombo);
            root.Controls.Add(top, 0, 0);

            toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            AddButton("+Группа", viewModel.AddSetCommand);
            AddButton("Переим.", viewModel.RenameSetCommand);
            AddButton("-Группа", viewModel.DeleteSetCommand);
            toolStrip.Items.Add(new ToolStripSeparator());
            AddButton("+Папка", viewModel.AddRootFolderCommand);
            AddButton("+Вложенная", viewModel.AddChildFolderCommand);
            AddButton("+Закладка", viewModel.AddBookmarkCommand);
            AddButton("Копировать", viewModel.CopySelectedNodesCommand);
            AddButton("Вставить", viewModel.PasteNodesCommand);
            top.Controls.Add(toolStrip);

            groupsStrip.Dock = DockStyle.Fill;
            groupsStrip.GripStyle = ToolStripGripStyle.Hidden;
            groupsStrip.RenderMode = ToolStripRenderMode.System;
            groupsStrip.CanOverflow = true;
            groupsStrip.Stretch = true;
            groupsStrip.AutoSize = true;
            groupsStrip.Padding = new Padding(2, 1, 2, 1);
            groupsStrip.MouseUp += GroupsStrip_MouseUp;
            groupsStrip.KeyDown += GroupsStrip_KeyDown;
            root.Controls.Add(groupsStrip, 0, 1);

            statusLabel.Dock = DockStyle.Fill;
            statusLabel.AutoEllipsis = true;
            statusLabel.Padding = new Padding(4, 2, 4, 2);
            root.Controls.Add(tree, 0, 2);
            root.Controls.Add(statusLabel, 0, 3);
        }

        private static IEnumerable<ColumnSpec> GetDefaultColumnSpecs()
        {
            yield return new ColumnSpec("name", "Название", 340);
            yield return new ColumnSpec("file", "Файл", 280);
            yield return new ColumnSpec("line", "Строка", 70);
            yield return new ColumnSpec("comment", "Комментарий", 240);
            yield return new ColumnSpec("project", "Проект", 160);
            yield return new ColumnSpec("symbol", "Символ", 260);
        }

        private void BuildColumns()
        {
            columnsByKey.Clear();
            columnKeys.Clear();
            tree.Columns.Clear();

            var specs = GetDefaultColumnSpecs().ToList();
            var layout = viewModel.Ui?.Columns ?? new List<ColumnLayout>();
            var layoutByKey = layout.ToDictionary(x => x.Key ?? string.Empty, StringComparer.OrdinalIgnoreCase);

            var orderedSpecs = specs
                .Select((spec, defaultOrder) => new
                {
                    Spec = spec,
                    Order = layoutByKey.TryGetValue(spec.Key, out var saved) ? saved.Order : defaultOrder
                })
                .OrderBy(x => x.Order)
                .Select(x => x.Spec)
                .ToList();

            suppressColumnSave = true;
            try
            {
                foreach (var spec in orderedSpecs)
                {
                    layoutByKey.TryGetValue(spec.Key, out var saved);
                    var column = new TreeColumn(spec.Header, saved?.Width > 0 ? saved.Width : spec.DefaultWidth)
                    {
                        IsVisible = saved?.IsVisible ?? true
                    };

                    tree.Columns.Add(column);
                    columnsByKey[spec.Key] = column;
                    columnKeys[column] = spec.Key;
                }
            }
            finally
            {
                suppressColumnSave = false;
            }

            SaveColumnLayout();
        }

        private void SaveColumnLayout()
        {
            if (suppressColumnSave || viewModel.Ui == null)
            {
                return;
            }

            var layouts = new List<ColumnLayout>();
            for (var i = 0; i < tree.Columns.Count; i++)
            {
                var column = tree.Columns[i];
                if (!columnKeys.TryGetValue(column, out var key))
                {
                    continue;
                }

                layouts.Add(new ColumnLayout
                {
                    Key = key,
                    Order = i,
                    Width = column.Width,
                    IsVisible = column.IsVisible
                });
            }

            viewModel.Ui.Columns = layouts;
            _ = viewModel.SaveAsync();
        }

        private void ShowHeaderMenu(Point location)
        {
            headerMenu.Items.Clear();
            var clickedColumn = GetColumnAt(location);

            foreach (var spec in GetDefaultColumnSpecs())
            {
                if (!columnsByKey.TryGetValue(spec.Key, out var column))
                {
                    continue;
                }

                var item = new ToolStripMenuItem(spec.Header)
                {
                    Checked = column.IsVisible,
                    CheckOnClick = true,
                    Tag = column
                };

                item.CheckedChanged += (_, __) =>
                {
                    var menuItem = (ToolStripMenuItem)_;
                    var c = (TreeColumn)menuItem.Tag;
                    if (!menuItem.Checked && tree.Columns.Cast<TreeColumn>().Count(x => x.IsVisible) <= 1)
                    {
                        menuItem.Checked = true;
                        return;
                    }

                    c.IsVisible = menuItem.Checked;
                    tree.Update();
                    SaveColumnLayout();
                };

                headerMenu.Items.Add(item);
            }

            headerMenu.Items.Add(new ToolStripSeparator());

            var showAll = new ToolStripMenuItem("Показать все колонки");
            showAll.Click += (_, __) =>
            {
                foreach (TreeColumn column in tree.Columns)
                {
                    column.IsVisible = true;
                }

                tree.Update();
                SaveColumnLayout();
            };
            headerMenu.Items.Add(showAll);

            var autoWidth = new ToolStripMenuItem("Автоширина видимых колонок");
            autoWidth.Click += (_, __) =>
            {
                AutoSizeVisibleColumns();
                SaveColumnLayout();
            };
            headerMenu.Items.Add(autoWidth);

            if (clickedColumn != null)
            {
                headerMenu.Items.Add(new ToolStripSeparator());

                var left = new ToolStripMenuItem("Сдвинуть колонку влево");
                left.Enabled = tree.Columns.IndexOf(clickedColumn) > 0;
                left.Click += (_, __) => MoveColumn(clickedColumn, -1);
                headerMenu.Items.Add(left);

                var right = new ToolStripMenuItem("Сдвинуть колонку вправо");
                right.Enabled = tree.Columns.IndexOf(clickedColumn) >= 0 && tree.Columns.IndexOf(clickedColumn) < tree.Columns.Count - 1;
                right.Click += (_, __) => MoveColumn(clickedColumn, 1);
                headerMenu.Items.Add(right);
            }

            headerMenu.Show(tree, location);
        }


        private TreeColumn GetColumnAt(Point location)
        {
            if (location.Y > tree.ColumnHeaderHeight)
            {
                return null;
            }

            var x = -tree.OffsetX;
            foreach (TreeColumn column in tree.Columns)
            {
                if (!column.IsVisible)
                {
                    continue;
                }

                var rect = new Rectangle(x, 0, column.Width, tree.ColumnHeaderHeight);
                if (rect.Contains(location))
                {
                    return column;
                }

                x += column.Width;
            }

            return null;
        }

        private void MoveColumn(TreeColumn column, int delta)
        {
            var index = tree.Columns.IndexOf(column);
            var newIndex = index + delta;
            if (index < 0 || newIndex < 0 || newIndex >= tree.Columns.Count)
            {
                return;
            }

            suppressColumnSave = true;
            try
            {
                tree.Columns.Remove(column);
                tree.Columns.Insert(newIndex, column);
            }
            finally
            {
                suppressColumnSave = false;
            }

            tree.Update();
            SaveColumnLayout();
        }

        private void AutoSizeVisibleColumns()
        {
            foreach (TreeColumn column in tree.Columns)
            {
                if (!column.IsVisible || !columnKeys.TryGetValue(column, out var key))
                {
                    continue;
                }

                var headerWidth = TextRenderer.MeasureText(column.Header, tree.Font).Width + 24;
                var valueWidth = EnumerateItems(viewModel.CurrentNodes)
                    .Select(x => GetColumnText(x, key))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Select(x => TextRenderer.MeasureText(x, tree.Font).Width + 24)
                    .DefaultIfEmpty(0)
                    .Max();

                column.Width = Math.Min(Math.Max(headerWidth, valueWidth), 700);
            }

            tree.Update();
        }

        private static IEnumerable<DocumentItem> EnumerateItems(IEnumerable<DocumentItem> items)
        {
            if (items == null)
            {
                yield break;
            }

            foreach (var item in items)
            {
                yield return item;
                foreach (var child in EnumerateItems(item.Children))
                {
                    yield return child;
                }
            }
        }

        private static string GetColumnText(DocumentItem item, string key)
        {
            switch (key)
            {
                case "name": return item?.Name ?? string.Empty;
                case "file": return item?.Path ?? string.Empty;
                case "line": return item == null || item.IsFolder ? string.Empty : item.Line.ToString();
                case "comment": return item?.CommentFirstLine ?? string.Empty;
                case "project": return item?.Project ?? string.Empty;
                case "symbol": return item?.Symbol ?? string.Empty;
                default: return string.Empty;
            }
        }

        private void AddButton(string text, WpfCommand command)
        {
            var button = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text };
            button.Click += (_, __) => Execute(command, null);
            toolStrip.Items.Add(button);
        }

        private void BuildTree()
        {
            tree.Font = new Font("Segoe UI", 11f);
            tree.AutoRowHeight = true;
            //tree.RowHeight = 22;
            tree.AutoHeaderHeight = true;

            tree.Dock = DockStyle.Fill;
            tree.Model = treeModel;
            tree.UseColumns = true;
            tree.FullRowSelect = true;
            tree.FullRowSelectActiveColor = SystemColors.Highlight;
            tree.FullRowSelectInactiveColor = SystemColors.InactiveBorder;

            tree.GridLineStyle = GridLineStyle.Horizontal;
            tree.SelectionMode = TreeSelectionMode.Multi;
            tree.HideSelection = false;
            tree.AllowDrop = true;
            tree.AllowColumnReorder = true;
            tree.HighlightDropPosition = true;
            tree.ShowNodeToolTips = true;
            tree.DefaultToolTipProvider = new BookmarkToolTipProvider();
            tree.DragDropMarkColor = Color.DodgerBlue;
            tree.DragDropMarkWidth = 2;
            tree.TopEdgeSensivity = 0.25f;
            tree.BottomEdgeSensivity = 0.25f;

            BuildColumns();

            tree.NodeControls.Add(new NodeStateIcon { LeftMargin = 1 });
            tree.NodeControls.Add(new ExpandingIcon { LeftMargin = 1 });
            var nameNode = new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Name), ParentColumn = columnsByKey["name"], EditEnabled = true, IncrementalSearchEnabled = true };
            nameNode.DrawText += NameTextBox_DrawText;
            tree.NodeControls.Add(nameNode);
            tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.File), ParentColumn = columnsByKey["file"] });
            tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Line), ParentColumn = columnsByKey["line"] });
            tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Comment), ParentColumn = columnsByKey["comment"] });
            tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Project), ParentColumn = columnsByKey["project"] });
            tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Symbol), ParentColumn = columnsByKey["symbol"] });
        }

        private void BuildMenus()
        {
            //AddMenu("Открыть", viewModel.OpenBookmarkCommand);
            //AddMenu("Обновить", viewModel.UpdateBookmarkCommand);
            AddMenu("Переименовать", viewModel.RenameNodeCommand);
            AddMenu("Удалить", viewModel.DeleteNodeCommand);
            nodeMenu.Items.Add(new ToolStripSeparator());
            AddMenu("Добавить Папку", viewModel.AddChildFolderCommand);
            AddMenu("Добавить Закладку", viewModel.AddBookmarkCommand);
            nodeMenu.Items.Add(new ToolStripSeparator());
            AddMenu("Копировать", viewModel.CopySelectedNodesCommand, "Ctrl+C");
            AddMenu("Вставить", viewModel.PasteNodesCommand, "Ctrl+V");
            AddPropertiesMenu();
            BuildGroupMenu();
        }

        private void BuildGroupMenu()
        {
            AddGroupMenu("Добавить группу", viewModel.AddSetCommand);
            groupMenu.Items.Add(new ToolStripSeparator());
            AddRenameGroupMenu();
            AddGroupMenu("Удалить группу", viewModel.DeleteSetCommand);
            groupMenu.Items.Add(new ToolStripSeparator());
            AddGroupMenu("Передвинуть влево", viewModel.MoveSetUpCommand);
            AddGroupMenu("Передвинуть вправо", viewModel.MoveSetDownCommand);

            groupMenu.Opening += (_, e) =>
            {
                SelectGroupMenuTarget();

                foreach (ToolStripItem item in groupMenu.Items)
                {
                    if (item is ToolStripMenuItem menuItem)
                    {
                        if (string.Equals(menuItem.Name, "RenameGroupMenuItem", StringComparison.Ordinal))
                        {
                            menuItem.Enabled = viewModel.SelectedSet != null;
                            continue;
                        }

                        var command = menuItem.Tag as WpfCommand;
                        menuItem.Enabled = command == null || command.CanExecute(null);
                    }
                }
            };
        }


        private void AddRenameGroupMenu()
        {
            var item = new ToolStripMenuItem("Переименовать")
            {
                Name = "RenameGroupMenuItem"
            };

            item.Click += (_, __) =>
            {
                SelectGroupMenuTarget();

                var button = FindGroupButton(viewModel.SelectedSet);
                if (button != null)
                {
                    BeginRenameGroup(button);
                }
            };

            groupMenu.Items.Add(item);
        }


        private void AddPropertiesMenu()
        {
            var item = new ToolStripMenuItem("Свойства...");
            item.Click += delegate { ShowBookmarkProperties(GetCurrentItem()); };
            nodeMenu.Items.Add(item);
        }

        private void ShowBookmarkProperties(DocumentItem item)
        {
            if (item == null)
            {
                return;
            }

            using (var dialog = new BookmarkPropertiesDialog(item))
            {
                var result = dialog.ShowDialog(this);
                if (result != DialogResult.OK)
                {
                    return;
                }

                dialog.ApplyTo(item);
                _ = viewModel.SaveAsync();
                RebuildTree();
                RefreshStatus();
            }
        }

        private void AddGroupMenu(string text, WpfCommand command)
        {
            var item = new ToolStripMenuItem(text)
            {
                Tag = command
            };

            item.Click += (_, __) =>
            {
                SelectGroupMenuTarget();
                Execute(command, null);
                RefreshAll();
            };

            groupMenu.Items.Add(item);
        }


        private ToolStripButton FindGroupButton(DocumentSet set)
        {
            if (set == null)
            {
                return null;
            }

            return groupsStrip.Items
                .OfType<ToolStripButton>()
                .FirstOrDefault(x => ReferenceEquals(x.Tag, set));
        }

        private void GroupButton_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            var button = sender as ToolStripButton;
            if (button == null)
            {
                return;
            }

            groupMenu.Tag = button.Tag as DocumentSet;
            SelectSetFromButton(button);
            groupMenu.Show(Cursor.Position);
        }

        private void GroupsStrip_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            var item = groupsStrip.GetItemAt(e.Location) as ToolStripButton;
            if (item != null && item.Tag is DocumentSet)
            {
                return;
            }

            groupMenu.Tag = null;
            groupMenu.Show(groupsStrip, e.Location);
        }

        private void SelectGroupMenuTarget()
        {
            if (groupMenu.Tag is DocumentSet set && !ReferenceEquals(viewModel.SelectedSet, set))
            {
                viewModel.SelectedSet = set;
                setsCombo.SelectedItem = set;
                UpdateGroupButtonsChecked();
                RebuildTree();
                RefreshStatus();
            }
        }

        private void UpdateGroupButtonsChecked()
        {
            foreach (ToolStripItem item in groupsStrip.Items)
            {
                if (item is ToolStripButton groupButton && groupButton.Tag is DocumentSet group)
                {
                    groupButton.Checked = ReferenceEquals(group, viewModel.SelectedSet);
                }
            }
        }

        private void AddMenu(string text, WpfCommand command, string shortcut = null)
        {
            var item = new ToolStripMenuItem(text);
            if (!string.IsNullOrEmpty(shortcut)) item.ShortcutKeyDisplayString = shortcut;
            item.Click += (_, __) => Execute(command, GetCurrentItem());
            nodeMenu.Items.Add(item);
        }

        private void WireEvents()
        {
            setsCombo.SelectedIndexChanged += (_, __) =>
            {
                if (refreshing) return;
                viewModel.SelectedSet = setsCombo.SelectedItem as DocumentSet;
                RefreshGroupsStrip();
                RebuildTree();
                RefreshStatus();
            };

            tree.SelectionChanged += (_, __) => SyncSelectionFromTree();
            tree.NodeMouseDoubleClick += (_, e) => Execute(viewModel.OpenBookmarkCommand, (e.Node?.Tag as BookmarkTreeNode)?.Item);
            tree.ItemDrag += (_, __) => tree.DoDragDropSelectedNodes(DragDropEffects.Move);
            tree.DragOver += Tree_DragOver;
            tree.DragDrop += Tree_DragDrop;
            tree.KeyDown += Tree_KeyDown;
            tree.MouseUp += Tree_MouseUp;
            tree.ColumnWidthChanged += (_, __) => SaveColumnLayout();
            tree.ColumnReordered += (_, __) => SaveColumnLayout();
            nodeMenu.Opening += (_, e) =>
            {
                SyncSelectionFromTree();
                var current = GetCurrentItem();
                foreach (ToolStripItem i in nodeMenu.Items)
                {
                    if (i is ToolStripMenuItem mi)
                    {
                        var cmd = GetCommandByText(mi.Text);
                        mi.Enabled = cmd?.CanExecute(current) ?? true;
                    }
                }
            };
        }

        private WpfCommand GetCommandByText(string text)
        {
            switch (text)
            {
                case "Открыть": return viewModel.OpenBookmarkCommand;
                case "Обновить": return viewModel.UpdateBookmarkCommand;
                case "Переименовать": return viewModel.RenameNodeCommand;
                case "Добавить вложенную папку": return viewModel.AddChildFolderCommand;
                case "Добавить закладку сюда": return viewModel.AddBookmarkCommand;
                case "Копировать": return viewModel.CopySelectedNodesCommand;
                case "Вставить": return viewModel.PasteNodesCommand;
                case "Удалить": return viewModel.DeleteNodeCommand;
                default: return null;
            }
        }

        public void RefreshAll()
        {
            refreshing = true;
            try
            {
                setsCombo.Items.Clear();
                foreach (var set in viewModel.Sets)
                {
                    setsCombo.Items.Add(set);
                }

                setsCombo.SelectedItem = viewModel.SelectedSet;
                RefreshGroupsStrip();
            }
            finally { refreshing = false; }
            RebuildTree();
            RefreshStatus();
        }


        private void RefreshGroupsStrip()
        {
            groupsStrip.SuspendLayout();
            try
            {
                groupsStrip.Items.Clear();

                foreach (var set in viewModel.Sets)
                {
                    var button = new ToolStripButton(set.Name)
                    {
                        Tag = set,
                        CheckOnClick = false,
                        Checked = ReferenceEquals(set, viewModel.SelectedSet),
                        DisplayStyle = ToolStripItemDisplayStyle.Text,
                        AutoSize = true,
                        ToolTipText = set.Name
                    };

                    button.Click += (_, __) => SelectSetFromButton(button);
                    button.MouseDown += GroupButton_MouseDown;
                    button.MouseUp += GroupButton_MouseUp;
                    groupsStrip.Items.Add(button);
                }

                if (groupsStrip.Items.Count > 0)
                {
                    groupsStrip.Items.Add(new ToolStripSeparator());
                }

                var addButton = new ToolStripButton("+")
                {
                    DisplayStyle = ToolStripItemDisplayStyle.Text,
                    ToolTipText = "Создать группу"
                };
                addButton.Click += (_, __) => Execute(viewModel.AddSetCommand, null);
                groupsStrip.Items.Add(addButton);
            }
            finally
            {
                groupsStrip.ResumeLayout();
            }
        }

        private void SelectSetFromButton(ToolStripButton button)
        {
            if (refreshing)
            {
                return;
            }

            if (!(button.Tag is DocumentSet set))
            {
                return;
            }

            refreshing = true;
            try
            {
                viewModel.SelectedSet = set;
                setsCombo.SelectedItem = set;

                UpdateGroupButtonsChecked();
            }
            finally
            {
                refreshing = false;
            }

            RebuildTree();
            RefreshStatus();
        }

        private void GroupButton_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && e.Clicks == 2 && sender is ToolStripButton button)
            {
                BeginRenameGroup(button);
            }
        }

        private void GroupsStrip_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode != Keys.F2)
            {
                return;
            }

            var button = groupsStrip.Items
                .OfType<ToolStripButton>()
                .FirstOrDefault(x => x.Tag is DocumentSet set && ReferenceEquals(set, viewModel.SelectedSet));

            if (button != null)
            {
                BeginRenameGroup(button);
                e.Handled = true;
            }
        }

        private void BeginRenameGroup(ToolStripButton button)
        {
            if (!(button.Tag is DocumentSet set))
            {
                return;
            }

            EndRenameGroup(commit: true);
            SelectSetFromButton(button);

            var index = groupsStrip.Items.IndexOf(button);
            if (index < 0)
            {
                return;
            }

            var editor = new TextBox
            {
                Text = set.Name,
                BorderStyle = BorderStyle.FixedSingle,
                Font = groupsStrip.Font,
                Margin = Padding.Empty
            };

            var textWidth = TextRenderer.MeasureText(editor.Text + "WW", editor.Font).Width;
            var host = new ToolStripControlHost(editor)
            {
                AutoSize = false,
                Width = Math.Max(button.Width + 20, Math.Max(90, textWidth)),
                Height = Math.Max(groupsStrip.Height - 4, editor.PreferredHeight + 2),
                Margin = button.Margin,
                Padding = Padding.Empty,
                Tag = set
            };

            editingGroupHost = host;
            editingGroupSet = set;
            cancelGroupRename = false;

            groupsStrip.Items.RemoveAt(index);
            groupsStrip.Items.Insert(index, host);

            editor.KeyDown += GroupRenameEditor_KeyDown;
            editor.LostFocus += GroupRenameEditor_LostFocus;
            editor.SelectAll();
            editor.Focus();
        }

        private void GroupRenameEditor_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                EndRenameGroup(commit: true);
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Escape)
            {
                cancelGroupRename = true;
                EndRenameGroup(commit: false);
                e.SuppressKeyPress = true;
            }
        }

        private void GroupRenameEditor_LostFocus(object sender, EventArgs e)
        {
            EndRenameGroup(commit: !cancelGroupRename);
        }

        private void EndRenameGroup(bool commit)
        {
            var host = editingGroupHost;
            var set = editingGroupSet;
            if (host == null)
            {
                return;
            }

            editingGroupHost = null;
            editingGroupSet = null;

            var editor = host.Control as TextBox;
            var newName = editor?.Text;
            var index = groupsStrip.Items.IndexOf(host);

            if (commit && set != null)
            {
                if (!viewModel.TryRenameSet(set, newName, showErrors: true))
                {
                    // Invalid or duplicate name: keep the old name and leave the UI consistent.
                    cancelGroupRename = false;
                }
            }

            if (index >= 0)
            {
                groupsStrip.Items.RemoveAt(index);
            }

            RefreshAll();
            cancelGroupRename = false;
        }

        private void RebuildTree()
        {
            tree.BeginUpdate();
            try
            {
                treeModel.Nodes.Clear();
                if (viewModel.CurrentNodes != null)
                {
                    foreach (var item in viewModel.CurrentNodes)
                        treeModel.Nodes.Add(new BookmarkTreeNode(item));
                }
                tree.ExpandAll();
            }
            finally { tree.EndUpdate(); }
            SyncSelectionFromViewModel();
        }

        private void RefreshStatus() => statusLabel.Text = viewModel.StorageText ?? string.Empty;

        private void SyncSelectionFromTree()
        {
            if (refreshing) return;
            var items = tree.SelectedNodes.Select(n => (n.Tag as BookmarkTreeNode)?.Item).Where(x => x != null).ToList();
            viewModel.SetSelectedNodes(items);
        }

        private void SyncSelectionFromViewModel()
        {
            tree.ClearSelection();
            var selected = new HashSet<DocumentItem>(viewModel.SelectedNodes);
            foreach (var node in EnumerateTreeNodes(tree.Root))
            {
                if ((node.Tag as BookmarkTreeNode)?.Item is DocumentItem item && selected.Contains(item))
                    node.IsSelected = true;
            }
        }

        private IEnumerable<TreeNodeAdv> EnumerateTreeNodes(TreeNodeAdv root)
        {
            foreach (TreeNodeAdv child in root.Children)
            {
                yield return child;
                foreach (var nested in EnumerateTreeNodes(child)) yield return nested;
            }
        }

        private DocumentItem GetCurrentItem()
        {
            var current = tree.CurrentNode?.Tag as BookmarkTreeNode;
            return current?.Item ?? viewModel.SelectedNode;
        }

        private void Execute(WpfCommand command, object parameter)
        {
            if (command == null || !command.CanExecute(parameter)) return;
            command.Execute(parameter);
            RebuildTree();
            RefreshAll();
        }

        private void Tree_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            if (tree.UseColumns && e.Y <= tree.ColumnHeaderHeight)
            {
                ShowHeaderMenu(e.Location);
                return;
            }

            var node = tree.GetNodeAt(e.Location);
            if (node != null && !node.IsSelected)
            {
                tree.ClearSelection();
                node.IsSelected = true;
                tree.SelectedNode = node;
                //tree.CurrentNode = node;
                SyncSelectionFromTree();
            }

            nodeMenu.Show(tree, e.Location);
        }

        private async void Tree_DragDrop(object sender, DragEventArgs e)
        {
            var target = (tree.DropPosition.Node?.Tag as BookmarkTreeNode)?.Item;
            var position = ConvertDropPosition(tree.DropPosition.Position);
            if (viewModel.CanMoveSelectedNodesTo(target, position))
                await viewModel.MoveSelectedNodesToAsync(target, position);
            RebuildTree();
            RefreshStatus();
        }

        private void Tree_DragOver(object sender, DragEventArgs e)
        {
            var target = (tree.DropPosition.Node?.Tag as BookmarkTreeNode)?.Item;
            var position = ConvertDropPosition(tree.DropPosition.Position);
            e.Effect = viewModel.CanMoveSelectedNodesTo(target, position) ? DragDropEffects.Move : DragDropEffects.None;
        }

        private static DocSets.DropPosition ConvertDropPosition(NodePosition position)
        {
            switch (position)
            {
                case NodePosition.Before: return DocSets.DropPosition.Before;
                case NodePosition.After: return DocSets.DropPosition.After;
                default: return DocSets.DropPosition.Inside;
            }
        }

        private void Tree_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.Insert)) { Execute(viewModel.CopySelectedNodesCommand, GetCurrentItem()); e.Handled = true; }
            else if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert)) { Execute(viewModel.PasteNodesCommand, GetCurrentItem()); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { Execute(viewModel.DeleteNodeCommand, GetCurrentItem()); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Enter) { ShowBookmarkProperties(GetCurrentItem()); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Execute(viewModel.OpenBookmarkCommand, GetCurrentItem()); e.Handled = true; }
        }


        private sealed class BookmarkToolTipProvider : IToolTipProvider
        {
            public string GetToolTip(TreeNodeAdv node, NodeControl nodeControl)
            {
                var item = (node?.Tag as BookmarkTreeNode)?.Item;
                if (item == null)
                {
                    return string.Empty;
                }

                if (!string.IsNullOrWhiteSpace(item.Comment))
                {
                    return item.Comment;
                }

                return item.IsFolder ? item.Name : item.Display;
            }
        }

        private sealed class ColumnSpec
        {
            public ColumnSpec(string key, string header, int defaultWidth)
            {
                Key = key;
                Header = header;
                DefaultWidth = defaultWidth;
            }

            public string Key { get; }
            public string Header { get; }
            public int DefaultWidth { get; }
        }

        private readonly Dictionary<float, Font> boldFonts = new Dictionary<float, Font>();

        private void NameTextBox_DrawText(object sender, DrawTextEventArgs e)
        {
            var item = e.Node?.Tag as BookmarkTreeNode;
            if (item == null)
            {
                return;
            }

            if (item.Item.IsFolder)
            {
                e.Font = GetBoldFont(e.Font);
            }
        }

        private Font GetBoldFont(Font baseFont)
        {
            var key = baseFont.Size;

            if (!boldFonts.TryGetValue(key, out var font))
            {
                font = new Font(
                    baseFont.FontFamily,
                    baseFont.Size,
                    baseFont.Style | FontStyle.Bold,
                    baseFont.Unit,
                    baseFont.GdiCharSet,
                    baseFont.GdiVerticalFont);

                boldFonts[key] = font;
            }

            return font;
        }
    }
}
