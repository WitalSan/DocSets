using System.Collections.Generic;
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

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
    public int Attachments { get; set; }
    public int InternalLinks { get; set; }
    public int UnresolvedInternalLinks { get; set; }
    public int FailedPages { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = new();
    public OneNoteImportReport Report { get; set; } = new();
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum OneNoteImportStatus
{
    Imported,
    ImportedWithWarnings,
    ConvertedWithLoss,
    NotImported
}

internal sealed class OneNoteImportReportEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ObjectType { get; set; } = "";
    public string Name { get; set; } = "";
    public OneNoteImportStatus Status { get; set; }
    public string Reason { get; set; } = "";
    public string OneNotePageId { get; set; } = "";
    public string OneNoteObjectId { get; set; } = "";
    public string OneNoteLink { get; set; } = "";
    public string DocSetsNodeId { get; set; } = "";
    public string DocSetsAnchorId { get; set; } = "";
    public bool IsAggregate { get; set; }
    public List<string> RelatedProblemIds { get; set; } = new();
}

internal sealed class OneNoteImportReport
{
    public int Version { get; set; } = 1;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string NotebookName { get; set; } = "";
    public string NotebookId { get; set; } = "";
    public string ImportedRootNodeId { get; set; } = "";
    public List<OneNoteImportReportEntry> Entries { get; set; } = new();

    public int Count(OneNoteImportStatus status) => Entries.Count(entry => entry.Status == status);
    public int Problems => Entries.Count(entry => entry.Status != OneNoteImportStatus.Imported);
    public int PrimaryProblems => Entries.Count(entry =>
        entry.Status != OneNoteImportStatus.Imported && !entry.IsAggregate);
}
