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
        public static string ToMarkdown(DocumentLink link)
        {
            var target = link.Target;
            if (link.Kind == DocumentLinkKind.Symbol && !string.IsNullOrWhiteSpace(link.Project))
                target = link.Project + "|" + target;
            if (link.Kind == DocumentLinkKind.Symbol && !string.IsNullOrWhiteSpace(link.SourceId))
                target = link.SourceId + "|" + target;
            return "[" + link.Caption + "](" + link.Kind.ToString().ToLowerInvariant() + ":" + target + ")";
        }
        public static DocumentLinkRenderResult Render(string markdown) => new DocumentLinkRenderResult();
    }
}
