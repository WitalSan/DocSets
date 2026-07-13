using Aga.Controls.Tree;
using Aga.Controls.Tree.NodeControls;
using Microsoft.VisualStudio.Shell;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static System.Windows.Forms.VisualStyles.VisualStyleElement.Menu;
using WpfCommand = System.Windows.Input.ICommand;

namespace DocSets
{
    internal sealed class DocSetsWinFormsControl : UserControl
    {
        private readonly DocSetsViewModel _viewModel;
        private readonly ComboBox _workspaceCombo = new ComboBox();
        private readonly ToolStrip _standardGroupsStrip = new ToolStrip();
        private readonly ToolStrip _groupsStrip = new ToolStrip();
        private readonly ToolStrip _toolStrip = new ToolStrip();
        private readonly ToolStripSplitButton _undoButton = new ToolStripSplitButton();
        private readonly ToolStripSplitButton _redoButton = new ToolStripSplitButton();
        private readonly ToolStripButton _classicActivationButton = new ToolStripButton("Classic");
        private readonly ToolStripButton _clickOpenActivationButton = new ToolStripButton("Click→Open");
        private readonly ToolStripButton _collapseAllButton = new ToolStripButton();
        private readonly ToolStripButton _expandAllButton = new ToolStripButton();
        private readonly ToolStripButton _previousTreeNodeButton = new ToolStripButton();
        private readonly ToolStripButton _nextTreeNodeButton = new ToolStripButton();
        private readonly ToolStripButton _findPreviousButton = new ToolStripButton("<");
        private readonly ToolStripButton _findButton = new ToolStripButton("Найти");
        private readonly ToolStripLabel _findCounterLabel = new ToolStripLabel("0:0");
        private readonly ToolStripButton _findNextButton = new ToolStripButton(">");
        private readonly TreeViewAdv _tree = new TreeViewAdv();
        private readonly TreeModel _treeModel = new TreeModel();
        private OverflowNodeTextBox _nameNode;
        private readonly SplitContainer _contentSplit = new SplitContainer();
        private readonly BookmarkPropertiesPanel _propertiesPanel = new BookmarkPropertiesPanel();
        private readonly BookmarkPropertiesPanelExperimental _experimentalPropertiesPanel = new BookmarkPropertiesPanelExperimental();
        private readonly TabControl _propertiesTabs = new TabControl();
        private readonly System.Windows.Forms.Timer _propertiesSaveTimer = new System.Windows.Forms.Timer();
        private readonly System.Windows.Forms.Timer _experimentalPropertiesSaveTimer = new System.Windows.Forms.Timer();
        private CancellationTokenSource _previewCancellation;
        private CancellationTokenSource _experimentalPreviewCancellation;
        private readonly ToolStripButton _togglePropertiesButton = new ToolStripButton("Свойства");
        private bool _disposingProperties;
        private readonly Label _statusLabel = new Label();
        private readonly TextBox _filterTextBox = new TextBox();
        private readonly HashSet<BookmarkColor> _filterColors = new HashSet<BookmarkColor>();
        private readonly Dictionary<BookmarkColor, ToolStripMenuItem> _filterColorMenuItems = new Dictionary<BookmarkColor, ToolStripMenuItem>();
        private readonly FlowLayoutPanel _selectedFilterColorsPanel = new FlowLayoutPanel();
        private readonly ToolStripDropDownButton _filterColorsButton = new ToolStripDropDownButton();
        private ToolStripMenuItem _noColorFilterMenuItem;
        private bool _updatingColorFilter;
        private bool _restoringSplitter;
        private readonly ContextMenuStrip _nodeMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _headerMenu = new ContextMenuStrip();
        private readonly ContextMenuStrip _groupMenu = new ContextMenuStrip();
        private ToolStripMenuItem _addSolutionFolderMenuItem;
        private ToolStripMenuItem _addProjectFolderMenuItem;
        private ToolStripMenuItem _addFileFolderMenuItem;
        private ToolStripMenuItem _addClassFolderMenuItem;
        private readonly Dictionary<string, TreeColumn> _columnsByKey = new Dictionary<string, TreeColumn>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<TreeColumn, string> _columnKeys = new Dictionary<TreeColumn, string>();
        private readonly Dictionary<BookmarkColor, ToolStripMenuItem> _colorMenuItems = new Dictionary<BookmarkColor, ToolStripMenuItem>();
        private bool _refreshing;
        private bool _suppressColumnSave;
        private ToolStripControlHost _editingGroupHost;
        private DocumentItem _editingGroupSet;
        private bool _cancelGroupRename;
        private TreeActivationMode _treeActivationMode = TreeActivationMode.ClassicDoubleClickOpen;
        private readonly List<DocumentItem> _findResults = new List<DocumentItem>();
        private int _findIndex = -1;
        private bool _selectingFromFind;
        private bool _propertiesVisible = true;
        private bool _showSetsOverview;
        private readonly object _setsOverviewExpansionOwner = new object();
        private readonly Dictionary<object, HashSet<DocumentItem>> _collapsedItemsByView = new Dictionary<object, HashSet<DocumentItem>>();
        private readonly Dictionary<object, HashSet<DocumentItem>> _selectedItemsByView = new Dictionary<object, HashSet<DocumentItem>>();
        private object _renderedExpansionOwner;
        private bool _localStateRestored;
        private const string SetsOverviewTag = "__SETS_OVERVIEW__";
        private const string AddGroupTag = "__ADD_GROUP__";

        public DocSetsWinFormsControl(DocSetsViewModel viewModel)
        {
            this._viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
            BookmarkTreeNode.PinResolver = _viewModel.ResolvePin;
            BookmarkTreeNode.PinChecker = _viewModel.IsPinned;
            Dock = DockStyle.Fill;
            BuildLayout();
            BuildTree();
            BuildMenus();
            _viewModel.TreeChanged += ViewModel_TreeChanged;
            WireEvents();
            RefreshAll();
        }

        public System.Threading.Tasks.Task AddBookmarkFromEditorAsync()
        {
            var target = _viewModel.SelectedNode;
            if (!_viewModel.AddBookmarkCommand.CanExecute(target))
            {
                return System.Threading.Tasks.Task.CompletedTask;
            }

            _viewModel.AddBookmarkCommand.Execute(target);
            RefreshAll();
            return System.Threading.Tasks.Task.CompletedTask;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            RestoreSplitterPosition();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SaveLocalSettings();
                _viewModel.TreeChanged -= ViewModel_TreeChanged;
                _propertiesSaveTimer.Stop();
                _propertiesSaveTimer.Dispose();
                _experimentalPropertiesSaveTimer.Stop();
                _experimentalPropertiesSaveTimer.Dispose();
                _disposingProperties = true;
                CancelPreview(ref _previewCancellation);
                CancelPreview(ref _experimentalPreviewCancellation);
                _consolasFont.Dispose();

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
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 5, ColumnCount = 1 };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(root);

            var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Padding = new Padding(3) };
            top.Controls.Add(new Label { Text = "Workspace:", AutoSize = true, Padding = new Padding(0, 6, 4, 0) });
            _workspaceCombo.DropDownStyle = ComboBoxStyle.DropDownList;
            _workspaceCombo.Width = 220;
            top.Controls.Add(_workspaceCombo);
            root.Controls.Add(top, 0, 0);

            _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            _toolStrip.ImageScalingSize = new Size(IconProvider.IconSize, IconProvider.IconSize);
            SetupUndoRedoButtons();
            _toolStrip.Items.Add(new ToolStripSeparator());
            //AddButton("+Группа", _viewModel.AddSetCommand);
            //AddButton("Переим.", _viewModel.RenameSetCommand);
            //AddButton("-Группа", _viewModel.DeleteSetCommand);
            //_toolStrip.Items.Add(new ToolStripSeparator());
            //AddButton("+Папка", _viewModel.AddRootFolderCommand);
            //AddButton("+Вложенная", _viewModel.AddChildFolderCommand);
            AddButton("+Закладка", _viewModel.AddBookmarkCommand, AppIcon.LinkSymbol, "Добавить закладку (Ctrl+Num+)");
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddTreeNavigationButtons();
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddFindButtons();
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddTreeActivationModeButtons();
            _toolStrip.Items.Add(new ToolStripSeparator());
            AddPropertiesPanelButton();
            //AddButton("Копировать", _viewModel.CopySelectedNodesCommand);
            //AddButton("Вставить", _viewModel.PasteNodesCommand);
            top.Controls.Add(_toolStrip);

            var groupsPanel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
            groupsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            groupsPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            groupsPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            ConfigureGroupsStrip(_standardGroupsStrip, canOverflow: false);
            ConfigureGroupsStrip(_groupsStrip, canOverflow: true);
            _groupsStrip.MouseUp += GroupsStrip_MouseUp;
            _groupsStrip.KeyDown += GroupsStrip_KeyDown;
            groupsPanel.Controls.Add(_standardGroupsStrip, 0, 0);
            groupsPanel.Controls.Add(_groupsStrip, 0, 1);
            root.Controls.Add(groupsPanel, 0, 2);
            root.Controls.Add(CreateFilterRow(), 0, 1);

            _contentSplit.Dock = DockStyle.Fill;
            _contentSplit.Orientation = Orientation.Horizontal;
            _contentSplit.FixedPanel = FixedPanel.Panel2;
            _contentSplit.Panel1MinSize = 120;
            _contentSplit.Panel2MinSize = 70;
            _contentSplit.SplitterWidth = 5;
            _contentSplit.SplitterDistance = 100;
            _contentSplit.Panel1.Controls.Add(_tree);

