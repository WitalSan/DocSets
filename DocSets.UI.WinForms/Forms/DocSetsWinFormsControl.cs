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
        private readonly DocSetsViewModel _viewModel;
        private readonly ComboBox _setsCombo = new ComboBox();
        private readonly ToolStrip _groupsStrip = new ToolStrip();
        private readonly ToolStrip _toolStrip = new ToolStrip();
        private readonly ToolStripButton _classicActivationButton = new ToolStripButton("Classic");
        private readonly ToolStripButton _clickOpenActivationButton = new ToolStripButton("Click→Open");
        private readonly ToolStripButton _findPreviousButton = new ToolStripButton("<");
        private readonly ToolStripButton _findButton = new ToolStripButton("Найти");
        private readonly ToolStripLabel _findCounterLabel = new ToolStripLabel("0:0");
        private readonly ToolStripButton _findNextButton = new ToolStripButton(">");
        private readonly TreeViewAdv _tree = new TreeViewAdv();
        private readonly TreeModel _treeModel = new TreeModel();
        private readonly Label _statusLabel = new Label();
        private readonly ContextMenuStrip _nodeMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _headerMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _groupMenu = new ContextMenuStrip();
        private ToolStripMenuItem _addSolutionFolderMenuItem;
        private ToolStripMenuItem _addProjectFolderMenuItem;
        private ToolStripMenuItem _addFileFolderMenuItem;
        private ToolStripMenuItem _addClassFolderMenuItem;
        private readonly Dictionary<string, TreeColumn> _columnsByKey = new Dictionary<string, TreeColumn>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TreeColumn, string> _columnKeys = new Dictionary<TreeColumn, string>();
        private bool _refreshing;
        private bool _suppressColumnSave;
        private ToolStripControlHost _editingGroupHost;
        private DocumentSet _editingGroupSet;
        private bool _cancelGroupRename;
        private TreeActivationMode _treeActivationMode = TreeActivationMode.ClassicDoubleClickOpen;
        private readonly List<DocumentItem> _findResults = new List<DocumentItem>();
        private int _findIndex = -1;
        private bool _selectingFromFind;

        public DocSetsWinFormsControl(DocSetsViewModel viewModel)
        {
            this._viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            Dock = DockStyle.Fill;
            BuildLayout();
            BuildTree();
            BuildMenus();
            WireEvents();
            RefreshAll();
        }

        public async System.Threading.Tasks.Task AddBookmarkFromEditorAsync()
        {
            var bookmark = await _viewModel.CreateBookmarkFromActiveDocumentAsync(showErrors: true);
            if (bookmark == null)
            {
                return;
            }

            var initialParent = _viewModel.SelectedNode?.NodeType == NodeType.Folder ? _viewModel.SelectedNode : null;

            using (var dialog = new BookmarkPropertiesDialog(
                bookmark,
                _viewModel.Sets,
                _viewModel.SelectedSet,
                initialParent))
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                dialog.ApplyTo(bookmark);
                await _viewModel.AddPreparedBookmarkAsync(bookmark, dialog.SelectedSet, dialog.SelectedParent);
            }

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
            _setsCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _setsCombo.Width = 220;
            top.Controls.Add(_setsCombo);
            root.Controls.Add(top, 0, 0);

            _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            //AddButton("+Группа", _viewModel.AddSetCommand);
            //AddButton("Переим.", _viewModel.RenameSetCommand);
            //AddButton("-Группа", _viewModel.DeleteSetCommand);
            //_toolStrip.Items.Add(new ToolStripSeparator());
            //AddButton("+Папка", _viewModel.AddRootFolderCommand);
            //AddButton("+Вложенная", _viewModel.AddChildFolderCommand);
            AddButton("+Закладка", _viewModel.AddBookmarkCommand);
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddFindButtons();
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddTreeActivationModeButtons();
            //AddButton("Копировать", _viewModel.CopySelectedNodesCommand);
            //AddButton("Вставить", _viewModel.PasteNodesCommand);
            top.Controls.Add(_toolStrip);

            _groupsStrip.Dock = DockStyle.Fill;
            _groupsStrip.GripStyle = ToolStripGripStyle.Hidden;
            _groupsStrip.RenderMode = ToolStripRenderMode.System;
            _groupsStrip.CanOverflow = true;
            _groupsStrip.Stretch = true;
            _groupsStrip.AutoSize = true;
            _groupsStrip.Padding = new Padding(2, 1, 2, 1);
            _groupsStrip.MouseUp += GroupsStrip_MouseUp;
            _groupsStrip.KeyDown += GroupsStrip_KeyDown;
            root.Controls.Add(_groupsStrip, 0, 1);

            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Padding = new Padding(4, 2, 4, 2);
            root.Controls.Add(_tree, 0, 2);
            root.Controls.Add(_statusLabel, 0, 3);
        }

        private static IEnumerable<ColumnSpec> GetDefaultColumnSpecs()
        {
            yield return new ColumnSpec("name", "Название", 340);
            yield return new ColumnSpec("comment", "Комментарий", 240);
            yield return new ColumnSpec("project", "Проект", 160);
            yield return new ColumnSpec("file", "Файл", 280);
            yield return new ColumnSpec("line", "Строка", 70);
            yield return new ColumnSpec("symbol", "Символ", 260);
        }

        private void BuildColumns()
        {
            _columnsByKey.Clear();
            _columnKeys.Clear();
            _tree.Columns.Clear();

            var specs = GetDefaultColumnSpecs().ToList();
            var layout = _viewModel.Ui?.Columns ?? new List<ColumnLayout>();
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

            _suppressColumnSave = true;
            try
            {
                foreach (var spec in orderedSpecs)
                {
                    layoutByKey.TryGetValue(spec.Key, out var saved);
                    var column = new TreeColumn(spec.Header, saved?.Width > 0 ? saved.Width : spec.DefaultWidth)
                    {
                        IsVisible = saved?.IsVisible ?? true
                    };

                    _tree.Columns.Add(column);
                    _columnsByKey[spec.Key] = column;
                    _columnKeys[column] = spec.Key;
                }
            }
            finally
            {
                _suppressColumnSave = false;
            }

            SaveColumnLayout();
        }

        private void SaveColumnLayout()
        {
            if (_suppressColumnSave || _viewModel.Ui == null)
            {
                return;
            }

            var layouts = new List<ColumnLayout>();
            for (var i = 0; i < _tree.Columns.Count; i++)
            {
                var column = _tree.Columns[i];
                if (!_columnKeys.TryGetValue(column, out var key))
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

            _viewModel.Ui.Columns = layouts;
            _ = _viewModel.SaveAsync();
        }

        private void ShowHeaderMenu(Point location)
        {
            _headerMenu.Items.Clear();
            var clickedColumn = GetColumnAt(location);

            foreach (var spec in GetDefaultColumnSpecs())
            {
                if (!_columnsByKey.TryGetValue(spec.Key, out var column))
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
                    if (!menuItem.Checked && _tree.Columns.Cast<TreeColumn>().Count(x => x.IsVisible) <= 1)
                    {
                        menuItem.Checked = true;
                        return;
                    }

                    c.IsVisible = menuItem.Checked;
                    _tree.Update();
                    SaveColumnLayout();
                };

                _headerMenu.Items.Add(item);
            }

            _headerMenu.Items.Add(new ToolStripSeparator());

            var showAll = new ToolStripMenuItem("Показать все колонки");
            showAll.Click += (_, __) =>
            {
                foreach (TreeColumn column in _tree.Columns)
                {
                    column.IsVisible = true;
                }

                _tree.Update();
                SaveColumnLayout();
            };
            _headerMenu.Items.Add(showAll);

            var autoWidth = new ToolStripMenuItem("Автоширина видимых колонок");
            autoWidth.Click += (_, __) =>
            {
                AutoSizeVisibleColumns();
                SaveColumnLayout();
            };
            _headerMenu.Items.Add(autoWidth);

            if (clickedColumn != null)
            {
                _headerMenu.Items.Add(new ToolStripSeparator());

                var left = new ToolStripMenuItem("Сдвинуть колонку влево");
                left.Enabled = _tree.Columns.IndexOf(clickedColumn) > 0;
                left.Click += (_, __) => MoveColumn(clickedColumn, -1);
                _headerMenu.Items.Add(left);

                var right = new ToolStripMenuItem("Сдвинуть колонку вправо");
                right.Enabled = _tree.Columns.IndexOf(clickedColumn) >= 0 && _tree.Columns.IndexOf(clickedColumn) < _tree.Columns.Count - 1;
                right.Click += (_, __) => MoveColumn(clickedColumn, 1);
                _headerMenu.Items.Add(right);
            }

            _headerMenu.Show(_tree, location);
        }


        private TreeColumn GetColumnAt(Point location)
        {
            if (location.Y > _tree.ColumnHeaderHeight)
            {
                return null;
            }

            var x = -_tree.OffsetX;
            foreach (TreeColumn column in _tree.Columns)
            {
                if (!column.IsVisible)
                {
                    continue;
                }

                var rect = new Rectangle(x, 0, column.Width, _tree.ColumnHeaderHeight);
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
            var index = _tree.Columns.IndexOf(column);
            var newIndex = index + delta;
            if (index < 0 || newIndex < 0 || newIndex >= _tree.Columns.Count)
            {
                return;
            }

            _suppressColumnSave = true;
            try
            {
                _tree.Columns.Remove(column);
                _tree.Columns.Insert(newIndex, column);
            }
            finally
            {
                _suppressColumnSave = false;
            }

            _tree.Update();
            SaveColumnLayout();
        }

        private void AutoSizeVisibleColumns()
        {
            foreach (TreeColumn column in _tree.Columns)
            {
                if (!column.IsVisible || !_columnKeys.TryGetValue(column, out var key))
                {
                    continue;
                }

                var headerWidth = TextRenderer.MeasureText(column.Header, _tree.Font).Width + 24;
                var valueWidth = EnumerateItems(_viewModel.CurrentNodes)
                    .Select(x => GetColumnText(x, key))
                    .Where(x => !string.IsNullOrEmpty(x))
                    .Select(x => TextRenderer.MeasureText(x, _tree.Font).Width + 24)
                    .DefaultIfEmpty(0)
                    .Max();

                column.Width = Math.Min(Math.Max(headerWidth, valueWidth), 700);
            }

            _tree.Update();
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
                case "line": return item == null || item.NodeType == NodeType.Folder ? string.Empty : item.Line.ToString();
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
            _toolStrip.Items.Add(button);
        }

        private void AddTreeActivationModeButtons()
        {
            return;
            _toolStrip.Items.Add(new ToolStripLabel("Клик:"));

            SetupActivationModeButton(
                _classicActivationButton,
                "Classic",
                "Старое поведение: одинарный клик выбирает, двойной клик открывает закладку",
                TreeActivationMode.ClassicDoubleClickOpen);

            SetupActivationModeButton(
                _clickOpenActivationButton,
                "Click→Open",
                "Новое поведение: одинарный клик открывает закладку, двойной клик открывает свойства",
                TreeActivationMode.ClickOpenDoubleClickProperties);

            _toolStrip.Items.Add(_classicActivationButton);
            _toolStrip.Items.Add(_clickOpenActivationButton);
            UpdateActivationModeButtons();
        }

        private void SetupActivationModeButton(ToolStripButton button, string text, string tooltip, TreeActivationMode mode)
        {
            button.Text = text;
            button.ToolTipText = tooltip;
            button.DisplayStyle = ToolStripItemDisplayStyle.Text;
            button.CheckOnClick = false;
            button.Tag = mode;
            button.Click += (_, __) => SetTreeActivationMode(mode);
        }

        private void UpdateActivationModeButtons()
        {
            _classicActivationButton.Checked = _treeActivationMode == TreeActivationMode.ClassicDoubleClickOpen;
            _clickOpenActivationButton.Checked = _treeActivationMode == TreeActivationMode.ClickOpenDoubleClickProperties;
        }

        private void AddFindButtons()
        {
            _findPreviousButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _findPreviousButton.ToolTipText = "Предыдущая найденная закладка";
            _findPreviousButton.Click += (_, __) => MoveFindSelection(-1);

            _findButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _findButton.ToolTipText = "Найти закладку в текущем Set по активному документу";
            _findButton.Click += async (_, __) => await FindBookmarksInCurrentSetAsync();

            _findCounterLabel.AutoSize = true;
            _findCounterLabel.ToolTipText = "Текущий результат: всего найдено";

            _findNextButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _findNextButton.ToolTipText = "Следующая найденная закладка";
            _findNextButton.Click += (_, __) => MoveFindSelection(1);

            _toolStrip.Items.Add(_findPreviousButton);
            _toolStrip.Items.Add(_findButton);
            _toolStrip.Items.Add(_findCounterLabel);
            _toolStrip.Items.Add(_findNextButton);
            UpdateFindCounter();
        }

        private void BuildTree()
        {
            _tree.Font = new Font("Segoe UI", 10f);
            _tree.AutoRowHeight = true;
            //_tree.RowHeight = 22;
            _tree.AutoHeaderHeight = true;

            _tree.Dock = DockStyle.Fill;
            _tree.Model = _treeModel;
            _tree.UseColumns = true;
            _tree.FullRowSelect = true;
            _tree.FullRowSelectActiveColor = SystemColors.Highlight;
            _tree.FullRowSelectInactiveColor = Color.FromArgb(255, 226, 120);

            _tree.GridLineStyle = GridLineStyle.Horizontal;
            _tree.SelectionMode = TreeSelectionMode.Multi;
            _tree.HideSelection = false;
            _tree.AllowDrop = true;
            _tree.AllowColumnReorder = true;
            _tree.HighlightDropPosition = true;
            _tree.ShowNodeToolTips = true;
            _tree.DefaultToolTipProvider = new BookmarkToolTipProvider();
            _tree.DragDropMarkColor = Color.DodgerBlue;
            _tree.DragDropMarkWidth = 2;
            _tree.TopEdgeSensivity = 0.25f;
            _tree.BottomEdgeSensivity = 0.25f;

            BuildColumns();

            _tree.NodeControls.Add(new NodeStateIcon { LeftMargin = 1 });
            _tree.NodeControls.Add(new ExpandingIcon { LeftMargin = 1 });
            _tree.NodeControls.Add(new NodeIcon
            {
                DataPropertyName = nameof(BookmarkTreeNode.Image),
                ParentColumn = _columnsByKey["name"],
                LeftMargin = 2
            });
            var nameNode = new OverflowNodeTextBox
            {
                DataPropertyName = nameof(BookmarkTreeNode.Name),
                ParentColumn = _columnsByKey["name"],
                EditEnabled = true,
                IncrementalSearchEnabled = true,
                LeftMargin = 3,
                ColumnTextResolver = GetColumnTextForOverflow
            };
            nameNode.DrawText += NameTextBox_DrawText;

            _tree.NodeControls.Add(nameNode);
            _tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.File), ParentColumn = _columnsByKey["file"] });
            _tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Line), ParentColumn = _columnsByKey["line"] });
            _tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Comment), ParentColumn = _columnsByKey["comment"] });
            _tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Project), ParentColumn = _columnsByKey["project"] });
            _tree.NodeControls.Add(new NodeTextBox { DataPropertyName = nameof(BookmarkTreeNode.Symbol), ParentColumn = _columnsByKey["symbol"] });
        }
        private string GetColumnTextForOverflow(TreeColumn column, TreeNodeAdv node)
        {
            if (column == null || node == null || !_columnKeys.TryGetValue(column, out var key))
            {
                return string.Empty;
            }

            var item = (node.Tag as BookmarkTreeNode)?.Item;
            return GetColumnText(item, key);
        }

        private void BuildMenus()
        {
            AddMenu("Open", _viewModel.OpenBookmarkCommand);
            //AddMenu("Обновить", _viewModel.UpdateBookmarkCommand);
            //AddMenu("Переименовать", _viewModel.RenameNodeCommand);
            //AddMenu("Удалить", _viewModel.DeleteNodeCommand);
            //_nodeMenu.Items.Add(new ToolStripSeparator());
            AddMenu("Add BookMark", _viewModel.AddBookmarkCommand);
            AddContextFolderMenus();
            _nodeMenu.Items.Add(new ToolStripSeparator());
            AddMenu("Copy", _viewModel.CopySelectedNodesCommand, "Ctrl+C");
            AddMenu("Paste", _viewModel.PasteNodesCommand, "Ctrl+V");
            //AddJsonMenu();
            AddMenu("Copy JSON", _viewModel.CopySelectedNodesAsJsonCommand);
            AddMenu("Del", _viewModel.DeleteNodeCommand);
            _nodeMenu.Items.Add(new ToolStripSeparator());
            AddPropertiesMenu();

            BuildGroupMenu();
        }

        private void BuildGroupMenu()
        {
            AddGroupMenu("Add Set", _viewModel.AddSetCommand);
            //_groupMenu.Items.Add(new ToolStripSeparator());
            AddRenameGroupMenu();
            AddGroupMenu("Del Set", _viewModel.DeleteSetCommand);
            _groupMenu.Items.Add(new ToolStripSeparator());
            AddGroupMenu("Move Left", _viewModel.MoveSetUpCommand);
            AddGroupMenu("Move Right", _viewModel.MoveSetDownCommand);

            _groupMenu.Opening += (_, e) =>
            {
                SelectGroupMenuTarget();

                foreach (ToolStripItem item in _groupMenu.Items)
                {
                    if (item is ToolStripMenuItem menuItem)
                    {
                        if (string.Equals(menuItem.Name, "RenameGroupMenuItem", StringComparison.Ordinal))
                        {
                            menuItem.Enabled = _viewModel.SelectedSet != null;
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
            var item = new ToolStripMenuItem("Rename")
            {
                Name = "RenameGroupMenuItem"
            };

            item.Click += (_, __) =>
            {
                SelectGroupMenuTarget();

                var button = FindGroupButton(_viewModel.SelectedSet);
                if (button != null)
                {
                    BeginRenameGroup(button);
                }
            };

            _groupMenu.Items.Add(item);
        }


        private void AddPropertiesMenu()
        {
            var item = new ToolStripMenuItem("Properties...");
            item.Click += delegate { ShowBookmarkProperties(GetCurrentItem()); };
            _nodeMenu.Items.Add(item);
        }

        private void AddContextFolderMenus()
        {
            AddMenu("Add Folder", _viewModel.AddFolderCommand);
            _addSolutionFolderMenuItem = AddContextFolderMenu("Add Folder <Solution>", ActiveDocumentFolderKind.Solution);
            _addProjectFolderMenuItem = AddContextFolderMenu("Add Folder <Project>", ActiveDocumentFolderKind.Project);
            _addFileFolderMenuItem = AddContextFolderMenu("Add Folder <File>", ActiveDocumentFolderKind.File);
            _addClassFolderMenuItem = AddContextFolderMenu("Add Folder <Class>", ActiveDocumentFolderKind.Class);
        }

        private ToolStripMenuItem AddContextFolderMenu(string text, ActiveDocumentFolderKind kind)
        {
            var item = new ToolStripMenuItem(text)
            {
                Tag = kind
            };

            item.Click += async (_, __) =>
            {
                await _viewModel.CreateFolderFromActiveDocumentAsync(kind, GetCurrentItem());
                RebuildTree();
                RefreshStatus();
            };

            _nodeMenu.Items.Add(item);
            return item;
        }

        private async void RefreshContextFolderMenuTexts()
        {
            if (_addSolutionFolderMenuItem == null || _addProjectFolderMenuItem == null || _addFileFolderMenuItem == null || _addClassFolderMenuItem == null)
            {
                return;
            }

            _addSolutionFolderMenuItem.Text = "Add Folder <Solution>";
            _addProjectFolderMenuItem.Text = "Add Folder <Project>";
            _addFileFolderMenuItem.Text = "Add Folder <File>";
            _addClassFolderMenuItem.Text = "Add Folder <Class>";

            var context = await _viewModel.GetActiveDocumentContextAsync();
            if (context == null)
            {
                _addSolutionFolderMenuItem.Enabled = false;
                _addProjectFolderMenuItem.Enabled = false;
                _addFileFolderMenuItem.Enabled = false;
                _addClassFolderMenuItem.Enabled = false;
                return;
            }

            ApplyContextFolderMenuText(_addSolutionFolderMenuItem, context.SolutionName, "Solution");
            ApplyContextFolderMenuText(_addProjectFolderMenuItem, context.ProjectName, "Project");
            ApplyContextFolderMenuText(_addFileFolderMenuItem, context.FileName, "File");
            ApplyContextFolderMenuText(_addClassFolderMenuItem, context.ClassName, "Class");
        }

        private static void ApplyContextFolderMenuText(ToolStripMenuItem item, string name, string fallback)
        {
            var hasName = !string.IsNullOrWhiteSpace(name);
            item.Text = $"Add Folder <{(hasName ? name.Trim() : fallback)}>";
            item.Enabled = hasName;
        }

        private void AddJsonMenu()
        {
            var jsonMenu = new ToolStripMenuItem("Json");
            AddSubMenu(jsonMenu, "Copy", _viewModel.CopySelectedNodesAsJsonCommand);
            AddSubMenu(jsonMenu, "Paste", _viewModel.PasteNodesFromJsonCommand);
            _nodeMenu.Items.Add(jsonMenu);
        }

        private void AddSubMenu(ToolStripMenuItem parent, string text, WpfCommand command)
        {
            var item = new ToolStripMenuItem(text)
            {
                Tag = command
            };

            item.Click += (_, __) => Execute(command, GetCurrentItem());
            parent.DropDownItems.Add(item);
        }

        private async void ShowBookmarkProperties(DocumentItem item)
        {
            if (item == null)
            {
                return;
            }

            var itemSet = _viewModel.GetSetContainingNode(item) ?? _viewModel.SelectedSet;
            var itemParent = _viewModel.GetParentFolder(item);

            using (var dialog = new BookmarkPropertiesDialog(
                item,
                _viewModel.Sets,
                itemSet,
                itemParent,
                item))
            {
                var result = dialog.ShowDialog(this);
                if (result != DialogResult.OK)
                {
                    return;
                }

                dialog.ApplyTo(item);
                await _viewModel.MoveExistingNodeAsync(item, dialog.SelectedSet, dialog.SelectedParent);
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

            _groupMenu.Items.Add(item);
        }


        private ToolStripButton FindGroupButton(DocumentSet set)
        {
            if (set == null)
            {
                return null;
            }

            return _groupsStrip.Items
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

            _groupMenu.Tag = button.Tag as DocumentSet;
            SelectSetFromButton(button);
            _groupMenu.Show(Cursor.Position);
        }

        private void GroupsStrip_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            var item = _groupsStrip.GetItemAt(e.Location) as ToolStripButton;
            if (item != null && item.Tag is DocumentSet)
            {
                return;
            }

            _groupMenu.Tag = null;
            _groupMenu.Show(_groupsStrip, e.Location);
        }

        private void SelectGroupMenuTarget()
        {
            if (_groupMenu.Tag is DocumentSet set && !ReferenceEquals(_viewModel.SelectedSet, set))
            {
                _viewModel.SelectedSet = set;
                _setsCombo.SelectedItem = set;
                UpdateGroupButtonsChecked();
                RebuildTree();
                RefreshStatus();
            }
        }

        private void UpdateGroupButtonsChecked()
        {
            foreach (ToolStripItem item in _groupsStrip.Items)
            {
                if (item is ToolStripButton groupButton && groupButton.Tag is DocumentSet group)
                {
                    groupButton.Checked = ReferenceEquals(group, _viewModel.SelectedSet);
                }
            }
        }

        private void AddMenu(string text, WpfCommand command, string shortcut = null)
        {
            var item = new ToolStripMenuItem(text);
            if (!string.IsNullOrEmpty(shortcut)) item.ShortcutKeyDisplayString = shortcut;
            item.Click += (_, __) => Execute(command, GetCurrentItem());
            _nodeMenu.Items.Add(item);
        }

        private void WireEvents()
        {
            _setsCombo.SelectedIndexChanged += (_, __) =>
            {
                if (_refreshing) return;
                _viewModel.SelectedSet = _setsCombo.SelectedItem as DocumentSet;
                ClearFindResults();
                RefreshGroupsStrip();
                RebuildTree();
                RefreshStatus();
            };

            _tree.SelectionChanged += (_, __) =>
            {
                if (!_selectingFromFind)
                {
                    SyncSelectionFromTree();
                }
            };
            WireTreeActivationBehavior();
            _tree.ItemDrag += (_, __) => _tree.DoDragDropSelectedNodes(DragDropEffects.Move);
            _tree.DragOver += Tree_DragOver;
            _tree.DragDrop += Tree_DragDrop;
            _tree.KeyDown += Tree_KeyDown;
            _tree.MouseUp += Tree_MouseUp;
            _tree.ColumnWidthChanged += (_, __) => SaveColumnLayout();
            _tree.ColumnReordered += (_, __) => SaveColumnLayout();
            _nodeMenu.Opening += (_, e) =>
            {
                SyncSelectionFromTree();
                RefreshContextFolderMenuTexts();
                var current = GetCurrentItem();
                UpdateNodeMenuEnabled(_nodeMenu.Items, current);
            };
        }

        private void UpdateNodeMenuEnabled(ToolStripItemCollection items, DocumentItem current)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is ToolStripMenuItem menuItem)
                {
                    var command = menuItem.Tag as WpfCommand ?? GetCommandByText(menuItem.Text);
                    menuItem.Enabled = command?.CanExecute(current) ?? true;

                    if (menuItem.DropDownItems.Count > 0)
                    {
                        UpdateNodeMenuEnabled(menuItem.DropDownItems, current);
                        menuItem.Enabled = menuItem.DropDownItems.OfType<ToolStripItem>().Any(x => x.Enabled);
                    }
                }
            }
        }

        private WpfCommand GetCommandByText(string text)
        {
            switch (text)
            {
                case "Открыть": return _viewModel.OpenBookmarkCommand;
                case "Обновить": return _viewModel.UpdateBookmarkCommand;
                case "Переименовать": return _viewModel.RenameNodeCommand;
                case "Добавить вложенную папку": return _viewModel.AddChildFolderCommand;
                case "Добавить закладку сюда": return _viewModel.AddBookmarkCommand;
                case "Копировать": return _viewModel.CopySelectedNodesCommand;
                case "Вставить": return _viewModel.PasteNodesCommand;
                case "Copy": return _viewModel.CopySelectedNodesAsJsonCommand;
                case "Paste": return _viewModel.PasteNodesFromJsonCommand;
                case "Удалить": return _viewModel.DeleteNodeCommand;
                default: return null;
            }
        }

        public void RefreshAll()
        {
            _refreshing = true;
            try
            {
                _setsCombo.Items.Clear();
                foreach (var set in _viewModel.Sets)
                {
                    _setsCombo.Items.Add(set);
                }

                _setsCombo.SelectedItem = _viewModel.SelectedSet;
                RefreshGroupsStrip();
            }
            finally { _refreshing = false; }
            RebuildTree();
            RefreshStatus();
        }


        private void RefreshGroupsStrip()
        {
            _groupsStrip.SuspendLayout();
            try
            {
                _groupsStrip.Items.Clear();

                foreach (var set in _viewModel.Sets)
                {
                    var button = new ToolStripButton(set.Name)
                    {
                        Tag = set,
                        CheckOnClick = false,
                        Checked = ReferenceEquals(set, _viewModel.SelectedSet),
                        DisplayStyle = ToolStripItemDisplayStyle.Text,
                        AutoSize = true,
                        ToolTipText = set.Name
                    };

                    button.Click += (_, __) => SelectSetFromButton(button);
                    button.MouseDown += GroupButton_MouseDown;
                    button.MouseUp += GroupButton_MouseUp;
                    _groupsStrip.Items.Add(button);
                }

                if (_groupsStrip.Items.Count > 0)
                {
                    _groupsStrip.Items.Add(new ToolStripSeparator());
                }

                var addButton = new ToolStripButton("+")
                {
                    DisplayStyle = ToolStripItemDisplayStyle.Text,
                    ToolTipText = "Создать группу"
                };
                addButton.Click += (_, __) => Execute(_viewModel.AddSetCommand, null);
                _groupsStrip.Items.Add(addButton);
            }
            finally
            {
                _groupsStrip.ResumeLayout();
            }
        }

        private void SelectSetFromButton(ToolStripButton button)
        {
            if (_refreshing)
            {
                return;
            }

            if (!(button.Tag is DocumentSet set))
            {
                return;
            }

            _refreshing = true;
            try
            {
                _viewModel.SelectedSet = set;
                _setsCombo.SelectedItem = set;
                ClearFindResults();

                UpdateGroupButtonsChecked();
            }
            finally
            {
                _refreshing = false;
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

            var button = _groupsStrip.Items
                .OfType<ToolStripButton>()
                .FirstOrDefault(x => x.Tag is DocumentSet set && ReferenceEquals(set, _viewModel.SelectedSet));

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

            var index = _groupsStrip.Items.IndexOf(button);
            if (index < 0)
            {
                return;
            }

            var editor = new TextBox
            {
                Text = set.Name,
                BorderStyle = BorderStyle.FixedSingle,
                Font = _groupsStrip.Font,
                Margin = Padding.Empty
            };

            var textWidth = TextRenderer.MeasureText(editor.Text + "WW", editor.Font).Width;
            var host = new ToolStripControlHost(editor)
            {
                AutoSize = false,
                Width = Math.Max(button.Width + 20, Math.Max(90, textWidth)),
                Height = Math.Max(_groupsStrip.Height - 4, editor.PreferredHeight + 2),
                Margin = button.Margin,
                Padding = Padding.Empty,
                Tag = set
            };

            _editingGroupHost = host;
            _editingGroupSet = set;
            _cancelGroupRename = false;

            _groupsStrip.Items.RemoveAt(index);
            _groupsStrip.Items.Insert(index, host);

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
                _cancelGroupRename = true;
                EndRenameGroup(commit: false);
                e.SuppressKeyPress = true;
            }
        }

        private void GroupRenameEditor_LostFocus(object sender, EventArgs e)
        {
            EndRenameGroup(commit: !_cancelGroupRename);
        }

        private void EndRenameGroup(bool commit)
        {
            var host = _editingGroupHost;
            var set = _editingGroupSet;
            if (host == null)
            {
                return;
            }

            _editingGroupHost = null;
            _editingGroupSet = null;

            var editor = host.Control as TextBox;
            var newName = editor?.Text;
            var index = _groupsStrip.Items.IndexOf(host);

            if (commit && set != null)
            {
                if (!_viewModel.TryRenameSet(set, newName, showErrors: true))
                {
                    // Invalid or duplicate name: keep the old name and leave the UI consistent.
                    _cancelGroupRename = false;
                }
            }

            if (index >= 0)
            {
                _groupsStrip.Items.RemoveAt(index);
            }

            RefreshAll();
            _cancelGroupRename = false;
        }

        private void RebuildTree()
        {
            _tree.BeginUpdate();
            try
            {
                _treeModel.Nodes.Clear();
                if (_viewModel.CurrentNodes != null)
                {
                    foreach (var item in _viewModel.CurrentNodes)
                        _treeModel.Nodes.Add(new BookmarkTreeNode(item));
                }
                _tree.ExpandAll();
            }
            finally { _tree.EndUpdate(); }
            SyncSelectionFromViewModel();
        }

        private async System.Threading.Tasks.Task FindBookmarksInCurrentSetAsync()
        {
            _findResults.Clear();
            _findIndex = -1;

            var probe = await _viewModel.CreateBookmarkFromActiveDocumentAsync(showErrors: false);
            if (probe != null && _viewModel.CurrentNodes != null)
            {
                _findResults.AddRange(FindMatchesInCurrentSet(probe));
            }

            if (_findResults.Count > 0)
            {
                _findIndex = 0;
                SelectFindResult();
            }
            else
            {
                _viewModel.SetSelectedNodes(Enumerable.Empty<DocumentItem>());
                SyncSelectionFromViewModel();
            }

            UpdateFindCounter();
        }

        private IEnumerable<DocumentItem> FindMatchesInCurrentSet(DocumentItem probe)
        {
            var items = EnumerateItems(_viewModel.CurrentNodes)
                .Where(x => x != null && SamePath(x.Path, probe.Path))
                .ToList();

            if (items.Count == 0)
            {
                return Enumerable.Empty<DocumentItem>();
            }

            var symbolMatches = items
                .Where(x => !string.IsNullOrWhiteSpace(probe.Symbol) && SameText(x.Symbol, probe.Symbol))
                .OrderBy(x => Distance(x.Line, probe.Line))
                .ThenBy(x => x.Name)
                .ToList();

            if (symbolMatches.Count > 0)
            {
                return symbolMatches;
            }

            var fileBookmarks = items
                .Where(x => x.Type == BookmarkType.File)
                .OrderBy(x => x.Name)
                .ToList();

            if (fileBookmarks.Count > 0)
            {
                return fileBookmarks;
            }

            if (false)
            {
                return items
                    .OrderBy(x => Distance(x.Line, probe.Line))
                    .ThenBy(x => x.Name)
                    .ToList();
            }

            return Enumerable.Empty<DocumentItem>();
        }

        private static int Distance(int a, int b)
        {
            return Math.Abs(Math.Max(1, a) - Math.Max(1, b));
        }

        private static bool SameText(string left, string right)
        {
            return string.Equals((left ?? string.Empty).Trim(), (right ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool SamePath(string left, string right)
        {
            var l = NormalizePathForCompare(left);
            var r = NormalizePathForCompare(right);
            return !string.IsNullOrWhiteSpace(l) && string.Equals(l, r, StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePathForCompare(string path)
        {
            return (path ?? string.Empty).Replace('/', '\\').Trim();
        }

        private void MoveFindSelection(int delta)
        {
            if (_findResults.Count == 0)
            {
                UpdateFindCounter();
                return;
            }

            _findIndex = (_findIndex + delta) % _findResults.Count;
            if (_findIndex < 0)
            {
                _findIndex += _findResults.Count;
            }

            SelectFindResult();
            UpdateFindCounter();
        }

        private void SelectFindResult()
        {
            if (_findIndex < 0 || _findIndex >= _findResults.Count)
            {
                return;
            }

            var item = _findResults[_findIndex];
            _viewModel.SetSelectedNodes(new[] { item });
            SyncSelectionFromViewModel();
        }

        private void ClearFindResults()
        {
            _findResults.Clear();
            _findIndex = -1;
            UpdateFindCounter();
        }

        private void UpdateFindCounter()
        {
            var current = _findResults.Count == 0 || _findIndex < 0 ? 0 : _findIndex + 1;
            _findCounterLabel.Text = $"{current}:{_findResults.Count}";
            var enabled = _findResults.Count > 0;
            _findPreviousButton.Enabled = enabled;
            _findNextButton.Enabled = enabled;
        }

        private void RefreshStatus() => _statusLabel.Text = _viewModel.StorageText ?? string.Empty;

        private void SyncSelectionFromTree()
        {
            if (_refreshing) return;
            var items = _tree.SelectedNodes.Select(n => (n.Tag as BookmarkTreeNode)?.Item).Where(x => x != null).ToList();
            _viewModel.SetSelectedNodes(items);
        }

        private void SyncSelectionFromViewModel()
        {
            _selectingFromFind = true;
            try
            {
                _tree.ClearSelection();
                var selected = new HashSet<DocumentItem>(_viewModel.SelectedNodes);
                TreeNodeAdv firstSelected = null;

                foreach (var node in EnumerateTreeNodes(_tree.Root))
                {
                    if ((node.Tag as BookmarkTreeNode)?.Item is DocumentItem item && selected.Contains(item))
                    {
                        node.IsSelected = true;
                        if (firstSelected == null)
                        {
                            firstSelected = node;
                        }
                    }
                }

                if (firstSelected != null)
                {
                    _tree.SelectedNode = firstSelected;
                    _tree.EnsureVisible(firstSelected);
                }

                _tree.Invalidate();
            }
            finally
            {
                _selectingFromFind = false;
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
            var current = _tree.CurrentNode?.Tag as BookmarkTreeNode;
            return current?.Item ?? _viewModel.SelectedNode;
        }

        private void Execute(WpfCommand command, object parameter)
        {
            if (command == null || !command.CanExecute(parameter)) return;
            command.Execute(parameter);
            RebuildTree();
            RefreshAll();
        }

        private void SetTreeActivationMode(TreeActivationMode mode)
        {
            if (_treeActivationMode == mode)
            {
                UpdateActivationModeButtons();
                return;
            }

            UnwireTreeActivationBehavior();
            _treeActivationMode = mode;
            WireTreeActivationBehavior();
            UpdateActivationModeButtons();
        }

        private void WireTreeActivationBehavior()
        {
            UnwireTreeActivationBehavior();

            if (_treeActivationMode == TreeActivationMode.ClickOpenDoubleClickProperties)
            {
                WireClickOpenDoubleClickPropertiesBehavior();
            }
            else
            {
                WireClassicTreeActivationBehavior();
            }

            UpdateActivationModeButtons();
        }

        private void UnwireTreeActivationBehavior()
        {
            _tree.NodeMouseClick -= Tree_NodeMouseClick_OpenBookmark;
            _tree.NodeMouseDoubleClick -= Tree_NodeMouseDoubleClick_OpenBookmark;
            _tree.NodeMouseDoubleClick -= Tree_NodeMouseDoubleClick_ShowProperties;
        }

        private void WireClassicTreeActivationBehavior()
        {
            // Старое поведение: одиночный клик только выбирает, двойной клик открывает закладку.
            _tree.NodeMouseDoubleClick += Tree_NodeMouseDoubleClick_OpenBookmark;
        }

        private void WireClickOpenDoubleClickPropertiesBehavior()
        {
            // Новое поведение без таймера: одиночный клик сразу открывает закладку,
            // двойной клик открывает свойства. При двойном клике первый клик уже успевает открыть файл.
            _tree.NodeMouseClick += Tree_NodeMouseClick_OpenBookmark;
            _tree.NodeMouseDoubleClick += Tree_NodeMouseDoubleClick_ShowProperties;
        }

        private void Tree_NodeMouseClick_OpenBookmark(object sender, TreeNodeAdvMouseEventArgs e)
        {
            var item = GetBookmarkFromMouseEvent(e);
            if (item == null || item.Type == BookmarkType.Empty)
            {
                return;
            }

            Execute(_viewModel.OpenBookmarkCommand, item);
        }

        private void Tree_NodeMouseDoubleClick_OpenBookmark(object sender, TreeNodeAdvMouseEventArgs e)
        {
            var item = GetBookmarkFromMouseEvent(e);
            if (item == null || item.Type == BookmarkType.Empty)
            {
                return;
            }

            Execute(_viewModel.OpenBookmarkCommand, item);
        }

        private void Tree_NodeMouseDoubleClick_ShowProperties(object sender, TreeNodeAdvMouseEventArgs e)
        {
            var item = GetBookmarkFromMouseEvent(e);
            if (item == null)
            {
                return;
            }

            ShowBookmarkProperties(item);
        }

        private static DocumentItem GetBookmarkFromMouseEvent(TreeNodeAdvMouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || e.Node == null)
            {
                return null;
            }

            return (e.Node.Tag as BookmarkTreeNode)?.Item;
        }

        private void Tree_MouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right)
            {
                return;
            }

            if (_tree.UseColumns && e.Y <= _tree.ColumnHeaderHeight)
            {
                ShowHeaderMenu(e.Location);
                return;
            }

            var node = _tree.GetNodeAt(e.Location);
            if (node != null && !node.IsSelected)
            {
                _tree.ClearSelection();
                node.IsSelected = true;
                _tree.SelectedNode = node;
                //_tree.CurrentNode = node;
                SyncSelectionFromTree();
            }

            _nodeMenu.Show(_tree, e.Location);
        }

        private async void Tree_DragDrop(object sender, DragEventArgs e)
        {
            var target = (_tree.DropPosition.Node?.Tag as BookmarkTreeNode)?.Item;
            var position = ConvertDropPosition(_tree.DropPosition.Position);
            if (_viewModel.CanMoveSelectedNodesTo(target, position))
                await _viewModel.MoveSelectedNodesToAsync(target, position);
            RebuildTree();
            RefreshStatus();
        }

        private void Tree_DragOver(object sender, DragEventArgs e)
        {
            var target = (_tree.DropPosition.Node?.Tag as BookmarkTreeNode)?.Item;
            var position = ConvertDropPosition(_tree.DropPosition.Position);
            e.Effect = _viewModel.CanMoveSelectedNodesTo(target, position) ? DragDropEffects.Move : DragDropEffects.None;
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
            if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.Insert)) { Execute(_viewModel.CopySelectedNodesCommand, GetCurrentItem()); e.Handled = true; }
            else if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert)) { Execute(_viewModel.PasteNodesCommand, GetCurrentItem()); e.Handled = true; }
            else if (e.KeyCode == Keys.Delete) { Execute(_viewModel.DeleteNodeCommand, GetCurrentItem()); e.Handled = true; }
            else if (e.Alt && e.KeyCode == Keys.Enter) { ShowBookmarkProperties(GetCurrentItem()); e.Handled = true; }
            else if (e.KeyCode == Keys.Enter) { Execute(_viewModel.OpenBookmarkCommand, GetCurrentItem()); e.Handled = true; }
        }


        private enum TreeActivationMode
        {
            ClassicDoubleClickOpen,
            ClickOpenDoubleClickProperties
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

                return item.NodeType == NodeType.Folder ? item.Name : item.Display;
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

        private readonly Font _consolasFont = new Font("Consolas", 10f, FontStyle.Regular);

        private void NameTextBox_DrawText(object sender, DrawTextEventArgs e)
        {
            var item = e.Node?.Tag as BookmarkTreeNode;
            if (item == null)
            {
                return;
            }

            if (item.Item.NodeType == NodeType.Folder)
            {
                e.Font = GetBoldFont(e.Font);
            }
            else
            {
                e.Font = _consolasFont;
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
