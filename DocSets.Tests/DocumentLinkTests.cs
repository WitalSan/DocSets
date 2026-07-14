namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class DocumentLinkTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SerializesEverySupportedLinkKind()
        {
            Assert.Equal("[Run](symbol:A.B.Run)", DocumentLinkService.ToMarkdown(new DocumentLink { Kind = DocumentLinkKind.Symbol, Caption = "Run", Target = "A.B.Run" }));
            Assert.Equal("[Model.cs](file:src/Model.cs)", DocumentLinkService.ToMarkdown(new DocumentLink { Kind = DocumentLinkKind.File, Caption = "Model.cs", Target = "src/Model.cs" }));
            Assert.Equal("[Bookmark](bookmark:item-id)", DocumentLinkService.ToMarkdown(new DocumentLink { Kind = DocumentLinkKind.Bookmark, Caption = "Bookmark", Target = "item-id" }));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RendersLinksAndMarkdownWithoutBreakingOffsets()
        {
            var result = DocumentLinkService.Render("# Header\r\n**See** [Run](symbol:A.B.Run) and `code`");
            Assert.Equal("Header\nSee Run and code", result.Text);
            Assert.Equal(1, result.Links.Count);
            Assert.Equal("Run", result.Text.Substring(result.Links[0].Start, result.Links[0].Length));
            Assert.Equal("A.B.Run", result.Links[0].Link.Target);
            Assert.Equal(2, result.Styles.Count);
            Assert.Equal(MarkdownStyle.Bold, result.Styles[0].Style);
            Assert.Equal(MarkdownStyle.Code, result.Styles[1].Style);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ShorthandCreatesSymbolLink()
        {
            var result = DocumentLinkService.Render("Call [[A.B.Run]]");
            Assert.Equal("Call A.B.Run", result.Text);
            Assert.Equal(DocumentLinkKind.Symbol, result.Links[0].Link.Kind);
            Assert.Equal("A.B.Run", result.Links[0].Link.Target);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SymbolProjectRoundTrips()
        {
            var markdown = DocumentLinkService.ToMarkdown(new DocumentLink { Kind = DocumentLinkKind.Symbol, Caption = "Run", Target = "A.B.Run", Project = "ProjectA" });
            Assert.Equal("[Run](symbol:ProjectA|A.B.Run)", markdown);
            var link = DocumentLinkService.Render(markdown).Links[0].Link;
            Assert.Equal("ProjectA", link.Project); Assert.Equal("A.B.Run", link.Target);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CustomDragPayloadRoundTrips()
        {
            var source = new DocumentLink { Kind = DocumentLinkKind.Bookmark, Caption = "Bookmark", Target = "item-id" };
            Assert.True(DocumentLinkService.TryGetLink(DocumentLinkService.CreateDataObject(source), out var restored));
            Assert.Equal(DocumentLinkKind.Bookmark, restored.Kind); Assert.Equal("item-id", restored.Target);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ExternalUrlLinkRendersAndRoundTrips()
        {
            var markdown = "[SERVICESW-1344](https://jira.example/browse/SERVICESW-1344)";
            var rendered = DocumentLinkService.Render(markdown);
            Assert.Equal("SERVICESW-1344", rendered.Text);
            Assert.Equal(DocumentLinkKind.Url, rendered.Links[0].Link.Kind);
            Assert.Equal("https://jira.example/browse/SERVICESW-1344", rendered.Links[0].Link.Target);
            Assert.Equal(markdown, DocumentLinkService.ToMarkdown(rendered.Links[0].Link));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RenderProvidesPreviewToMarkdownCaretMap()
        {
            var markdown = "first [SERVICESW-1344](https://jira/browse/SERVICESW-1344) last";
            var rendered = DocumentLinkService.Render(markdown);
            Assert.Equal(markdown.IndexOf("first", System.StringComparison.Ordinal), rendered.PreviewToSource[rendered.Text.IndexOf("first", System.StringComparison.Ordinal)]);
            Assert.Equal(markdown.IndexOf("SERVICESW-1344", System.StringComparison.Ordinal), rendered.PreviewToSource[rendered.Text.IndexOf("SERVICESW-1344", System.StringComparison.Ordinal)]);
            Assert.Equal(markdown.IndexOf("last", System.StringComparison.Ordinal), rendered.PreviewToSource[rendered.Text.IndexOf("last", System.StringComparison.Ordinal)]);
        }
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DroppedUrlBecomesExternalLink()
        {
            var data = new System.Windows.Forms.DataObject();
            data.SetData(System.Windows.Forms.DataFormats.UnicodeText, "https://jira.example/browse/SERVICESW-1344");
            Assert.True(DocumentLinkService.TryGetLink(data, out var link));
            Assert.Equal(DocumentLinkKind.Url, link.Kind);
            Assert.Equal("https://jira.example/browse/SERVICESW-1344", link.Target);
        }
    }
}
