namespace DocSets.Desktop.OneNote;

internal enum OneNoteDiagnosticMatch
{
    Exact,
    NotFound,
    Ambiguous,
    Wrong
}

internal sealed class OneNoteObjectIdDiagnosticReport
{
    public string Version { get; set; } = "1";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string NotebookName { get; set; } = "";
    public string NotebookId { get; set; } = "";
    public int TotalObjectLinks { get; set; }
    public int ResolvedByCom { get; set; }
    public int NotResolvedByCom { get; set; }
    public Dictionary<string, OneNoteDiagnosticMethodSummary> Methods { get; set; } = new();
    public List<OneNoteObjectIdDiagnosticEntry> Entries { get; set; } = new();
    public List<OneNoteObjectIdPageSummary> Pages { get; set; } = new();
}

internal sealed class OneNoteDiagnosticMethodSummary
{
    public int Exact { get; set; }
    public int NotFound { get; set; }
    public int Ambiguous { get; set; }
    public int Wrong { get; set; }
}

internal sealed class OneNoteObjectIdDiagnosticEntry
{
    public string SourcePageId { get; set; } = "";
    public string TargetPageId { get; set; } = "";
    public string OriginalHref { get; set; } = "";
    public string DecodedHref { get; set; } = "";
    public string LinkObjectId { get; set; } = "";
    public string CanonicalLinkObjectId { get; set; } = "";
    public List<string> LinkPositions { get; set; } = new();
    public string ResolvedSourceObjectId { get; set; } = "";
    public string CanonicalSourceObjectId { get; set; } = "";
    public List<string> SourcePositions { get; set; } = new();
    public string HtmlAnchorId { get; set; } = "";
    public string ComHyperlink { get; set; } = "";
    public string ComHyperlinkObjectId { get; set; } = "";
    public string XmlElementType { get; set; } = "";
    public string XmlElementName { get; set; } = "";
    public List<string> ParentElements { get; set; } = new();
    public int SiblingIndex { get; set; }
    public int PageIndex { get; set; }
    public string TextPreview { get; set; } = "";
    public Dictionary<string, OneNoteDiagnosticMatch> Matches { get; set; } = new();
    public Dictionary<string, List<string>> Candidates { get; set; } = new();
}

internal sealed class OneNoteObjectIdPageSummary
{
    public string PageId { get; set; } = "";
    public string PageName { get; set; } = "";
    public int XmlObjectIds { get; set; }
    public int UniqueCanonicalIds { get; set; }
    public int UniquePositions { get; set; }
    public Dictionary<string, List<string>> RepeatedPositions { get; set; } = new();
}

internal sealed class OneNoteDiagnosticProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = "";
}
