using System.Collections.Generic;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class BreadcrumbToolTipTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void NestedComponentsUseTheirOwnSnapshots()
        {
            var item = Item(
                new SymbolSnapshot { Symbol = "DocSets.DocumentItem", Signature = "DocumentItem", Comment = "Item comment" },
                new SymbolSnapshot { Symbol = "DocSets.DocumentItem.Column", Signature = "int Column", Comment = "Column comment" });

            Assert.True(BreadcrumbToolTipBuilder.Build(item, "DocSets.DocumentItem").Contains("Item comment"));
            Assert.False(BreadcrumbToolTipBuilder.Build(item, "DocSets.DocumentItem").Contains("Column comment"));
            Assert.True(BreadcrumbToolTipBuilder.Build(item, "DocSets.DocumentItem.Column").Contains("Column comment"));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SnapshotSignatureAndCommentPrecedeBookmarkComment()
        {
            var item = Item(new SymbolSnapshot { Symbol = "A.Run", Signature = "void Run(int count)", Comment = "Runs work." });
            item.Content = "Bookmark note";
            var value = BreadcrumbToolTipBuilder.Build(item, "A.Run");
            Assert.True(value.Contains("void Run(int count)"));
            Assert.True(value.Contains("Runs work."));
            Assert.True(value.EndsWith("Bookmark note"));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void BookmarkCommentUsesTheSameOverviewAsTreeToolTip()
        {
            var item = Item(new SymbolSnapshot { Symbol = "A.Run", Signature = "void Run()" });
            item.ContentFormat = ContentFormat.Html;
            item.Content = "<h2>Заголовок</h2><script>скрытый текст</script><p>" +
                new string('x', BookmarkToolTipFormatter.MaximumCharactersPerLine * 6) + "</p>";

            var overview = BookmarkToolTipFormatter.Format(item.Content, item.ContentFormat);
            var value = BreadcrumbToolTipBuilder.Build(item, "A.Run");

            Assert.True(value.EndsWith(overview));
            Assert.False(value.Contains("<h2>"));
            Assert.False(value.Contains("скрытый текст"));
            Assert.AreEqual(
                BookmarkToolTipFormatter.MaximumLines,
                overview.Split(new[] { System.Environment.NewLine }, System.StringSplitOptions.None).Length);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CodePreviewIsNotUsedAsFallback()
        {
            var item = new DocumentItem { EditorState = new EditorState { CodePreview = "/// Hidden\r\nvoid Run(int count)" } };
            Assert.Equal(string.Empty, BreadcrumbToolTipBuilder.Build(item, "A.Run"));
        }

        private static DocumentItem Item(params SymbolSnapshot[] snapshots)
        {
            return new DocumentItem { EditorState = new EditorState { SymbolSnapshots = new List<SymbolSnapshot>(snapshots) } };
        }
    }
}
