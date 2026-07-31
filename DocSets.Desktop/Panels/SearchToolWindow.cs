namespace DocSets.Desktop.Panels;

internal sealed class SearchResult
{
    public DocumentItem Item { get; init; }
    public string Where { get; init; }
    public string Fragment { get; init; }
}

internal sealed class SearchToolWindow : DesktopDockContent
{
    private readonly ToolStrip toolbar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripTextBox query = new() { AutoSize = false, Width = 240 };
    private readonly ToolStripButton find = new("Найти");
    private readonly ListView results = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false
    };
    private Func<IEnumerable<DocumentItem>> itemsProvider;

    public SearchToolWindow() : base("search", "Поиск")
    {
        toolbar.Items.Add(new ToolStripLabel("Найти:"));
        toolbar.Items.Add(query);
        toolbar.Items.Add(find);
        results.Columns.Add("Закладка", 180);
        results.Columns.Add("Где", 90);
        results.Columns.Add("Файл", 220);
        results.Columns.Add("Фрагмент", 420);
        Controls.Add(results);
        Controls.Add(toolbar);
        toolbar.Dock = DockStyle.Top;
        find.Click += (_, __) => ExecuteSearch();
        query.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { ExecuteSearch(); e.SuppressKeyPress = true; } };
        results.DoubleClick += (_, __) => ActivateSelected();
        results.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) ActivateSelected(); };
    }

    public event EventHandler<SearchResult> ResultActivated;
    public void SetItemsProvider(Func<IEnumerable<DocumentItem>> provider) => itemsProvider = provider;
    public void FocusSearch() { Show(); query.Focus(); query.SelectAll(); }

    private void ExecuteSearch()
    {
        var text = query.Text.Trim();
        results.Items.Clear();
        if (text.Length == 0 || itemsProvider == null) return;
        foreach (var item in itemsProvider())
        {
            AddIfMatch(item, "Название", item.Name, text);
            AddIfMatch(item, "Заметка", StripHtml(item.Content), text);
            AddIfMatch(item, "Код", item.EditorState?.CodePreview ?? item.EditorState?.SelectedText, text);
            AddIfMatch(item, "Символ", item.Symbol, text);
            AddIfMatch(item, "Файл", item.Path, text);
        }
    }

    private void AddIfMatch(DocumentItem item, string where, string source, string queryText)
    {
        if (string.IsNullOrWhiteSpace(source)) return;
        var index = source.IndexOf(queryText, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) return;
        var start = Math.Max(0, index - 45);
        var length = Math.Min(source.Length - start, queryText.Length + 90);
        var fragment = source.Substring(start, length).Replace('\r', ' ').Replace('\n', ' ');
        var result = new SearchResult { Item = item, Where = where, Fragment = fragment };
        var row = new ListViewItem(item.Name ?? item.Id) { Tag = result };
        row.SubItems.Add(where);
        row.SubItems.Add(item.Path ?? "");
        row.SubItems.Add(fragment);
        results.Items.Add(row);
    }

    private void ActivateSelected()
    {
        if (results.SelectedItems.Count == 0) return;
        if (results.SelectedItems[0].Tag is SearchResult result) ResultActivated?.Invoke(this, result);
    }

    private static string StripHtml(string value) =>
        System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(value ?? "", "<[^>]+>", " "));
}
