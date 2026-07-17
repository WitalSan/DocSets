using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class BookmarkSearchResultsControl : UserControl
    {
        private readonly ListView list = new ListView();
        private readonly Label status = new Label();
        private readonly Button showAll = new Button();
        private IReadOnlyList<BookmarkSearchResult> results = Array.Empty<BookmarkSearchResult>();
        private bool updating;
        private readonly Dictionary<string, bool> columnVisibility = new Dictionary<string, bool>
        {
            ["tree"] = true, ["bookmark"] = true, ["field"] = true, ["path"] = true, ["snippet"] = true
        };
        private int selectedResultIndex = -1;
        private int currentDisplayLimit = int.MaxValue;

        public event Action<int, BookmarkSearchResult> ResultActivated;
        public event EventHandler ShowAllRequested;

        public BookmarkSearchResultsControl(bool showAllButton)
        {
            Dock = DockStyle.Fill;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Margin = Padding.Empty };
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.HideSelection = false;
            list.MultiSelect = false;
            list.GridLines = false;
            BuildColumns();
            list.SelectedIndexChanged += (_, __) => ActivateSelected();
            list.ItemActivate += (_, __) => ActivateSelected();
            root.Controls.Add(list, 0, 0);

            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Padding = new Padding(3) };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            status.Dock = DockStyle.Fill;
            status.TextAlign = ContentAlignment.MiddleLeft;
            showAll.Text = "Показать все…";
            showAll.AutoSize = true;
            showAll.Visible = showAllButton;
            showAll.Click += (_, __) => ShowAllRequested?.Invoke(this, EventArgs.Empty);
            footer.Controls.Add(status, 0, 0);
            footer.Controls.Add(showAll, 1, 0);
            root.Controls.Add(footer, 0, 1);
            Controls.Add(root);
        }

        public bool IsColumnVisible(string key) => columnVisibility.TryGetValue(key, out var visible) && visible;

        public void SetColumnVisible(string key, bool visible)
        {
            if (!columnVisibility.ContainsKey(key) || columnVisibility[key] == visible) return;
            columnVisibility[key] = visible;
            BuildColumns();
            SetResults(results, selectedResultIndex, currentDisplayLimit);
        }
        private void BuildColumns()
        {
            list.Columns.Clear();
            if (columnVisibility["tree"]) list.Columns.Add("Путь в дереве", 210);
            if (columnVisibility["bookmark"]) list.Columns.Add("Закладка", 150);
            if (columnVisibility["field"]) list.Columns.Add("Где", 85);
            if (columnVisibility["path"]) list.Columns.Add("Path", 230);
            if (columnVisibility["snippet"]) list.Columns.Add("Фрагмент", 320);
        }

        private List<string> GetColumnValues(BookmarkSearchResult result)
        {
            var values = new List<string>();
            if (columnVisibility["tree"]) values.Add(result.TreePath ?? string.Empty);
            if (columnVisibility["bookmark"]) values.Add(result.Item?.Name ?? string.Empty);
            if (columnVisibility["field"]) values.Add(result.FieldName);
            if (columnVisibility["path"]) values.Add(result.Item?.Path ?? string.Empty);
            if (columnVisibility["snippet"]) values.Add(result.Snippet ?? string.Empty);
            return values;
        }
        public void SetResults(IReadOnlyList<BookmarkSearchResult> value, int selectedIndex, int displayLimit = int.MaxValue)
        {
            results = value ?? Array.Empty<BookmarkSearchResult>();
            selectedResultIndex = selectedIndex;
            currentDisplayLimit = displayLimit;
            updating = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                var count = Math.Min(results.Count, Math.Max(0, displayLimit));
                for (var index = 0; index < count; index++)
                {
                    var result = results[index];
                    var values = GetColumnValues(result);
                    var item = new ListViewItem(values.Count == 0 ? string.Empty : values[0]) { Tag = index };
                    for (var column = 1; column < values.Count; column++) item.SubItems.Add(values[column]);
                    list.Items.Add(item);
                }
                if (selectedIndex >= 0 && selectedIndex < list.Items.Count)
                {
                    list.Items[selectedIndex].Selected = true;
                    list.Items[selectedIndex].Focused = true;
                    list.EnsureVisible(selectedIndex);
                }
                status.Text = count < results.Count ? $"Показано {count} из {results.Count}" : $"Найдено: {results.Count}";
            }
            finally { list.EndUpdate(); updating = false; }
        }

        public void SelectResult(int selectedIndex, bool ensureVisible)
        {
            selectedResultIndex = selectedIndex;
            if (selectedIndex < 0 || selectedIndex >= list.Items.Count) return;
            updating = true;
            try
            {
                foreach (var item in list.SelectedItems.Cast<ListViewItem>().ToArray()) item.Selected = false;
                list.Items[selectedIndex].Selected = true;
                list.Items[selectedIndex].Focused = true;
                if (ensureVisible) list.EnsureVisible(selectedIndex);
            }
            finally { updating = false; }
        }

        public void FocusResults() { list.Focus(); }

        private void ActivateSelected()
        {
            if (updating || list.SelectedItems.Count == 0) return;
            var index = (int)list.SelectedItems[0].Tag;
            if (index >= 0 && index < results.Count) ResultActivated?.Invoke(index, results[index]);
        }
    }

    internal sealed class BookmarkSearchPanel : UserControl
    {
        private readonly TextBox query = new TextBox();
        private readonly ComboBox scope = new ComboBox();
        private readonly Button columnsButton = new Button { Text = "Колонки ▼", AutoSize = true };
        private readonly CheckedListBox columnsList = new CheckedListBox { CheckOnClick = true, BorderStyle = BorderStyle.None };
        private readonly ContextMenuStrip columnsDropDown = new ContextMenuStrip { AutoClose = true, ShowCheckMargin = true, ShowImageMargin = false, Padding = Padding.Empty };
        private bool suppressColumnsButtonClick;
        private readonly CheckBox names = new CheckBox { Text = "Названия", Checked = true, AutoSize = true };
        private readonly CheckBox symbols = new CheckBox { Text = "Символы", Checked = true, AutoSize = true };
        private readonly CheckBox comments = new CheckBox { Text = "Комментарии", Checked = true, AutoSize = true };
        private readonly CheckBox matchCase = new CheckBox { Text = "Aa", Appearance = Appearance.Button, AutoSize = true };
        private readonly CheckBox wholeWord = new CheckBox { Text = "|w|", Appearance = Appearance.Button, AutoSize = true };
        private readonly CheckBox regex = new CheckBox { Text = "Regex", Appearance = Appearance.Button, AutoSize = true };
        private readonly Button previous = new Button();
        private readonly Button next = new Button();
        private readonly Label searchIcon = new Label();
        private readonly ToolTip toolTip = new ToolTip();        public BookmarkSearchResultsControl Results { get; } = new BookmarkSearchResultsControl(false);
        public event EventHandler SearchChanged;
        public event EventHandler PreviousRequested;
        public event EventHandler NextRequested;

        public BookmarkSearchPanel()
        {
            Dock = DockStyle.Fill;
            var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(3) };
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var searchRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = Padding.Empty };
            searchIcon.Size = DpiService.Scale(this, new Size(24, 24));
            searchIcon.Margin = new Padding(0, 2, 2, 0);
            searchIcon.ImageAlign = ContentAlignment.MiddleCenter;
            toolTip.SetToolTip(searchIcon, "Найти");
            query.Width = DpiService.Scale(this, 230);
            query.Margin = new Padding(0, 3, 3, 0);
            previous.Size = DpiService.Scale(this, new Size(27, 27));
            previous.Margin = Padding.Empty;
            next.Size = DpiService.Scale(this, new Size(27, 27));
            next.Margin = Padding.Empty;
            var separator = new Label { Text = "|", AutoSize = true, Padding = new Padding(4, 5, 4, 0) };
            ApplyNavigationImages();
            previous.Click += (_, __) => PreviousRequested?.Invoke(this, EventArgs.Empty);
            next.Click += (_, __) => NextRequested?.Invoke(this, EventArgs.Empty);
            toolTip.SetToolTip(previous, "Предыдущий результат");
            toolTip.SetToolTip(next, "Следующий результат");
            toolTip.SetToolTip(matchCase, "Учитывать регистр");
            toolTip.SetToolTip(wholeWord, "Только целые слова");
            toolTip.SetToolTip(regex, "Регулярное выражение");
            searchRow.Controls.Add(searchIcon);
            searchRow.Controls.Add(query);
            searchRow.Controls.Add(previous);
            searchRow.Controls.Add(next);
            searchRow.Controls.Add(separator);
            searchRow.Controls.Add(matchCase);
            searchRow.Controls.Add(wholeWord);
            searchRow.Controls.Add(regex);

            var filterRow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            filterRow.Controls.Add(names);
            filterRow.Controls.Add(symbols);
            filterRow.Controls.Add(comments);
            scope.DropDownStyle = ComboBoxStyle.DropDownList;
            scope.Items.Add("В текущей группе");
            scope.Items.Add("Во всех группах");
            scope.SelectedIndex = 0;
            scope.Width = DpiService.Scale(this, 150);
            scope.Margin = new Padding(12, 1, 0, 0);
            filterRow.Controls.Add(scope);
            ConfigureColumnsDropDown();
            columnsButton.Margin = new Padding(6, 1, 0, 0);
            columnsButton.Click += ColumnsButton_Click;
            columnsButton.MouseDown += (_, __) =>
            {
                if (!columnsDropDown.Visible) return;
                suppressColumnsButtonClick = true;
                columnsDropDown.Close(ToolStripDropDownCloseReason.CloseCalled);
            };
            filterRow.Controls.Add(columnsButton);

            root.Controls.Add(searchRow, 0, 0);
            root.Controls.Add(filterRow, 0, 1);
            root.Controls.Add(Results, 0, 2);
            Controls.Add(root);

            query.AllowDrop = true;
            query.DragEnter += SearchDragEnter;
            query.DragOver += SearchDragEnter;
            query.DragDrop += SearchDragDrop;
            query.TextChanged += Changed;
            scope.SelectedIndexChanged += Changed;
            names.CheckedChanged += Changed;
            symbols.CheckedChanged += Changed;
            comments.CheckedChanged += Changed;
            matchCase.CheckedChanged += Changed;
            wholeWord.CheckedChanged += Changed;
            regex.CheckedChanged += Changed;
        }
        private static readonly string[] ColumnKeys = { "tree", "bookmark", "field", "path", "snippet" };
        private static readonly string[] ColumnCaptions = { "Путь в дереве", "Закладка", "Где", "Path", "Фрагмент" };

        private void ConfigureColumnsDropDown()
        {
            for (var index = 0; index < ColumnKeys.Length; index++)
            {
                var key = ColumnKeys[index];
                var item = new ToolStripMenuItem(ColumnCaptions[index])
                {
                    Checked = Results.IsColumnVisible(key),
                    CheckOnClick = true
                };
                item.CheckedChanged += (_, __) => Results.SetColumnVisible(key, item.Checked);
                columnsDropDown.Items.Add(item);
            }

            // Оставляем меню открытым, пока пользователь настраивает несколько колонок.
            columnsDropDown.Closing += (_, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.ItemClicked) e.Cancel = true;
            };
            columnsDropDown.Closed += (_, e) =>
            {
                if (e.CloseReason == ToolStripDropDownCloseReason.AppClicked &&
                    columnsButton.RectangleToScreen(columnsButton.ClientRectangle).Contains(Cursor.Position))
                    suppressColumnsButtonClick = true;
            };
        }
        private void ColumnsButton_Click(object sender, EventArgs e)
        {
            if (suppressColumnsButtonClick)
            {
                suppressColumnsButtonClick = false;
                return;
            }
            if (columnsDropDown.Visible)
            {
                columnsDropDown.Close();
                return;
            }
            columnsDropDown.Show(columnsButton, new Point(0, columnsButton.Height));
        }
        public string Query { get => query.Text ?? string.Empty; set { if (query.Text != value) query.Text = value ?? string.Empty; } }
        public BookmarkSearchRequest CreateRequest() => new BookmarkSearchRequest
        {
            Query = Query,
            Scope = scope.SelectedIndex == 1 ? BookmarkSearchScope.AllGroups : BookmarkSearchScope.CurrentGroup,
            SearchNames = names.Checked,
            SearchSymbolsAndPaths = symbols.Checked,
            SearchComments = comments.Checked,
            MatchCase = matchCase.Checked,
            MatchWholeWord = wholeWord.Checked,
            UseRegularExpressions = regex.Checked
        };
        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            ApplyNavigationImages();
        }

        private void ApplyNavigationImages()
        {
            previous.Image = IconProvider.Get(AppIcon.NvUp, this, 16);
            next.Image = IconProvider.Get(AppIcon.NvDown, this, 16);
            searchIcon.Image = IconProvider.Get(AppIcon.Find, this, 16);
        }
        public void EnsureSymbolsAndPathsEnabled() { symbols.Checked = true; }
        public void FocusQuery() { query.Focus(); query.SelectAll(); }
        internal static bool TryGetDroppedQuery(IDataObject data, out string value)
        {
            value = null;
            if (DocumentLinkService.TryGetLink(data, out var link))
                value = link.Kind == DocumentLinkKind.Symbol && !string.IsNullOrWhiteSpace(link.Target) ? link.Target : link.Caption ?? link.Target;
            else if (data?.GetDataPresent(DataFormats.UnicodeText) == true)
                value = data.GetData(DataFormats.UnicodeText) as string;
            else if (data?.GetDataPresent(DataFormats.Text) == true)
                value = data.GetData(DataFormats.Text) as string;

            value = (value ?? string.Empty).Trim();
            return value.Length > 0;
        }

        private void SearchDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = TryGetDroppedQuery(e.Data, out _) ? DragDropEffects.Copy : DragDropEffects.None;
        }

        private void SearchDragDrop(object sender, DragEventArgs e)
        {
            if (!TryGetDroppedQuery(e.Data, out var value)) return;
            Query = value;
            query.Focus();
            query.SelectAll();
        }

        private void Changed(object sender, EventArgs e) => SearchChanged?.Invoke(this, EventArgs.Empty);
    }
}