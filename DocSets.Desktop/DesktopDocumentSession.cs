using System.ComponentModel;
using System.Security.Cryptography;

namespace DocSets.Desktop;

internal sealed class DesktopDocumentSession
{
    private readonly IDocSetStore store = new DirectoryDocSetStore();
    private readonly IDocSetDocumentRepository repository = new DocSetDocumentRepository();

    public DocSetDocument Document { get; private set; }
    public bool IsDirty { get; private set; }
    public string DirectoryPath => Document?.DirectoryPath ?? "";
    public string Name => Document?.Manifest?.Name ?? "";
    public string AssetDirectory => string.IsNullOrWhiteSpace(DirectoryPath)
        ? ""
        : Path.Combine(DirectoryPath, "assets");

    public event EventHandler DocumentChanged;
    public event EventHandler DirtyChanged;

    public async Task OpenAsync(string directoryPath)
    {
        DetachItems();
        Document = await repository.OpenAsync(NormalizeDirectory(directoryPath));
        AttachItems();
        SetDirty(false);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task CreateAsync(string directoryPath, string name)
    {
        var fullPath = NormalizeDirectory(directoryPath);
        await store.CreateAsync(fullPath, CreateId(name), name);
        await OpenAsync(fullPath);
    }

    public async Task SaveAsync()
    {
        if (Document == null) return;
        await repository.SaveAsync(Document);
        SetDirty(false);
    }

    public void Close()
    {
        DetachItems();
        Document = null;
        SetDirty(false);
        DocumentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MarkDirty()
    {
        if (Document != null) SetDirty(true);
    }

    public IEnumerable<DocumentItem> AllItems()
    {
        if (Document == null) yield break;
        foreach (var root in Document.State.Sets)
            foreach (var item in Enumerate(root))
                yield return item;
    }

    public DocumentItem FindById(string id) => AllItems().FirstOrDefault(item =>
        string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task<string> SaveImageAsync(string base64, string mime, string originalName)
    {
        var bytes = Convert.FromBase64String(base64 ?? "");
        var extension = MimeExtension(mime, originalName);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var relative = "images/" + hash + extension;
        var path = Path.Combine(AssetDirectory, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, bytes);
        return "asset:" + relative;
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

    private void ItemPropertyChanged(object sender, PropertyChangedEventArgs e) => MarkDirty();

    private void SetDirty(bool value)
    {
        if (IsDirty == value) return;
        IsDirty = value;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string CreateId(string name)
    {
        var value = new string((name ?? "docset").Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) ? character : '-')
            .ToArray()).Trim('-');
        return string.IsNullOrWhiteSpace(value) ? "docset-" + Guid.NewGuid().ToString("N") : value;
    }

    private static string MimeExtension(string mime, string originalName)
    {
        var extension = Path.GetExtension(originalName ?? "");
        if (!string.IsNullOrWhiteSpace(extension) && extension.Length <= 8) return extension.ToLowerInvariant();
        return (mime ?? "").ToLowerInvariant() switch
        {
            "image/jpeg" => ".jpg",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            _ => ".png"
        };
    }
}