            var propertiesHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(3) };
            _propertiesTabs.Dock = DockStyle.Fill;
            var classicPropertiesTab = new TabPage("Свойства-1");
            var experimentalPropertiesTab = new TabPage("Свойства-2");
            classicPropertiesTab.Controls.Add(_propertiesPanel);
            // experimentalPropertiesTab.Controls.Add(_experimentalPropertiesPanel);
            // _propertiesTabs.TabPages.Add(classicPropertiesTab);
            // _propertiesTabs.TabPages.Add(experimentalPropertiesTab);
            // propertiesHost.Controls.Add(_propertiesTabs);
            propertiesHost.Controls.Add(_experimentalPropertiesPanel);
            _contentSplit.Panel2.Controls.Add(propertiesHost);

            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.AutoEllipsis = true;
            _statusLabel.Padding = new Padding(4, 2, 4, 2);
            root.Controls.Add(_contentSplit, 0, 3);
            root.Controls.Add(_statusLabel, 0, 4);
        }

        private static void ConfigureGroupsStrip(ToolStrip strip, bool canOverflow)
        {
            strip.Dock = DockStyle.Fill;
            strip.GripStyle = ToolStripGripStyle.Hidden;
            strip.RenderMode = ToolStripRenderMode.System;
            strip.CanOverflow = canOverflow;
            strip.LayoutStyle = ToolStripLayoutStyle.HorizontalStackWithOverflow;
            strip.Stretch = true;
            strip.AutoSize = true;
            strip.ShowItemToolTips = true;
            strip.Padding = new Padding(2, 1, 2, 1);
        }

        private Control CreateFilterRow()
        {
            var row = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                WrapContents = false,
                Padding = new Padding(4, 2, 4, 2)
            };

            var resetButton = new Button
            {
                Text = "X",
                AutoSize = false,
                Width = 26,
                Height = 24,
                Margin = new Padding(0, 0, 5, 0),
                FlatStyle = FlatStyle.System
            };
            resetButton.Click += (_, __) => ResetFilters();
            new ToolTip().SetToolTip(resetButton, "Сбросить текстовый и цветовой фильтры");
            row.Controls.Add(resetButton);

            row.Controls.Add(new Label { Text = "Фильтр:", AutoSize = true, Padding = new Padding(0, 5, 4, 0) });
            _filterTextBox.Width = 220;
            _filterTextBox.TextChanged += (_, __) => { RebuildTree(); SaveLocalState(); };
            row.Controls.Add(_filterTextBox);

            var colorStrip = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                AutoSize = true,
                CanOverflow = false,
                RenderMode = ToolStripRenderMode.System,
                Padding = new Padding(0),
                Margin = new Padding(5, 0, 0, 0)
            };

            _filterColorsButton.Text = "Цвета: без фильтра";
            _filterColorsButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            colorStrip.Items.Add(_filterColorsButton);
            BuildFilterColorMenu();
            row.Controls.Add(colorStrip);

            _selectedFilterColorsPanel.AutoSize = true;
            _selectedFilterColorsPanel.WrapContents = false;
            _selectedFilterColorsPanel.Margin = new Padding(5, 2, 0, 0);
            _selectedFilterColorsPanel.Padding = new Padding(0);
            row.Controls.Add(_selectedFilterColorsPanel);

            UpdateFilterColorUi();
            return row;
        }

        private void BuildFilterColorMenu()
        {
            _filterColorsButton.DropDownItems.Clear();
            _filterColorMenuItems.Clear();

            _noColorFilterMenuItem = new ToolStripMenuItem("Без фильтра");
            _noColorFilterMenuItem.Click += (_, __) => ClearColorFilter();
            _filterColorsButton.DropDownItems.Add(_noColorFilterMenuItem);
            _filterColorsButton.DropDownItems.Add(new ToolStripSeparator());

            AddFilterColorMenuItem("Без цветовой метки", BookmarkColor.None);
            AddFilterColorMenuItem("Красный", BookmarkColor.Red);
            AddFilterColorMenuItem("Оранжевый", BookmarkColor.Orange);
            AddFilterColorMenuItem("Жёлтый", BookmarkColor.Yellow);
            AddFilterColorMenuItem("Зелёный", BookmarkColor.Green);
            AddFilterColorMenuItem("Бирюзовый", BookmarkColor.Cyan);
            AddFilterColorMenuItem("Синий", BookmarkColor.Blue);
            AddFilterColorMenuItem("Фиолетовый", BookmarkColor.Purple);
            AddFilterColorMenuItem("Серый", BookmarkColor.Gray);
        }

        private void AddFilterColorMenuItem(string text, BookmarkColor color)
        {
            var item = new ToolStripMenuItem(text)
            {
                ImageScaling = ToolStripItemImageScaling.None,
                Tag = color
            };
            item.Click += (_, __) => ToggleColorFilter(color);
            _filterColorMenuItems[color] = item;
            _filterColorsButton.DropDownItems.Add(item);
        }

        private void ToggleColorFilter(BookmarkColor color)
        {
            if (_filterColors.Contains(color))
                _filterColors.Remove(color);
            else
                _filterColors.Add(color);

            UpdateFilterColorUi();
            RebuildTree();
            SaveLocalState();
        }

        private void ClearColorFilter()
        {
            _updatingColorFilter = true;
            try
            {
                _filterColors.Clear();
            }
            finally
            {
                _updatingColorFilter = false;
            }

            UpdateFilterColorUi();
            RebuildTree();
            SaveLocalState();
        }

        private void ResetFilters()
        {
            _updatingColorFilter = true;
            try
            {
                _filterColors.Clear();
                _filterTextBox.Text = string.Empty;
            }
            finally
            {
                _updatingColorFilter = false;
            }

            UpdateFilterColorUi();
            RebuildTree();
            SaveLocalState();
        }

        private void UpdateFilterColorUi()
        {
            var hasFilter = _filterColors.Count > 0;
            if (_noColorFilterMenuItem != null)
            {
                SetFilterMenuImage(_noColorFilterMenuItem, CreateFilterChoiceImage(null, !hasFilter));
            }

            foreach (var pair in _filterColorMenuItems)
            {
                SetFilterMenuImage(pair.Value, CreateFilterChoiceImage(pair.Key, _filterColors.Contains(pair.Key)));
            }

            _filterColorsButton.Text = hasFilter
                ? "Цвета: " + _filterColors.Count
                : "Цвета: без фильтра";

            _selectedFilterColorsPanel.SuspendLayout();
            try
            {
                _selectedFilterColorsPanel.Controls.Clear();
                foreach (var color in _filterColors.OrderBy(x => (int)x))
                {
                    var marker = new Label
                    {
                        AutoSize = false,
                        Width = IconProvider.IconSize,
                        Height = IconProvider.IconSize,
                        Margin = new Padding(2, 0, 0, 0),
                        BackColor = color == BookmarkColor.None ? Color.White : GetBookmarkColor(color),
                        BorderStyle = BorderStyle.FixedSingle,
                        Text = color == BookmarkColor.None ? "×" : string.Empty,
                        TextAlign = ContentAlignment.MiddleCenter
                    };
                    new ToolTip().SetToolTip(marker, GetBookmarkColorName(color));
                    _selectedFilterColorsPanel.Controls.Add(marker);
                }
            }
            finally
            {
                _selectedFilterColorsPanel.ResumeLayout();
            }
        }

        private static Image CreateFilterChoiceImage(BookmarkColor? color, bool selected)
        {
            var bitmap = new Bitmap(36, 16);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                var checkBox = new Rectangle(0, 1, 13, 13);
                graphics.FillRectangle(SystemBrushes.Window, checkBox);
                graphics.DrawRectangle(SystemPens.ControlDarkDark, checkBox);
                if (selected)
                {
                    using (var pen = new Pen(SystemColors.Highlight, 2))
                    {
                        graphics.DrawLines(pen, new[]
                        {
                            new Point(3, 7),
                            new Point(6, 10),
                            new Point(11, 3)
                        });
                    }
                }

                if (color.HasValue)
                {
                    var swatch = new Rectangle(20, 1, 13, 13);
                    using (var brush = new SolidBrush(GetBookmarkColor(color.Value)))
                    {
                        graphics.FillRectangle(brush, swatch);
                    }
                    graphics.DrawRectangle(Pens.Black, swatch);
                    if (color.Value == BookmarkColor.None)
                    {
                        graphics.DrawLine(Pens.Gray, swatch.Left, swatch.Bottom, swatch.Right, swatch.Top);
                    }
                }
            }

            return bitmap;
        }

        private static void SetFilterMenuImage(ToolStripMenuItem item, Image image)
        {
            var oldImage = item.Image;
            item.Image = image;
            item.ImageScaling = ToolStripItemImageScaling.None;
            oldImage?.Dispose();
        }

        private static string GetBookmarkColorName(BookmarkColor color)
        {
            switch (color)
            {
                case BookmarkColor.Red: return "Красный";
                case BookmarkColor.Orange: return "Оранжевый";
                case BookmarkColor.Yellow: return "Жёлтый";
                case BookmarkColor.Green: return "Зелёный";
                case BookmarkColor.Cyan: return "Бирюзовый";
                case BookmarkColor.Blue: return "Синий";
                case BookmarkColor.Purple: return "Фиолетовый";
                case BookmarkColor.Gray: return "Серый";
                default: return "Без цветовой метки";
            }
        }

        private bool MatchesFilter(DocumentItem item)
        {
            if (item == null)
                return false;

            var text = (_filterTextBox.Text ?? string.Empty).Trim();
            if (text.Length > 0 && (item.Name ?? string.Empty).IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0)
                return false;

            return _filterColors.Count == 0 || _filterColors.Contains(item.Color);
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
            SaveLocalState();
        }

        private void RestoreSplitterPosition()
        {
            if (_viewModel.Ui == null || _contentSplit.ClientSize.Height <= 1)
            {
                return;
            }

            var panelHeight = Math.Max(_contentSplit.Panel2MinSize, _viewModel.Ui.PropertiesPanelHeight);
            var maximumPanelHeight = _contentSplit.ClientSize.Height
                - _contentSplit.Panel1MinSize
                - _contentSplit.SplitterWidth;
            panelHeight = Math.Min(panelHeight, Math.Max(_contentSplit.Panel2MinSize, maximumPanelHeight));

            _restoringSplitter = true;
            try
            {
                _contentSplit.SplitterDistance = Math.Max(_contentSplit.ClientSize.Height
                    - panelHeight
                    - _contentSplit.SplitterWidth, 1);
            }
            finally
            {
                _restoringSplitter = false;
            }
        }

        private void SaveSplitterPosition()
        {
            if (_restoringSplitter || _viewModel.Ui == null || _contentSplit.Panel2Collapsed)
            {
                return;
            }

            var panelHeight = _contentSplit.ClientSize.Height
                - _contentSplit.SplitterDistance
                - _contentSplit.SplitterWidth;
            if (panelHeight < _contentSplit.Panel2MinSize)
            {
                return;
            }

            if (_viewModel.Ui.PropertiesPanelHeight != panelHeight)
            {
                _viewModel.Ui.PropertiesPanelHeight = panelHeight;
                SaveLocalState();
            }
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

        private string GetColumnText(DocumentItem item, string key)
        {
            switch (key)
            {
                case "name":
                {
                    var target = _viewModel.ResolvePin(item);
                    if (target == null) return item?.IsPinItem == true ? "<missing>" : string.Empty;
                    return target.NodeType == NodeType.Folder && string.IsNullOrWhiteSpace(target.Name) ? "Новая папка" : target.Name ?? string.Empty;
                }
                case "file": return item?.Path ?? string.Empty;
                case "line": return item == null || item.NodeType == NodeType.Folder ? string.Empty : item.Line.ToString();
                case "comment": return item?.CommentFirstLine ?? string.Empty;
                case "project": return item?.Project ?? string.Empty;
                case "symbol": return item?.Symbol ?? string.Empty;
                default: return string.Empty;
            }
        }

        private void AddButton(string text, WpfCommand command, AppIcon? icon = null, string toolTipText = null)
        {
            var button = new ToolStripButton(text)
            {
                DisplayStyle = icon.HasValue ? ToolStripItemDisplayStyle.ImageAndText : ToolStripItemDisplayStyle.Text,
                Image = icon.HasValue ? IconProvider.Get(icon.Value, IconProvider.IconSize) : null,
                ToolTipText = toolTipText ?? text
            };
            button.Click += (_, __) => Execute(command, null);
            _toolStrip.Items.Add(button);
        }

        private void AddTreeNavigationButtons()
        {
            _expandAllButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            _expandAllButton.Image = IconProvider.Get(AppIcon.ExpandAll, IconProvider.IconSize);
            _expandAllButton.ToolTipText = "Развернуть все узлы дерева";
            _expandAllButton.Click += (_, __) => _tree.ExpandAll();

            _collapseAllButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            _collapseAllButton.Image = IconProvider.Get(AppIcon.CollapseAll, IconProvider.IconSize);
            _collapseAllButton.ToolTipText = "Свернуть все узлы дерева";
            _collapseAllButton.Click += (_, __) => _tree.CollapseAll();

            _previousTreeNodeButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            _previousTreeNodeButton.Image = IconProvider.Get(AppIcon.NavigatePrevious, IconProvider.IconSize);
            _previousTreeNodeButton.ToolTipText = "Перейти к предыдущей видимой закладке";
            _previousTreeNodeButton.Click += (_, __) => MoveTreeBookmark(-1);

            _nextTreeNodeButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            _nextTreeNodeButton.Image = IconProvider.Get(AppIcon.NavigateNext, IconProvider.IconSize);
            _nextTreeNodeButton.ToolTipText = "Перейти к следующей видимой закладке";
            _nextTreeNodeButton.Click += (_, __) => MoveTreeBookmark(1);

            _toolStrip.Items.Add(_expandAllButton);
            _toolStrip.Items.Add(_collapseAllButton);
            _toolStrip.Items.Add(_previousTreeNodeButton);
            _toolStrip.Items.Add(_nextTreeNodeButton);
        }

        private void MoveTreeBookmark(int delta)
        {
            var nodes = EnumerateTreeNodes(_tree.Root)
                .Where(node => (node.Tag as BookmarkTreeNode)?.Item?.Type != BookmarkType.Empty)
                .ToList();

            if (nodes.Count == 0)
            {
                return;
            }

            var current = _tree.CurrentNode ?? _tree.SelectedNode;
            var index = nodes.IndexOf(current);
            if (index < 0)
            {
                index = delta > 0 ? -1 : 0;
            }

            index = (index + delta) % nodes.Count;
            if (index < 0)
            {
                index += nodes.Count;
            }

            var targetNode = nodes[index];
            var item = (targetNode.Tag as BookmarkTreeNode)?.Item;
            if (item == null)
            {
                return;
            }

            _tree.ClearSelection();
            targetNode.IsSelected = true;
            _tree.SelectedNode = targetNode;
            _tree.EnsureVisible(targetNode);
            _viewModel.SetSelectedNodes(new[] { item });
            LoadPropertiesPanel(item);
            Execute(_viewModel.OpenBookmarkCommand, item);
        }
        
        private IEnumerable<TreeNodeAdv> EnumerateVisibleTreeNodes(TreeNodeAdv root)
        {
            foreach (TreeNodeAdv child in root.Children)
            {
                yield return child;
                if (!child.IsExpanded)
                {
                    continue;
                }

                foreach (var nested in EnumerateVisibleTreeNodes(child))
                {
                    yield return nested;
                }
            }
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

        private void SetupUndoRedoButtons()
        {
            _undoButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            _undoButton.Image = IconProvider.Get(AppIcon.Undo, IconProvider.IconSize);
            _undoButton.ImageScaling = ToolStripItemImageScaling.None;
            _undoButton.ToolTipText = "Отменить (Ctrl+Z)";
            _undoButton.ButtonClick += (_, __) => Execute(_viewModel.UndoCommand, null);
            _undoButton.DropDownOpening += (_, __) => FillUndoDropDown(_undoButton, _viewModel.UndoOperations, true);

            _redoButton.DisplayStyle = ToolStripItemDisplayStyle.Image;
            _redoButton.Image = IconProvider.Get(AppIcon.Redo, IconProvider.IconSize);
            _redoButton.ImageScaling = ToolStripItemImageScaling.None;
            _redoButton.ToolTipText = "Повторить (Ctrl+Y)";
            _redoButton.ButtonClick += (_, __) => Execute(_viewModel.RedoCommand, null);
            _redoButton.DropDownOpening += (_, __) => FillUndoDropDown(_redoButton, _viewModel.RedoOperations, false);

            _toolStrip.Items.Add(_undoButton);
            _toolStrip.Items.Add(_redoButton);
        }

        private void FillUndoDropDown(ToolStripSplitButton button, IReadOnlyList<string> operations, bool undo)
        {
            button.DropDownItems.Clear();
            if (operations.Count == 0)
            {
                button.DropDownItems.Add(new ToolStripMenuItem("Нет операций") { Enabled = false });
                return;
            }

            for (var i = 0; i < operations.Count; i++)
            {
                var count = i + 1;
                var item = new ToolStripMenuItem(operations[i]);
                item.ToolTipText = undo ? $"Отменить операций: {count}" : $"Повторить операций: {count}";
                item.Click += async (_, __) =>
                {
                    if (undo) await _viewModel.UndoManyAsync(count);
                    else await _viewModel.RedoManyAsync(count);
                    RefreshAll();
                };
                button.DropDownItems.Add(item);
            }
        }

        private void AddFindButtons()
        {
            _findPreviousButton.DisplayStyle = ToolStripItemDisplayStyle.Text;
            _findPreviousButton.ToolTipText = "Предыдущая найденная закладка";
            _findPreviousButton.Click += (_, __) => MoveFindSelection(-1);

            _findButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            _findButton.Image = IconProvider.Get(AppIcon.Find, IconProvider.IconSize);
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
                LeftMargin = 2, 
            });
            _tree.NodeControls.Add(new NodeIcon
            {
                DataPropertyName = nameof(BookmarkTreeNode.PinnedImage),
                ParentColumn = _columnsByKey["name"],
                LeftMargin = 2,
                ScaleMode = ImageScaleMode.Clip
            });

            var colorNode = new NodeTextBox
            {
                DataPropertyName = nameof(BookmarkTreeNode.ColorMarker),
                ParentColumn = _columnsByKey["name"],
                LeftMargin = 3
            };
            colorNode.DrawText += ColorNode_DrawText;
            _tree.NodeControls.Add(colorNode);

            _nameNode = new OverflowNodeTextBox
            {
                DataPropertyName = nameof(BookmarkTreeNode.Name),
                ParentColumn = _columnsByKey["name"],
                EditEnabled = true,
                IncrementalSearchEnabled = true,
                LeftMargin = 3,
                ColumnTextResolver = GetColumnTextForOverflow
            };
            _nameNode.DrawText += NameTextBox_DrawText;
            _nameNode.LabelChanged += NameNode_LabelChanged;

            _tree.NodeControls.Add(_nameNode);
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
            _nodeMenu.ImageScalingSize = new Size(16, 16);
            _groupMenu.ImageScalingSize = new Size(16, 16);
            AddNodeMenu("Open", _viewModel.OpenBookmarkCommand, null, AppIcon.LinkSymbol);
            AddNodeMenu("Pin / Unpin", _viewModel.TogglePinCommand, null, AppIcon.PinOverlay);
            var goToOriginalMenu = new ToolStripMenuItem("Перейти к оригиналу") { Name = "GoToPinOriginal" };
            goToOriginalMenu.Click += (_, __) => GoToPinOriginal(GetCurrentItem());
            _nodeMenu.Items.Add(goToOriginalMenu);
            AddSyncWithCurrentPositionMenu();
            //AddMenu("Обновить", _viewModel.UpdateBookmarkCommand);
            //AddMenu("Переименовать", _viewModel.RenameNodeCommand);
            //AddMenu("Удалить", _viewModel.DeleteNodeCommand);
            //_nodeMenu.Items.Add(new ToolStripSeparator());
            AddNodeMenu("Add BookMark", _viewModel.AddBookmarkCommand, null, AppIcon.LinkSymbol);
            AddContextFolderMenus();
            _nodeMenu.Items.Add(new ToolStripSeparator());
            AddNodeMenu("Copy", _viewModel.CopySelectedNodesCommand, "Ctrl+C", AppIcon.Copy);
            AddNodeMenu("Paste", _viewModel.PasteNodesCommand, "Ctrl+V", AppIcon.Paste);
            //AddJsonMenu();
            AddNodeMenu("Copy JSON", _viewModel.CopySelectedNodesAsJsonCommand, null, AppIcon.Copy);
            AddNodeMenu("Del", _viewModel.DeleteNodeCommand);
            _nodeMenu.Items.Add(new ToolStripSeparator());
            AddColorMenu();
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

            _groupMenu.Items.Add(new ToolStripSeparator());
            AddGroupMenu("Copy JSON", _viewModel.CopySelectedNodesAsJsonCommand, null, AppIcon.Copy);
            AddGroupMenu("Paste", _viewModel.PasteNodesCommand, "Ctrl+V", AppIcon.Paste);
            _groupMenu.Items.Add(new ToolStripSeparator());
            AddRegenerateAllIdsMenu();

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


        private void AddRegenerateAllIdsMenu()
        {
            var item = new ToolStripMenuItem("Обновить все Id")
            {
                Name = "RegenerateAllIdsMenuItem",
                Tag = _viewModel.RegenerateAllIdsCommand
            };

            item.Click += (_, __) =>
            {
                var result = MessageBox.Show(
                    this,
                    "Все Id будут заново сформированы из текущих имен элементов. " +
                    "Локальные настройки других решений, использующих этот workspace, могут стать недействительными. Продолжить?",
                    "Обновить все Id",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);

                if (result != DialogResult.Yes) return;

                Execute(_viewModel.RegenerateAllIdsCommand, null);
                RefreshAll();
            };

            _groupMenu.Items.Add(item);
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


        private void AddSyncWithCurrentPositionMenu()
        {
            var item = new ToolStripMenuItem("Синхронизировать с текущей позицией")
            {
                Tag = _viewModel.SyncWithCurrentPositionCommand,
                Image = IconProvider.Get(AppIcon.Sync, 16)
            };

            item.Click += async (_, __) =>
            {
                var current = GetCurrentItem();
                if (current == null)
                {
                    return;
                }

                await _viewModel.SyncWithCurrentPositionAsync(current);
                RebuildTree();
                RefreshStatus();
            };

            _nodeMenu.Items.Add(item);
        }

        private void AddColorMenu()
        {
            var colorMenu = new ToolStripMenuItem("Цвет");
            AddColorMenuItem(colorMenu, "Без цвета", BookmarkColor.None);
            AddColorMenuItem(colorMenu, "Красный", BookmarkColor.Red);
            AddColorMenuItem(colorMenu, "Оранжевый", BookmarkColor.Orange);
            AddColorMenuItem(colorMenu, "Жёлтый", BookmarkColor.Yellow);
            AddColorMenuItem(colorMenu, "Зелёный", BookmarkColor.Green);
            AddColorMenuItem(colorMenu, "Бирюзовый", BookmarkColor.Cyan);
            AddColorMenuItem(colorMenu, "Синий", BookmarkColor.Blue);
            AddColorMenuItem(colorMenu, "Фиолетовый", BookmarkColor.Purple);
            AddColorMenuItem(colorMenu, "Серый", BookmarkColor.Gray);
            _nodeMenu.Items.Add(colorMenu);
        }

        private void AddColorMenuItem(ToolStripMenuItem parent, string text, BookmarkColor color)
        {
            var item = new ToolStripMenuItem(text)
            {
                Tag = color,
                Image = CreateColorSwatch(color, 16)
            };

            _colorMenuItems[color] = item;

            item.Click += async (_, __) =>
            {
                var selected = _viewModel.SelectedNodes?.ToList() ?? new List<DocumentItem>();
                if (selected.Count == 0)
                {
                    var current = GetCurrentItem();
                    if (current != null)
                    {
                        selected.Add(current);
                    }
                }

                foreach (var bookmark in selected)
                {
                    bookmark.Color = color;
                }

                await _viewModel.SaveAsync();
                _tree.Invalidate();
            };

            parent.DropDownItems.Add(item);
        }

        private static Image CreateColorSwatch(BookmarkColor color, int size)
        {
            var bitmap = new Bitmap(size, size);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                var rectangle = new Rectangle(2, 2, size - 5, size - 5);
                using (var brush = new SolidBrush(GetBookmarkColor(color)))
                {
                    graphics.FillRectangle(brush, rectangle);
                }

                graphics.DrawRectangle(Pens.Black, rectangle);
                if (color == BookmarkColor.None)
                {
                    graphics.DrawLine(Pens.Gray, rectangle.Left, rectangle.Bottom, rectangle.Right, rectangle.Top);
                }
            }

            return bitmap;
        }

        private static Color GetBookmarkColor(BookmarkColor color)
        {
            switch (color)
            {
                case BookmarkColor.Red: return Color.FromArgb(237, 28, 54);
                case BookmarkColor.Orange: return Color.FromArgb(255, 140, 0);
                case BookmarkColor.Yellow: return Color.FromArgb(255, 229, 0);
                case BookmarkColor.Green: return Color.FromArgb(31, 201, 37);
                case BookmarkColor.Cyan: return Color.FromArgb(0, 188, 212);
                case BookmarkColor.Blue: return Color.FromArgb(22, 139, 205);
                case BookmarkColor.Purple: return Color.FromArgb(156, 39, 176);
                case BookmarkColor.Gray: return Color.FromArgb(128, 128, 128);
                default: return Color.White;
            }
        }

        private void AddPropertiesMenu()
        {
            var item = new ToolStripMenuItem("Properties...")
            {
                Image = IconProvider.Get(AppIcon.Properties, 16)
            };
            item.Click += delegate { ShowBookmarkProperties(GetCurrentItem()); };
            _nodeMenu.Items.Add(item);
        }

        private void AddContextFolderMenus()
        {
            AddNodeMenu("Add Folder", _viewModel.AddFolderCommand, null, AppIcon.Folder);
            _addSolutionFolderMenuItem = AddContextFolderMenu("Add Folder <Solution>", ActiveDocumentFolderKind.Solution);
            _addProjectFolderMenuItem = AddContextFolderMenu("Add Folder <Project>", ActiveDocumentFolderKind.Project);
            _addFileFolderMenuItem = AddContextFolderMenu("Add Folder <File>", ActiveDocumentFolderKind.File);
            _addClassFolderMenuItem = AddContextFolderMenu("Add Folder <Class>", ActiveDocumentFolderKind.Class);
        }

        private ToolStripMenuItem AddContextFolderMenu(string text, ActiveDocumentFolderKind kind)
        {
            var item = new ToolStripMenuItem(text)
            {
                Tag = kind,
                Image = IconProvider.Get(
                    kind == ActiveDocumentFolderKind.File ? AppIcon.FolderLinkFile :
                    kind == ActiveDocumentFolderKind.Class ? AppIcon.FolderLinkSymbol : AppIcon.Folder,
                    16)
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

            item = _viewModel.ResolvePin(item);
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
                LoadPropertiesPanel(item);
                RefreshStatus();
            }
        }

        private void AddGroupMenu(string text, WpfCommand command, string shortcut = null, AppIcon? icon = null)
        {
            var item = new ToolStripMenuItem(text)
            {
                Image = icon.HasValue ? IconProvider.Get(icon.Value, IconProvider.IconSize) : null,
                Tag = command
            };
            
            if (!string.IsNullOrEmpty(shortcut)) item.ShortcutKeyDisplayString = shortcut;

            item.Click += (_, __) =>
            {
                SelectGroupMenuTarget();
                Execute(command, null);
                RefreshAll();
            };

            _groupMenu.Items.Add(item);
        }


        private ToolStripButton FindGroupButton(DocumentItem set)
        {
            if (set == null)
            {
                return null;
            }

            return _standardGroupsStrip.Items.OfType<ToolStripButton>()
                .Concat(_groupsStrip.Items.OfType<ToolStripButton>())
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

            _groupMenu.Tag = button.Tag as DocumentItem;
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
            if (item != null && item.Tag is DocumentItem)
            {
                return;
            }

            _groupMenu.Tag = null;
            _groupMenu.Show(_groupsStrip, e.Location);
        }

        private void SelectGroupMenuTarget()
        {
            if (_groupMenu.Tag is DocumentItem set && !ReferenceEquals(_viewModel.SelectedSet, set))
            {
                _viewModel.SelectedSet = set;
                UpdateGroupButtonsChecked();
                RebuildTree();
                RefreshStatus();
            }
        }

        private void UpdateGroupButtonsChecked()
        {
            foreach (var strip in new[] { _standardGroupsStrip, _groupsStrip })
            foreach (ToolStripItem item in strip.Items)
            {
                if (item is ToolStripButton groupButton)
                {
                    if (Equals(groupButton.Tag, SetsOverviewTag))
                        groupButton.Checked = _showSetsOverview;
                    else if (groupButton.Tag is DocumentItem group)
                        groupButton.Checked = !_showSetsOverview && ReferenceEquals(group, _viewModel.SelectedSet);
                }
            }

            ApplyGroupsOverflowPolicy();
        }

        private void ApplyGroupsOverflowPolicy()
        {
            ToolStripItem activeItem = null;

            foreach (ToolStripItem item in _groupsStrip.Items)
            {
                if (item is ToolStripSeparator)
                {
                    item.Overflow = ToolStripItemOverflow.Never;
                    continue;
                }

                if (!(item is ToolStripButton button))
                {
                    continue;
                }

                var isFullTree = Equals(button.Tag, SetsOverviewTag);
                var isAddButton = Equals(button.Tag, AddGroupTag);
                var isActive = button.Checked;

                button.Overflow = isFullTree || isAddButton || isActive
                    ? ToolStripItemOverflow.Never
                    : ToolStripItemOverflow.AsNeeded;

                if (isActive)
                {
                    activeItem = button;
                }
            }

            _groupsStrip.PerformLayout();

            // Повторная разметка после смены Overflow гарантирует, что активная вкладка
            // будет возвращена из overflow-меню в основную полосу.
            if (activeItem != null)
            {
                activeItem.Invalidate();
            }

            ConfigureGroupsOverflow();

            _groupsStrip.Invalidate();
        }
        
        private void ConfigureGroupsOverflow()
        {
            //TODO: выпадающий список групп (когда кнопки не влезают) хотелось в одну колонку - не работает все равно несколько групп в строке
            var overflowButton = _groupsStrip.OverflowButton;

            if (overflowButton?.DropDown == null)
                return;

            overflowButton.DropDown.LayoutStyle =
                ToolStripLayoutStyle.VerticalStackWithOverflow;

            overflowButton.DropDown.AutoSize = true;
            overflowButton.DropDown.MaximumSize = new Size(350, 0);
        }

        private void AddNodeMenu(string text, WpfCommand command, string shortcut = null, AppIcon? icon = null)
        {
            var item = new ToolStripMenuItem(text)
            {
                Image = icon.HasValue ? IconProvider.Get(icon.Value, IconProvider.IconSize) : null
            };
            if (!string.IsNullOrEmpty(shortcut)) item.ShortcutKeyDisplayString = shortcut;
            item.Click += (_, __) => Execute(command, GetCurrentItem());
            _nodeMenu.Items.Add(item);
        }

        private void WireEvents()
        {
            _workspaceCombo.SelectedIndexChanged += async (_, __) =>
            {
                if (_refreshing) return;
                var workspace = _workspaceCombo.SelectedItem as WorkspaceInfo;
                if (workspace == null) return;
                _showSetsOverview = false;
                ClearFindResults();
                await _viewModel.SelectWorkspaceAsync(workspace);
                RefreshAll();
            };

            _tree.SelectionChanged += (_, __) =>
            {
                if (!_selectingFromFind)
                {
                    SyncSelectionFromTree();
                }
            };
            _tree.Collapsing += Tree_Collapsing;
            WireTreeActivationBehavior();
            _tree.ItemDrag += (_, __) => _tree.DoDragDropSelectedNodes(DragDropEffects.Move | DragDropEffects.Copy);
            _tree.DragOver += Tree_DragOver;
            _tree.DragDrop += Tree_DragDrop;
            _tree.KeyDown += Tree_KeyDown;
            _tree.MouseUp += Tree_MouseUp;
            _tree.ColumnWidthChanged += (_, __) => SaveColumnLayout();
            _tree.ColumnReordered += (_, __) => SaveColumnLayout();
            _contentSplit.SplitterMoved += (_, __) => SaveSplitterPosition();

            _propertiesSaveTimer.Interval = 500;
            _propertiesSaveTimer.Tick += async (_, __) =>
            {
                _propertiesSaveTimer.Stop();
                var undoDescription = _propertiesPanel.GetPendingChangeDescription();
                if (undoDescription != null) _viewModel.CaptureUndoState(undoDescription);
                if (_propertiesPanel.ApplyToCurrentItem())
                {
                    await _viewModel.SaveAsync();
                    RebuildTree();
                    RefreshStatus();
                }
            };
            _propertiesPanel.ItemChanged += (_, __) =>
            {
                _propertiesSaveTimer.Stop();
                _propertiesSaveTimer.Start();
            };
            _propertiesPanel.ColorChanged += async (_, __) =>
            {
                var targets = GetSelectedPropertyTargets(GetCurrentItem());
                await _viewModel.SetColorAsync(targets, _propertiesPanel.SelectedColor);
                _tree.Invalidate();
                LoadPropertiesPanel(GetCurrentItem());
                RefreshStatus();
            };
            _propertiesPanel.PinChanged += async (_, __) =>
            {
                var current = GetCurrentItem();
                var targets = GetSelectedPropertyTargets(current);
                if (targets.Count == 0) return;

                await _viewModel.SetPinnedAsync(targets, _propertiesPanel.RequestedPinState);
                RebuildTree();
                LoadPropertiesPanel(current);
                RefreshStatus();
            };
            _propertiesPanel.RefreshCodeRequested += async (_, __) =>
            {
                var current = _propertiesPanel.CurrentItem ?? GetCurrentItem();
                if (current == null)
                {
                    return;
                }

                await _viewModel.SyncWithCurrentPositionAsync(current);
                LoadPropertiesPanel(current);
                RebuildTree();
                RefreshStatus();
            };
            _propertiesPanel.PreviewRequested += async (_, __) =>
            {
                if (_propertiesVisible && _propertiesTabs.SelectedTab?.Controls.Contains(_propertiesPanel) == true)
                {
                    await RefreshLivePreviewAsync();
                }
            };
            _propertiesPanel.SymbolLinkClicked += async symbol =>
            {
                var current = _propertiesPanel.CurrentItem ?? GetCurrentItem();
                await _viewModel.OpenSymbolAsync(current, symbol);
            };
            _propertiesPanel.Leave += async (_, __) =>
            {
                _propertiesSaveTimer.Stop();
                var undoDescription = _propertiesPanel.GetPendingChangeDescription();
                if (undoDescription != null) _viewModel.CaptureUndoState(undoDescription);
                if (_propertiesPanel.ApplyToCurrentItem())
                {
                    await _viewModel.SaveAsync();
                    RebuildTree();
                    RefreshStatus();
                }
            };

            _experimentalPropertiesSaveTimer.Interval = 500;
            _experimentalPropertiesSaveTimer.Tick += async (_, __) =>
            {
                _experimentalPropertiesSaveTimer.Stop();
                var undoDescription = _experimentalPropertiesPanel.GetPendingChangeDescription();
                if (undoDescription != null) _viewModel.CaptureUndoState(undoDescription);
                if (_experimentalPropertiesPanel.ApplyToCurrentItem())
                {
                    await _viewModel.SaveAsync();
                    RebuildTree();
                    RefreshStatus();
                }
            };
            _experimentalPropertiesPanel.ItemChanged += (_, __) =>
            {
                _experimentalPropertiesSaveTimer.Stop();
                _experimentalPropertiesSaveTimer.Start();
            };
            _experimentalPropertiesPanel.ColorChanged += async (_, __) =>
            {
                var targets = GetSelectedPropertyTargets(GetCurrentItem());
                await _viewModel.SetColorAsync(targets, _experimentalPropertiesPanel.SelectedColor);
                _tree.Invalidate();
                LoadPropertiesPanel(GetCurrentItem());
                RefreshStatus();
            };
            _experimentalPropertiesPanel.PinChanged += async (_, __) =>
            {
                var current = GetCurrentItem();
                var targets = GetSelectedPropertyTargets(current);
                if (targets.Count == 0) return;

                await _viewModel.SetPinnedAsync(targets, _experimentalPropertiesPanel.RequestedPinState);
                RebuildTree();
                LoadPropertiesPanel(current);
                RefreshStatus();
            };
            _experimentalPropertiesPanel.RefreshCodeRequested += async (_, __) =>
            {
                var current = _experimentalPropertiesPanel.CurrentItem ?? GetCurrentItem();
                if (current == null) return;

                await _viewModel.SyncWithCurrentPositionAsync(current);
                LoadPropertiesPanel(current);
                RebuildTree();
                RefreshStatus();
            };
            _experimentalPropertiesPanel.PreviewRequested += async (_, __) =>
            {
                if (_propertiesVisible && _experimentalPropertiesPanel.Visible)
                {
                    await RefreshExperimentalLivePreviewAsync();
                }
            };
            _experimentalPropertiesPanel.SymbolLinkClicked += async symbol =>
            {
                var current = _experimentalPropertiesPanel.CurrentItem ?? GetCurrentItem();
                await _viewModel.OpenSymbolAsync(current, symbol);
            };
            _experimentalPropertiesPanel.Leave += async (_, __) =>
            {
                _experimentalPropertiesSaveTimer.Stop();
                var undoDescription = _experimentalPropertiesPanel.GetPendingChangeDescription();
                if (undoDescription != null) _viewModel.CaptureUndoState(undoDescription);
                if (_experimentalPropertiesPanel.ApplyToCurrentItem())
                {
                    await _viewModel.SaveAsync();
                    RebuildTree();
                    RefreshStatus();
                }
            };
            _propertiesTabs.SelectedIndexChanged += (_, __) =>
            {
                _propertiesSaveTimer.Stop();
                _experimentalPropertiesSaveTimer.Stop();

                var changed = false;
                var classicDescription = _propertiesPanel.GetPendingChangeDescription();
                if (classicDescription != null) _viewModel.CaptureUndoState(classicDescription);
                changed |= _propertiesPanel.ApplyToCurrentItem();

                var experimentalDescription = _experimentalPropertiesPanel.GetPendingChangeDescription();
                if (experimentalDescription != null) _viewModel.CaptureUndoState(experimentalDescription);
                changed |= _experimentalPropertiesPanel.ApplyToCurrentItem();
                if (changed)
                {
                    _ = _viewModel.SaveAsync();
                    RebuildTree();
                    RefreshStatus();
                }

                LoadPropertiesPanel(GetCurrentItem());
            };
            _experimentalPropertiesPanel.LayoutStateChanged += (_, __) =>
            {
                if (_localStateRestored) SaveLocalState();
            };

            _nodeMenu.Opening += (_, e) =>
            {
                SyncSelectionFromTree();
                RefreshContextFolderMenuTexts();
                var current = GetCurrentItem();
                UpdateColorMenuChecks(current);
                UpdateNodeMenuEnabled(_nodeMenu.Items, current);
                var goToOriginal = _nodeMenu.Items.Find("GoToPinOriginal", false).FirstOrDefault();
                if (goToOriginal != null) goToOriginal.Visible = current?.IsPinItem == true;
            };
        }


        private void Tree_Collapsing(object sender, TreeViewAdvEventArgs e)
        {
            if (_refreshing || e?.Node == null)
                return;

            // A selected descendant would become invisible after this node is collapsed.
            // Move the selection to the collapsing node itself without rebuilding the tree.
            var containsSelectedDescendant = _tree.SelectedNodes.Any(selected =>
                selected != null && !ReferenceEquals(selected, e.Node) && IsDescendantOf(selected, e.Node));

            if (!containsSelectedDescendant)
                return;

            _selectingFromFind = true;
            try
            {
                _tree.ClearSelection();
                e.Node.IsSelected = true;
                _tree.SelectedNode = e.Node;
                _tree.EnsureVisible(e.Node);
            }
            finally
            {
                _selectingFromFind = false;
            }

            // SelectionChanged was intentionally suppressed above. Synchronize the model and
            // the per-tab selection cache directly; this operation must not rebuild the tree.
            SyncSelectionFromTree();
        }

        private static bool IsDescendantOf(TreeNodeAdv node, TreeNodeAdv ancestor)
        {
            for (var current = node.Parent; current != null; current = current.Parent)
            {
                if (ReferenceEquals(current, ancestor))
                    return true;
            }

            return false;
        }

        private void UpdateColorMenuChecks(DocumentItem current)
        {
            foreach (var pair in _colorMenuItems)
            {
                pair.Value.Checked = current != null && current.Color == pair.Key;
            }
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

        private string GetViewKey(object owner)
        {
            if (ReferenceEquals(owner, _setsOverviewExpansionOwner)) return "full-tree";
            return (owner as DocumentItem)?.Id ?? "";
        }

        private void RestoreLocalState()
        {
            var local = _viewModel.SolutionState;
            _showSetsOverview = string.Equals(local.ActiveViewId, "full-tree", StringComparison.OrdinalIgnoreCase);
            if (!_showSetsOverview)
            {
                var set = _viewModel.Sets.FirstOrDefault(x => string.Equals(x.Id, local.ActiveViewId, StringComparison.OrdinalIgnoreCase));
                if (set != null) _viewModel.SelectedSet = set;
                else _showSetsOverview = true;
            }

            if (Enum.TryParse(local.ActivationMode, out TreeActivationMode mode))
                _treeActivationMode = mode;
            _filterTextBox.Text = local.FilterText ?? string.Empty;
            _filterColors.Clear();
            foreach (var color in local.FilterColors ?? new List<BookmarkColor>()) _filterColors.Add(color);
            _propertiesVisible = local.PropertiesVisible;
            _contentSplit.Panel2Collapsed = !_propertiesVisible;
            _togglePropertiesButton.Checked = _propertiesVisible;
            _experimentalPropertiesPanel.ApplyLayoutState(
                local.PropertiesSectionOrder,
                local.ExpandedPropertiesSections);

            var byId = EnumerateItems(_viewModel.Root.Children).Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            _collapsedItemsByView.Clear();
            _selectedItemsByView.Clear();
            foreach (var pair in local.Views ?? new Dictionary<string, TreeViewLocalState>())
            {
                object owner = string.Equals(pair.Key, "full-tree", StringComparison.OrdinalIgnoreCase)
                    ? _setsOverviewExpansionOwner
                    : (object)_viewModel.Sets.FirstOrDefault(x => string.Equals(x.Id, pair.Key, StringComparison.OrdinalIgnoreCase));
                if (owner == null) continue;
                var viewState = pair.Value ?? new TreeViewLocalState();
                var collapsedIds = viewState.CollapsedIds ?? new List<string>();
                if (collapsedIds.Count == 0 && viewState.LegacyExpandedIds != null)
                {
                    var legacyExpanded = new HashSet<string>(viewState.LegacyExpandedIds, StringComparer.OrdinalIgnoreCase);
                    IEnumerable<DocumentItem> viewItems;
                    if (ReferenceEquals(owner, _setsOverviewExpansionOwner))
                        viewItems = EnumerateItems(_viewModel.Root.Children);
                    else
                        viewItems = EnumerateItems(((DocumentItem)owner).Children);

                    collapsedIds = viewItems
                        .Where(x => x != null && x.NodeType == NodeType.Folder && !legacyExpanded.Contains(x.Id))
                        .Select(x => x.Id)
                        .ToList();
                }

                _collapsedItemsByView[owner] = new HashSet<DocumentItem>(collapsedIds.Where(byId.ContainsKey).Select(id => byId[id]));
                _selectedItemsByView[owner] = new HashSet<DocumentItem>((viewState.SelectedIds ?? new List<string>()).Where(byId.ContainsKey).Select(id => byId[id]));
            }
            _localStateRestored = true;
            UpdateFilterColorUi();
            WireTreeActivationBehavior();
        }

        private void CaptureRenderedViewState()
        {
            if (_renderedExpansionOwner == null)
                return;

            var collapsedItems = new HashSet<DocumentItem>();
            foreach (var treeNode in EnumerateTreeNodes(_tree.Root))
            {
                var item = (treeNode.Tag as BookmarkTreeNode)?.Item;
                if (item != null && item.NodeType == NodeType.Folder && !treeNode.IsExpanded)
                    collapsedItems.Add(item);
            }
            _collapsedItemsByView[_renderedExpansionOwner] = collapsedItems;

            var selectedItems = new HashSet<DocumentItem>();
            foreach (var treeNode in _tree.SelectedNodes)
            {
                var item = (treeNode.Tag as BookmarkTreeNode)?.Item;
                if (item != null)
                    selectedItems.Add(item);
            }
            _selectedItemsByView[_renderedExpansionOwner] = selectedItems;
        }

        public void SaveLocalSettings()
        {
            CaptureRenderedViewState();
            SaveLocalState();
        }

        private void SaveLocalState()
        {
            if (!_localStateRestored || !_viewModel.IsLoaded) return;
            var local = _viewModel.SolutionState;
            local.ActiveViewId = _showSetsOverview ? "full-tree" : (_viewModel.SelectedSet?.Id ?? "full-tree");
            local.ActivationMode = _treeActivationMode.ToString();
            local.FilterText = _filterTextBox.Text ?? string.Empty;
            local.FilterColors = _filterColors.ToList();
            local.PropertiesVisible = _propertiesVisible;
            local.PropertiesSectionOrder = _experimentalPropertiesPanel.SectionOrder.ToList();
            local.ExpandedPropertiesSections = _experimentalPropertiesPanel.ExpandedSections.ToList();
            local.Views = new Dictionary<string, TreeViewLocalState>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in _collapsedItemsByView)
            {
                var key = GetViewKey(pair.Key); if (string.IsNullOrWhiteSpace(key)) continue;
                local.Views[key] = new TreeViewLocalState { CollapsedIds = pair.Value.Where(x => x != null).Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() };
            }
            foreach (var pair in _selectedItemsByView)
            {
                var key = GetViewKey(pair.Key); if (string.IsNullOrWhiteSpace(key)) continue;
                if (!local.Views.TryGetValue(key, out var view)) local.Views[key] = view = new TreeViewLocalState();
                view.SelectedIds = pair.Value.Where(x => x != null).Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            _viewModel.SaveSolutionState();
        }

        public void RefreshAll()
        {
            if (_viewModel.IsLoaded && !_localStateRestored)
                RestoreLocalState();

            _refreshing = true;
            try
            {
                _workspaceCombo.Items.Clear();
                foreach (var workspace in _viewModel.Workspaces)
                {
                    _workspaceCombo.Items.Add(workspace);
                }
                _workspaceCombo.SelectedItem = _viewModel.SelectedWorkspace;

                RefreshGroupsStrip();
            }
            finally { _refreshing = false; }
            RefreshToolbarItems();
            RebuildTree();
            RefreshStatus();
            RestoreSplitterPosition();
        }

        private void RefreshToolbarItems()
        {
            _undoButton.Enabled = _viewModel.UndoCommand.CanExecute(null);
            _redoButton.Enabled = _viewModel.RedoCommand.CanExecute(null);
        }


        private void RefreshGroupsStrip()
        {
            _groupsStrip.SuspendLayout();
            try
            {
                _standardGroupsStrip.Items.Clear();
                _groupsStrip.Items.Clear();

                var setsButton = new ToolStripButton("Full-Tree")
                {
                    Tag = SetsOverviewTag,
                    CheckOnClick = false,
                    Checked = _showSetsOverview,
                    DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                    AutoSize = true,
                    ToolTipText = "Управление наборами и их порядком",
                    Overflow = ToolStripItemOverflow.Never,
                    Image = IconProvider.Get(AppIcon.Folder),
                };
                setsButton.Click += (_, __) => SelectSetsOverview();
                _standardGroupsStrip.Items.Add(setsButton);


                AddStandardGroupButton(_viewModel.HistoryRoot, IconProvider.Get(AppIcon.Item));
                AddStandardGroupButton(_viewModel.PinRoot, IconProvider.Get(AppIcon.PinOverlay));

                foreach (var set in _viewModel.Sets.Where(x => x != null && x.NodeType == NodeType.Folder && !x.IsHistoryRoot && !x.IsPinRoot))
                {
                    var button = new ToolStripButton(set.Name)
                    {
                        Tag = set,
                        CheckOnClick = false,
                        Checked = !_showSetsOverview && ReferenceEquals(set, _viewModel.SelectedSet),
                        DisplayStyle = ToolStripItemDisplayStyle.Text,
                        AutoSize = true,
                        ToolTipText = set.Name,
                        Overflow = ToolStripItemOverflow.AsNeeded
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
                    Tag = AddGroupTag,
                    DisplayStyle = ToolStripItemDisplayStyle.Text,
                    ToolTipText = "Создать группу",
                    Overflow = ToolStripItemOverflow.Never
                };
                addButton.Click += (_, __) => Execute(_viewModel.AddSetCommand, null);
                _groupsStrip.Items.Add(addButton);
            }
            finally
            {
                _groupsStrip.ResumeLayout();
            }

            ApplyGroupsOverflowPolicy();
        }

        private ToolStripItem AddStandardGroupButton(DocumentItem set, Image icon=null, string toolTip = null)
        {
            if (set == null) return null;
            var button = new ToolStripButton(set.Name)
            {
                Tag = set,
                CheckOnClick = false,
                Checked = !_showSetsOverview && ReferenceEquals(set, _viewModel.SelectedSet),
                DisplayStyle = ToolStripItemDisplayStyle.ImageAndText,
                Image = icon != null ? icon: null,
                AutoSize = true,
                ToolTipText = toolTip ?? set.Name,
                Overflow = ToolStripItemOverflow.Never
            };
            button.Click += (_, __) => SelectSetFromButton(button);
            _standardGroupsStrip.Items.Add(button);
            return button;
        }

        private void SelectSetsOverview()
        {
            if (_refreshing)
                return;

            _showSetsOverview = true;
            _viewModel.SolutionState.ActiveViewId = "full-tree";
            _viewModel.SaveSolutionState();
            ClearFindResults();
            UpdateGroupButtonsChecked();
            RebuildTree();
            RefreshStatus();
        }

        private void SelectSetFromButton(ToolStripButton button)
        {
            if (_refreshing)
            {
                return;
            }

            if (!(button.Tag is DocumentItem set))
            {
                return;
            }

            _refreshing = true;
            try
            {
                _showSetsOverview = false;
                _viewModel.SelectedSet = set;
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
                .FirstOrDefault(x => x.Tag is DocumentItem set && ReferenceEquals(set, _viewModel.SelectedSet));

            if (button != null)
            {
                BeginRenameGroup(button);
                e.Handled = true;
            }
        }

        private void BeginRenameGroup(ToolStripButton button)
        {
            if (!(button.Tag is DocumentItem set))
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
                Width = Math.Max(button.Width + IconProvider.IconSize, Math.Max(90, textWidth)),
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

        private void ViewModel_TreeChanged(object sender, DocumentTreeChangedEventArgs e)
        {
            if (IsDisposed || e == null) return;
            if (InvokeRequired)
            {
                BeginInvoke(new Action(() => ViewModel_TreeChanged(sender, e)));
                return;
            }

            var rootStructureChanged = ReferenceEquals(e.OldParent, _viewModel.Root)
                || ReferenceEquals(e.NewParent, _viewModel.Root);
            var rootNameChanged = e.Kind == DocumentTreeChangeKind.PropertyChanged
                && e.PropertyName == nameof(DocumentItem.Name)
                && e.Item != null && e.Item.IsRootChild;

            if (rootNameChanged)
                BeginInvoke(new Action(RefreshGroupsStrip));

            if (e.Kind != DocumentTreeChangeKind.PropertyChanged)
            {
                if (rootStructureChanged) RefreshGroupsStrip();
                RebuildTree();
            }
            RefreshToolbarItems();
        }

        private void RebuildTree()
        {
            // Expansion and selection state belong to the displayed tab, not to the TreeView as a whole.
            // Save both states of the view that is currently rendered before rebuilding it.
            if (_renderedExpansionOwner != null)
            {
                CaptureRenderedViewState();
                SaveLocalState();
            }

            var expansionOwner = _showSetsOverview
                ? _setsOverviewExpansionOwner
                : (object)_viewModel.SelectedSet;

            var wasRefreshing = _refreshing;
            _refreshing = true;
            _tree.BeginUpdate();
            try
            {
                _treeModel.Nodes.Clear();
                if (_showSetsOverview)
                {
                    foreach (var item in _viewModel.Root.Children)
                    {
                        var node = BookmarkTreeNode.CreateFiltered(item, MatchesFilter);
                        if (node != null)
                            _treeModel.Nodes.Add(node);
                    }
                }
                else if (_viewModel.CurrentNodes != null)
                {
                    foreach (var item in _viewModel.CurrentNodes)
                    {
                        var node = BookmarkTreeNode.CreateFiltered(item, MatchesFilter);
                        if (node != null)
                            _treeModel.Nodes.Add(node);
                    }
                }

                // Nodes are expanded by default. Persist only the exceptions.
                _tree.ExpandAll();
                if (expansionOwner != null && _collapsedItemsByView.TryGetValue(expansionOwner, out var collapsedForView))
                {
                    foreach (var treeNode in EnumerateTreeNodes(_tree.Root))
                    {
                        var item = (treeNode.Tag as BookmarkTreeNode)?.Item;
                        if (item != null && collapsedForView.Contains(item))
                            treeNode.IsExpanded = false;
                    }
                }

                _renderedExpansionOwner = expansionOwner;
            }
            finally
            {
                _tree.EndUpdate();
                _refreshing = wasRefreshing;
            }
            RestoreSelectionForView(expansionOwner);
            LoadPropertiesPanel(GetCurrentItem());
        }

        internal System.Threading.Tasks.Task FindBookmarksFromEditorAsync()
        {
            return FindBookmarksInCurrentSetAsync();
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

            var pendingPropertyChange = _propertiesPanel.GetPendingChangeDescription();
            if (pendingPropertyChange != null) _viewModel.CaptureUndoState(pendingPropertyChange);
            var propertiesChanged = _propertiesPanel.ApplyToCurrentItem();
            var experimentalPendingPropertyChange = _experimentalPropertiesPanel.GetPendingChangeDescription();
            if (experimentalPendingPropertyChange != null) _viewModel.CaptureUndoState(experimentalPendingPropertyChange);
            propertiesChanged |= _experimentalPropertiesPanel.ApplyToCurrentItem();
            if (propertiesChanged)
            {
                _ = _viewModel.SaveAsync();
            }

            var items = _tree.SelectedNodes.Select(n => (n.Tag as BookmarkTreeNode)?.Item).Where(x => x != null).ToList();
            var selectionOwner = _showSetsOverview ? _setsOverviewExpansionOwner : (object)_viewModel.SelectedSet;
            if (selectionOwner != null)
            {
                _selectedItemsByView[selectionOwner] = new HashSet<DocumentItem>(items);
                SaveLocalState();
            }

            if (_showSetsOverview)
            {
                var selected = items.FirstOrDefault();
                var rootFolder = selected == null ? null : _viewModel.GetSetContainingNode(selected);
                if (rootFolder != null && !ReferenceEquals(_viewModel.SelectedSet, rootFolder))
                {
                    _viewModel.SelectedSet = rootFolder;
                    UpdateGroupButtonsChecked();
                }
            }

            _viewModel.SetSelectedNodes(items);
            LoadPropertiesPanel(items.FirstOrDefault());
        }

        private void LoadPropertiesPanel(DocumentItem item)
        {
            _propertiesSaveTimer.Stop();
            _experimentalPropertiesSaveTimer.Stop();
            CancelPreview(ref _previewCancellation);
            CancelPreview(ref _experimentalPreviewCancellation);
            var selectedCount = _tree.SelectedNodes.Count;
            var targets = GetSelectedPropertyTargets(item);
            var target = _viewModel.ResolvePin(item) ?? targets.FirstOrDefault();
            var allPinned = targets.Count > 0 && targets.All(_viewModel.IsPinned);
            var anyPinned = targets.Any(_viewModel.IsPinned);
            var canPin = targets.Count > 0 && targets.All(_viewModel.CanSetPinned);
            BookmarkColor? commonColor = null;
            if (targets.Count > 0 && targets.All(candidate => candidate.Color == targets[0].Color))
            {
                commonColor = targets[0].Color;
            }

            _propertiesPanel.LoadSelection(
                target,
                selectedCount > 1,
                allPinned,
                anyPinned,
                commonColor,
                canPin);
            _experimentalPropertiesPanel.LoadSelection(
                target,
                selectedCount > 1,
                allPinned,
                anyPinned,
                commonColor,
                canPin);
        }

        private async Task RefreshLivePreviewAsync()
        {
            if (_disposingProperties) return;
            var current = _propertiesPanel.CurrentItem;
            CancelPreview(ref _previewCancellation);
            var cancellation = new CancellationTokenSource();
            _previewCancellation = cancellation;
            _propertiesPanel.ShowLivePreviewLoading();

            try
            {
                var preview = await _viewModel.GetLivePreviewAsync(current, cancellation.Token);
                if (!cancellation.IsCancellationRequested && ReferenceEquals(current, _propertiesPanel.CurrentItem))
                {
                    _propertiesPanel.ShowLivePreview(preview);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException) when (_disposingProperties)
            {
            }
            finally
            {
                Interlocked.CompareExchange(ref _previewCancellation, null, cancellation);
                cancellation.Dispose();
            }
        }

        private async Task RefreshExperimentalLivePreviewAsync()
        {
            if (_disposingProperties) return;
            var current = _experimentalPropertiesPanel.CurrentItem;
            CancelPreview(ref _experimentalPreviewCancellation);
            var cancellation = new CancellationTokenSource();
            _experimentalPreviewCancellation = cancellation;
            _experimentalPropertiesPanel.ShowLivePreviewLoading();

            try
            {
                var preview = await _viewModel.GetLivePreviewAsync(current, cancellation.Token);
                if (!cancellation.IsCancellationRequested && ReferenceEquals(current, _experimentalPropertiesPanel.CurrentItem))
                {
                    _experimentalPropertiesPanel.ShowLivePreview(preview);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (ObjectDisposedException) when (_disposingProperties)
            {
            }
            finally
            {
                Interlocked.CompareExchange(ref _experimentalPreviewCancellation, null, cancellation);
                cancellation.Dispose();
            }
        }

        private static void CancelPreview(ref CancellationTokenSource source)
        {
            var cancellation = Interlocked.Exchange(ref source, null);
            if (cancellation == null) return;

            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private List<DocumentItem> GetSelectedPropertyTargets(DocumentItem fallback)
        {
            var selected = _tree.SelectedNodes
                .Select(node => (node.Tag as BookmarkTreeNode)?.Item)
                .Where(item => item != null)
                .ToList();
            if (selected.Count == 0 && fallback != null)
            {
                selected.Add(fallback);
            }

            return selected
                .Select(_viewModel.ResolvePin)
                .Where(item => item != null)
                .Distinct()
                .ToList();
        }

        private void GoToPinOriginal(DocumentItem pin)
        {
            if (pin?.IsPinItem != true)
            {
                return;
            }

            var target = _viewModel.ResolvePin(pin);
            if (target == null)
            {
                return;
            }

            var set = _viewModel.GetSetContainingNode(target);
            if (set != null && !ReferenceEquals(_viewModel.SelectedSet, set))
            {
                _showSetsOverview = false;
                _viewModel.SelectedSet = set;
                UpdateGroupButtonsChecked();
                RebuildTree();
            }

            _viewModel.SetSelectedNodes(new[] { target });
            SyncSelectionFromViewModel();
            LoadPropertiesPanel(target);
        }

        private void AddPropertiesPanelButton()
        {
            _togglePropertiesButton.CheckOnClick = true;
            _togglePropertiesButton.Checked = true;
            _togglePropertiesButton.DisplayStyle = ToolStripItemDisplayStyle.ImageAndText;
            _togglePropertiesButton.Image = IconProvider.Get(AppIcon.Properties, IconProvider.IconSize);
            _togglePropertiesButton.ToolTipText = "Показать или скрыть панель свойств";
            _togglePropertiesButton.Click += (_, __) => SetPropertiesPanelVisible(_togglePropertiesButton.Checked);
            _toolStrip.Items.Add(_togglePropertiesButton);
        }

        private void SetPropertiesPanelVisible(bool visible)
        {
            _propertiesVisible = visible;
            _contentSplit.Panel2Collapsed = !visible;
            _togglePropertiesButton.Checked = visible;
            if (_localStateRestored) SaveLocalState();
            if (visible)
            {
                RestoreSplitterPosition();
                LoadPropertiesPanel(GetCurrentItem());
            }
        }

        private void RestoreSelectionForView(object selectionOwner)
        {
            if (selectionOwner == null || !_selectedItemsByView.TryGetValue(selectionOwner, out var selectedItems))
            {
                SyncSelectionFromViewModel();
                return;
            }

            _selectingFromFind = true;
            try
            {
                _tree.ClearSelection();
                TreeNodeAdv firstSelected = null;

                foreach (var node in EnumerateTreeNodes(_tree.Root))
                {
                    var item = (node.Tag as BookmarkTreeNode)?.Item;
                    if (item == null)
                        continue;

                    if (!selectedItems.Contains(item))
                        continue;

                    node.IsSelected = true;
                    if (firstSelected == null)
                        firstSelected = node;
                }

                if (firstSelected != null)
                {
                    _tree.SelectedNode = firstSelected;
                    _tree.EnsureVisible(firstSelected);
                }

                if (!ReferenceEquals(selectionOwner, _setsOverviewExpansionOwner))
                    _viewModel.SetSelectedNodes(selectedItems);

                _tree.Invalidate();
            }
            finally
            {
                _selectingFromFind = false;
            }
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
            _viewModel.SolutionState.ActivationMode = mode.ToString();
            _viewModel.SaveSolutionState();
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

        private void SelectSet(DocumentItem set)
        {
            var button = FindGroupButton(set);
            if (button != null)
                SelectSetFromButton(button);
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
            if (_showSetsOverview && item != null && item.NodeType == NodeType.Folder && item.IsRootChild)
            {
                SelectSet(item);
                return;
            }
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

            var contextItem = (node?.Tag as BookmarkTreeNode)?.Item;
            if (_showSetsOverview && (contextItem == null || (contextItem.NodeType == NodeType.Folder && contextItem.IsRootChild)))
            {
                _groupMenu.Tag = contextItem;
                _groupMenu.Show(_tree, e.Location);
            }
            else
            {
                _nodeMenu.Show(_tree, e.Location);
            }
        }

        private async void Tree_DragDrop(object sender, DragEventArgs e)
        {
            var target = (_tree.DropPosition.Node?.Tag as BookmarkTreeNode)?.Item;
            var position = ConvertDropPosition(_tree.DropPosition.Position);
            var copy = IsCopyDrag(e);
            var fullTree = _showSetsOverview;

            if (copy)
            {
                if (_viewModel.CanCopySelectedNodesTo(target, position, fullTree))
                {
                    await _viewModel.CopySelectedNodesToAsync(target, position, fullTree);
                }
            }
            else if (_viewModel.CanMoveSelectedNodesTo(target, position, fullTree))
            {
                await _viewModel.MoveSelectedNodesToAsync(target, position, fullTree);
            }

            RebuildTree();
            RefreshGroupsStrip();
            RefreshStatus();
        }

        private void Tree_DragOver(object sender, DragEventArgs e)
        {
            var target = (_tree.DropPosition.Node?.Tag as BookmarkTreeNode)?.Item;
            var position = ConvertDropPosition(_tree.DropPosition.Position);
            var copy = IsCopyDrag(e);
            var fullTree = _showSetsOverview;
            var valid = copy
                ? _viewModel.CanCopySelectedNodesTo(target, position, fullTree)
                : _viewModel.CanMoveSelectedNodesTo(target, position, fullTree);

            e.Effect = valid
                ? (copy ? DragDropEffects.Copy : DragDropEffects.Move)
                : DragDropEffects.None;
        }

        private static bool IsCopyDrag(DragEventArgs e)
        {
            const int controlKeyState = 8;
            return (e.KeyState & controlKeyState) != 0;
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
            if (e.Control && e.KeyCode == Keys.Z)
            {
                Execute(_viewModel.UndoCommand, null);
                e.Handled = true;
                return;
            }
            if (e.Control && (e.KeyCode == Keys.Y || (e.Shift && e.KeyCode == Keys.Z)))
            {
                Execute(_viewModel.RedoCommand, null);
                e.Handled = true;
                return;
            }

            if (_showSetsOverview)
            {
                var item = GetCurrentItem();
                if (e.KeyCode == Keys.Enter)
                {
                    if (item != null && item.NodeType == NodeType.Folder && item.IsRootChild) SelectSet(item);
                    else if (item != null && item.Type != BookmarkType.Empty) Execute(_viewModel.OpenBookmarkCommand, item);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.F2)
                {
                    if (item != null && item.IsRootChild) _nameNode?.BeginEdit();
                    else Execute(_viewModel.RenameNodeCommand, item);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.Delete)
                {
                    if (item != null && item.IsRootChild) _viewModel.DeleteSetCommand.Execute(null);
                    else Execute(_viewModel.DeleteNodeCommand, item);
                    _showSetsOverview = true;
                    RefreshAll();
                    e.Handled = true;
                }
                else if (e.Control && (e.KeyCode == Keys.C || e.KeyCode == Keys.Insert))
                {
                    Execute(_viewModel.CopySelectedNodesCommand, item);
                    e.Handled = true;
                }
                else if ((e.Control && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert))
                {
                    Execute(_viewModel.PasteNodesCommand, item);
                    e.Handled = true;
                }
                return;
            }

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

        private void ColorNode_DrawText(object sender, DrawTextEventArgs e)
        {
            var node = e.Node?.Tag as BookmarkTreeNode;
            if (node == null || node.Item.Color == BookmarkColor.None)
            {
                e.Text = string.Empty;
                return;
            }

            e.TextColor = GetBookmarkColor(node.Item.Color);
        }


        private void NameNode_LabelChanged(object sender, LabelEventArgs e)
        {
            if (!_showSetsOverview || !(e.Subject is BookmarkTreeNode treeNode))
            {
                return;
            }

            var set = treeNode.Item;
            if (!set.IsRootChild || set.NodeType != NodeType.Folder)
            {
                return;
            }

            if (!_viewModel.TryRenameSet(set, e.NewLabel, showErrors: true))
            {
                treeNode.Name = e.OldLabel;
                return;
            }

            // ApplyChanges is called while the inline editor is still being closed.
            // Refresh on the next UI turn so the tab caption and combo are rebuilt safely.
            BeginInvoke(new Action(() =>
            {
                _showSetsOverview = true;
                RefreshAll();
            }));
        }

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
