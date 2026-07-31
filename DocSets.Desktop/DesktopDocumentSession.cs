using System.ComponentModel;

namespace DocSets.Desktop;

/// <summary>
/// Связывает существующий Desktop UI с общим workspace-сервисом.
/// После перехода Desktop на общую ViewModel этот адаптер будет удалён.
/// </summary>
internal sealed class DesktopDocumentSession
{
    private readonly DesktopSolutionContextService _solutionContext = new();
    private readonly IDocSetWorkspaceService _workspace;
    private DocumentSetsState _state;

    public DesktopDocumentSession()
    {
        _workspace = new DocSetWorkspaceService(_solutionContext);
    }

    public DocumentSetsState State => _state;
    public bool IsDirty { get; private set; }
    public string DirectoryPath => _workspace.ActiveDocSetDirectory;
    public string Name => _workspace.ActiveDocSetName;
    public string AssetDirectory => _workspace.AssetDirectory;

    public event EventHandler DocumentChanged;
    public event EventHandler DirtyChanged;

    public async Task OpenAsync(string directoryPath)
    {
        var fullPath = NormalizeDirectory(directoryPath);
        DetachItems();
        _solutionContext.SetForDocSet(fullPath);
        if (!await _workspace.OpenDocSetAsync(fullPath))
            throw new InvalidOperationException("Не удалось открыть DocSet: " + fullPath);
        _state = await _workspace.LoadAsync()
            ?? throw new InvalidDataException("DocSet не содержит состояния.");
        AttachItems();
        SetDirty(false);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CreateAsync(string directoryPath, string name)
    {
        var fullPath = NormalizeDirectory(directoryPath);
        DetachItems();
        _solutionContext.SetForDocSet(fullPath);
        if (!await _workspace.CreateDocSetAsync(fullPath, name))
            throw new InvalidOperationException("Не удалось создать DocSet: " + fullPath);
        _state = await _workspace.LoadAsync()
            ?? throw new InvalidDataException("Созданный DocSet не содержит состояния.");
        AttachItems();
        SetDirty(false);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task SaveAsync()
    {
        if (_state == null) return;
        await _workspace.SaveAsync(_state);
        SetDirty(false);
    }

    public void Close()
    {
        DetachItems();
        _state = null;
        SetDirty(false);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkDirty()
    {
        if (_state != null) SetDirty(true);
    }

    public IEnumerable<DocumentItem> AllItems()
    {
        if (_state == null) yield break;
        foreach (var root in _state.Sets)
            foreach (var item in Enumerate(root))
                yield return item;
    }

    public DocumentItem FindById(string id) => AllItems().FirstOrDefault(item =>
        string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task<string> SaveImageAsync(string base64, string mimeType, string originalName)
    {
        var content = Convert.FromBase64String(base64 ?? "");
        return await _workspace.SaveImageAssetAsync(content, mimeType, originalName);
    }

    public static string NormalizeDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Путь DocSet не задан.");
        var fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath)) fullPath = Path.GetDirectoryName(fullPath)!;
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static IEnumerable<DocumentItem> Enumerate(DocumentItem item)
    {
        yield return item;
        foreach (var child in item.Children)
            foreach (var descendant in Enumerate(child))
                yield return descendant;
    }

    private void AttachItems()
    {
        foreach (var item in AllItems()) item.PropertyChanged += ItemPropertyChanged;
    }

    private void DetachItems()
    {
        foreach (var item in AllItems()) item.PropertyChanged -= ItemPropertyChanged;
    }

    private void ItemPropertyChanged(object sender, PropertyChangedEventArgs e)
    {
        MarkDirty();
    }

    private void SetDirty(bool value)
    {
        if (IsDirty == value) return;
        IsDirty = value;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }
}
