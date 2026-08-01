using System.Collections.Generic;

namespace DocSets.Desktop.OneNote;

internal sealed class OneNoteNotebook
{
    public OneNoteNotebook(string id, string name)
    {
        Id = id ?? "";
        Name = name ?? "";
    }

    public string Id { get; }
    public string Name { get; }
}

internal sealed class OneNoteImportProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = "";
}

internal sealed class OneNoteImportResult
{
    public DocumentItem Root { get; set; }
    public int Folders { get; set; }
    public int Pages { get; set; }
    public int Images { get; set; }
    public int FailedPages { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = new();
}
