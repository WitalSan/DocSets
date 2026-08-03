using System;
using System.Collections.Generic;

namespace DocSets.Desktop.OneNote;

internal sealed class OneNoteNotebook
{
    public OneNoteNotebook(string id, string name) { Id = id ?? ""; Name = name ?? ""; }
    public string Id { get; }
    public string Name { get; }
}

internal sealed class OneNoteImportProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public int OverallPercent { get; set; }
    public string Stage { get; set; } = "";
    public string Message { get; set; } = "";
    public OneNoteImportReport ReportSnapshot { get; set; }
    public OneNoteImportResult ResultSnapshot { get; set; }
    public string CompletedPageId { get; set; } = "";
    public string CompletedNodeId { get; set; } = "";
    public string ContentChecksum { get; set; } = "";
    public DateTimeOffset? OneNoteModifiedAtUtc { get; set; }
}

internal sealed class OneNoteImportResult
{
    public DocumentItem Root { get; set; }
    public int Folders { get; set; }
    public int Pages { get; set; }
    public int Images { get; set; }
    public int Attachments { get; set; }
    public int InternalLinks { get; set; }
    public int UnresolvedInternalLinks { get; set; }
    public int FailedPages { get; set; }
    public int NoteTags { get; set; }
    public bool Cancelled { get; set; }
    public bool LinkResolutionCompleted { get; set; }
    public List<string> Errors { get; } = new List<string>();
    public OneNoteImportReport Report { get; set; } = new OneNoteImportReport();
    public List<NoteTagStyle> NoteTagStyles { get; } = new List<NoteTagStyle>();
}
