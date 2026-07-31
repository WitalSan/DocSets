namespace DocSets.Desktop.Panels;

internal sealed class NoteDocument : DesktopDockContent
{
    private readonly JoditCommentControl editor;
    private DocumentItem item;
    private bool loading;

    public NoteDocument() : base("note", "Заметка")
    {
        editor = new JoditCommentControl(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DocSets", "Desktop", "WebView2", "Jodit"));
        editor.Dock = DockStyle.Fill;
        Controls.Add(editor);
        editor.CommentChanged += (_, __) =>
        {
            if (loading || item == null) return;
            item.Content = editor.CommentText;
            ContentChanged?.Invoke(this, item);
        };
        editor.SaveRequested += (_, __) => SaveRequested?.Invoke(this, EventArgs.Empty);
        editor.EditingCompleted += (_, __) => CommitCurrent();
        editor.ImageInsertionRequested += async (data, mime, name, requestId) =>
        {
            if (SaveImageAsync == null) return;
            try
            {
                var asset = await SaveImageAsync(data, mime, name);
                editor.CompleteImage(asset, requestId);
            }
            catch (Exception exception)
            {
                editor.FailImage(requestId, exception.Message);
                DocSetsLog.Current.Error("Изображения", "Не удалось сохранить изображение заметки.", exception);
            }
        };
        editor.LinkActivated += target => LinkActivated?.Invoke(this, target);
    }

    public event EventHandler<DocumentItem> ContentChanged;
    public event EventHandler SaveRequested;
    public event EventHandler<string> LinkActivated;
    public Func<string, string, string, Task<string>> SaveImageAsync { get; set; }

    public void SetAssetDirectory(string path) => editor.SetAssetDirectory(path);

    public void SetItem(DocumentItem value)
    {
        CommitCurrent();
        item = value;
        loading = true;
        try
        {
            editor.Enabled = item != null;
            editor.LoadComment(item?.Content ?? "");
        }
        finally { loading = false; }
    }

    public void CommitCurrent()
    {
        if (item != null) item.Content = editor.CommentText;
    }

    public void ExecuteCommand(string command) => editor.ExecuteEditorCommand(command);
}
