using ScintillaNET;
using System.Drawing;

namespace DocSets.Desktop.Panels;

internal sealed class CodeDocument : DesktopDockContent
{
    private readonly ToolStrip toolbar = new() { GripStyle = ToolStripGripStyle.Hidden };
    private readonly ToolStripLabel info = new();
    private readonly ToolStripTextBox search = new() { AutoSize = false, Width = 180 };
    private readonly ToolStripButton next = new("Найти далее");
    private readonly Scintilla editor = new() { Dock = DockStyle.Fill };
    private readonly Dictionary<string, int> firstVisibleLines = new(StringComparer.OrdinalIgnoreCase);
    private DocumentItem item;

    public CodeDocument() : base("code", "Код")
    {
        toolbar.Items.Add(info);
        toolbar.Items.Add(new ToolStripSeparator());
        toolbar.Items.Add(new ToolStripLabel("Найти:"));
        toolbar.Items.Add(search);
        toolbar.Items.Add(next);
        Controls.Add(editor);
        Controls.Add(toolbar);
        toolbar.Dock = DockStyle.Top;
        ConfigureEditor();
        next.Click += (_, __) => FindNext();
        search.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { FindNext(); e.SuppressKeyPress = true; } };
        editor.KeyDown += (_, e) =>
        {
            if (e.Control && e.KeyCode == Keys.F) { search.Focus(); search.SelectAll(); e.SuppressKeyPress = true; }
        };
    }

    public CodeViewMode ViewMode { get; private set; } = CodeViewMode.Snapshot;

    public void SetItem(DocumentItem value)
    {
        SaveScroll();
        item = value;
        var text = item?.EditorState?.CodePreview;
        if (string.IsNullOrEmpty(text)) text = item?.EditorState?.SelectedText;
        editor.ReadOnly = false;
        editor.Text = text ?? "";
        ConfigureLexer(Path.GetExtension(item?.Path ?? ""));
        editor.ReadOnly = true;
        info.Text = BuildInfo(item);
        if (item != null && firstVisibleLines.TryGetValue(item.Id ?? "", out var firstLine))
            editor.FirstVisibleLine = Math.Max(0, Math.Min(firstLine, Math.Max(0, editor.Lines.Count - 1)));
        else
            GoToSavedLine();
    }

    public void FindNext()
    {
        var value = search.Text;
        if (string.IsNullOrEmpty(value)) return;
        editor.TargetStart = Math.Max(editor.CurrentPosition, editor.SelectionEnd);
        editor.TargetEnd = editor.TextLength;
        editor.SearchFlags = SearchFlags.None;
        var position = editor.SearchInTarget(value);
        if (position < 0)
        {
            editor.TargetStart = 0;
            editor.TargetEnd = editor.TextLength;
            position = editor.SearchInTarget(value);
        }
        if (position < 0) return;
        editor.SetSelection(position + value.Length, position);
        editor.ScrollCaret();
        editor.Focus();
    }

    public void Copy() => editor.Copy();
    public void SelectAllText() => editor.SelectAll();

    private void ConfigureEditor()
    {
        editor.ReadOnly = true;
        editor.WrapMode = WrapMode.None;
        editor.TabWidth = 4;
        editor.UseTabs = false;
        editor.Styles[Style.Default].Font = "Consolas";
        editor.Styles[Style.Default].Size = 10;
        editor.StyleClearAll();
        editor.Margins[0].Type = MarginType.Number;
        editor.Margins[0].Width = 44;
        editor.CaretLineVisible = true;
        editor.CaretLineBackColor = Color.FromArgb(245, 248, 252);
    }

    private void ConfigureLexer(string extension)
    {
        extension = (extension ?? "").ToLowerInvariant();
        editor.Lexer = extension switch
        {
            ".cs" => Lexer.Cpp,
            ".json" => Lexer.Json,
            ".xml" or ".html" or ".htm" => Lexer.Html,
            ".css" => Lexer.Css,
            ".js" or ".ts" => Lexer.Cpp,
            ".sql" => Lexer.Sql,
            ".md" => Lexer.Markdown,
            _ => Lexer.Null
        };
        if (extension == ".cs") ConfigureCSharpStyles();
    }

    private void ConfigureCSharpStyles()
    {
        editor.SetKeywords(0, "abstract as base bool break byte case catch char checked class const continue decimal " +
            "default delegate do double else enum event explicit extern false finally fixed float for foreach goto " +
            "if implicit in int interface internal is lock long namespace new null object operator out override params " +
            "private protected public readonly ref return sbyte sealed short sizeof stackalloc static string struct " +
            "switch this throw true try typeof uint ulong unchecked unsafe ushort using virtual void volatile while " +
            "async await record init required var dynamic get set value yield where partial global");
        editor.Styles[Style.Cpp.Comment].ForeColor = Color.Green;
        editor.Styles[Style.Cpp.CommentLine].ForeColor = Color.Green;
        editor.Styles[Style.Cpp.CommentDoc].ForeColor = Color.Green;
        editor.Styles[Style.Cpp.Number].ForeColor = Color.DarkCyan;
        editor.Styles[Style.Cpp.Word].ForeColor = Color.Blue;
        editor.Styles[Style.Cpp.Word].Bold = true;
        editor.Styles[Style.Cpp.String].ForeColor = Color.Brown;
        editor.Styles[Style.Cpp.Character].ForeColor = Color.Brown;
        editor.Styles[Style.Cpp.Preprocessor].ForeColor = Color.Purple;
    }

    private void GoToSavedLine()
    {
        if (item == null || editor.Lines.Count == 0) return;
        var line = Math.Max(0, Math.Min(
            item.Line - 1 + (item.EditorState?.CaretLineOffset ?? 0),
            editor.Lines.Count - 1));
        editor.GotoPosition(editor.Lines[line].Position);
        editor.ScrollCaret();
    }

    private void SaveScroll()
    {
        if (item != null && !string.IsNullOrWhiteSpace(item.Id))
            firstVisibleLines[item.Id] = editor.FirstVisibleLine;
    }

    private static string BuildInfo(DocumentItem value)
    {
        if (value == null) return "Элемент не выбран";
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(value.Symbol)) parts.Add("Symbol: " + value.Symbol);
        if (!string.IsNullOrWhiteSpace(value.Path)) parts.Add("File: " + value.Path);
        if (value.Line > 0) parts.Add("Position: " + value.Line + ":" + Math.Max(1, value.Column));
        parts.Add("Source: Snapshot");
        return string.Join("    ", parts);
    }
}
