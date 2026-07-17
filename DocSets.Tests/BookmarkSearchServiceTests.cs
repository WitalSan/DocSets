using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class BookmarkSearchServiceTests
    {
        [TestMethod]
        public void CurrentGroupDoesNotReturnMatchesFromOtherGroups()
        {
            var current = Group("Current", Bookmark("First", "needle here"));
            var other = Group("Other", Bookmark("Second", "needle there"));
            var results = Search("needle", BookmarkSearchScope.CurrentGroup, new[] { current, other }, current);

            Assert.Equal(1, results.Count);
            Assert.Equal("First", results[0].Item.Name);
            Assert.Same(current, results[0].Group);
        }

        [TestMethod]
        public void AllGroupsSearchesNestedItemsAndSkipsAutomaticGroups()
        {
            var nested = Bookmark("Nested", "find me");
            var folder = new DocumentItem { Name = "Folder", NodeType = NodeType.Folder };
            folder.Children.Add(nested);
            var stored = Group("Stored", folder);
            var recent = Group("Recent", Bookmark("Recent item", "find me"));
            recent.IsRecentRoot = true;
            recent.IsLocalOnly = true;

            var results = Search("find", BookmarkSearchScope.AllGroups, new[] { stored, recent }, stored);

            Assert.Equal(1, results.Count);
            Assert.Same(nested, results[0].Item);
            Assert.Equal("Stored \u203a Folder", results[0].TreePath);
        }

        [TestMethod]
        public void CommentSearchReturnsEveryOccurrenceAndExactOffsets()
        {
            var item = Bookmark("Item", "one NEEDLE two needle");
            var group = Group("Group", item);
            var results = Search("needle", BookmarkSearchScope.CurrentGroup, new[] { group }, group);

            Assert.Equal(2, results.Count);
            Assert.SequenceEqual(new[] { 4, 15 }, results.Select(x => x.MatchStart));
            Assert.True(results.All(x => x.Field == BookmarkSearchField.Comment));
        }

        [TestMethod]
        public void MaximumResultsIsRespected()
        {
            var group = Group("Group", Bookmark("needle", "needle needle"));
            var request = new BookmarkSearchRequest { Query = "needle", MaximumResults = 2 };
            var results = new BookmarkSearchService().Search(request, new[] { group }, group);
            Assert.Equal(2, results.Count);
        }

        [TestMethod]
        public void MatchCaseAndWholeWordAreApplied()
        {
            var group = Group("Group", Bookmark("Item", "Word word wording"));
            var service = new BookmarkSearchService();
            var request = new BookmarkSearchRequest
            {
                Query = "Word",
                SearchNames = false,
                SearchSymbolsAndPaths = false,
                SearchComments = true,
                MatchCase = true,
                MatchWholeWord = true
            };

            var results = service.Search(request, new[] { group }, group);
            Assert.Equal(1, results.Count);
            Assert.Equal(0, results[0].MatchStart);
        }

        [TestMethod]
        public void RegularExpressionReturnsActualMatchSpans()
        {
            var group = Group("Group", Bookmark("Item", "AB-12 and CD-345"));
            var request = new BookmarkSearchRequest
            {
                Query = @"[A-Z]{2}-\d+",
                SearchNames = false,
                SearchSymbolsAndPaths = false,
                SearchComments = true,
                UseRegularExpressions = true
            };

            var results = new BookmarkSearchService().Search(request, new[] { group }, group);
            Assert.Equal(2, results.Count);
            Assert.SequenceEqual(new[] { "AB-12", "CD-345" }, results.Select(x => x.Item.Comment.Substring(x.MatchStart, x.MatchLength)));
        }

        [TestMethod]
        public void InvalidRegularExpressionReturnsNoResults()
        {
            var group = Group("Group", Bookmark("Item", "text"));
            var request = new BookmarkSearchRequest { Query = "(", UseRegularExpressions = true };
            Assert.Equal(0, new BookmarkSearchService().Search(request, new[] { group }, group).Count);
        }
        private static IReadOnlyList<BookmarkSearchResult> Search(string query, BookmarkSearchScope scope, IEnumerable<DocumentItem> groups, DocumentItem current)
        {
            return new BookmarkSearchService().Search(new BookmarkSearchRequest
            {
                Query = query,
                Scope = scope,
                SearchNames = false,
                SearchSymbolsAndPaths = false,
                SearchComments = true
            }, groups, current);
        }

        private static DocumentItem Group(string name, params DocumentItem[] children)
        {
            var group = new DocumentItem { Name = name, NodeType = NodeType.Folder };
            foreach (var child in children) group.Children.Add(child);
            return group;
        }

        private static DocumentItem Bookmark(string name, string comment)
        {
            return new DocumentItem { Name = name, Comment = comment, Type = BookmarkType.Symbol };
        }
    }
}