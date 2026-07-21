using System.Collections.Generic;

namespace DocSets
{
    internal enum DocumentLinkKind { Symbol, Bookmark, File, Url }

    internal sealed class DocumentLink
    {
        public DocumentLinkKind Kind { get; set; }
        public string Caption { get; set; }
        public string Target { get; set; }
        public string Project { get; set; }
        public string SourceId { get; set; }
    }

    internal sealed class RenderedDocumentLink { public DocumentLink Link { get; set; } }
    internal sealed class DocumentLinkRenderResult
    {
        public List<RenderedDocumentLink> Links { get; } = new List<RenderedDocumentLink>();
    }

    internal static class DocumentLinkService
    {
        public static string ToMarkdown(DocumentLink link) => string.Empty;
        public static DocumentLinkRenderResult Render(string markdown) => new DocumentLinkRenderResult();
    }
}
