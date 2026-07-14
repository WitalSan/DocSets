using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class ModelTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void FlatStorageRoundTripPreservesTreeAndMetadata()
        {
            var created = new DateTimeOffset(2026, 7, 13, 20, 0, 0, TimeSpan.Zero);
            var modified = created.AddHours(2);
            var state = new DocumentSetsState();
            var set = Folder("Set");
            var folder = Folder("Folder");
            var bookmark = Bookmark("Method", "C:/a.cs", BookmarkType.Symbol);
            bookmark.Symbol = "A.B.Method";
            bookmark.Project = "Project";
            bookmark.Comment = "line 1\nline 2";
            bookmark.Color = BookmarkColor.Purple;
            bookmark.CreatedAtUtc = created;
            bookmark.ModifiedAtUtc = modified;
            bookmark.ModifiedInSolution = "SolutionA";
            bookmark.EditorState = new EditorState { CaretLineOffset = 3, CodePreview = "void M()",
                SymbolSnapshots = new List<SymbolSnapshot> { new SymbolSnapshot { Symbol = "A.B.Method", Signature = "void Method()", Comment = "Docs" } } };
            folder.Children.Add(bookmark);
            set.Children.Add(folder);
            state.Sets.Add(set);
            state.Sets.Add(new DocumentItem { Name = "runtime", NodeType = NodeType.Folder, IsLocalOnly = true });

            var json = JsonConvert.SerializeObject(state);
            var restored = JsonConvert.DeserializeObject<DocumentSetsState>(json);

            Assert.Equal(1, restored.Sets.Count);
            var restoredBookmark = restored.Sets[0].Children[0].Children[0];
            Assert.Equal("Method", restoredBookmark.Name);
            Assert.Equal("A.B.Method", restoredBookmark.Symbol);
            Assert.Equal(BookmarkColor.Purple, restoredBookmark.Color);
            Assert.Equal(created, restoredBookmark.CreatedAtUtc.Value);
            Assert.Equal(modified, restoredBookmark.ModifiedAtUtc.Value);
            Assert.Equal("SolutionA", restoredBookmark.ModifiedInSolution);
            Assert.Equal("void M()", restoredBookmark.EditorState.CodePreview);
            Assert.Equal("Docs", restoredBookmark.EditorState.SymbolSnapshots[0].Comment);
            Assert.Same(restored.Sets[0].Children[0], restoredBookmark.Parent);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void StorageOmitsSymbolFieldsForFileBookmarks()
        {
            var state = new DocumentSetsState();
            var set = Folder("Set");
            var file = Bookmark("File", "C:/a.cs", BookmarkType.File);
            file.Symbol = "Must.Not.Persist";
            file.Project = "MustNotPersist";
            set.Children.Add(file);
            state.Sets.Add(set);

            var restored = JsonConvert.DeserializeObject<DocumentSetsState>(JsonConvert.SerializeObject(state));
            var item = restored.Sets[0].Children[0];
            Assert.Equal(string.Empty, item.Symbol);
            Assert.Equal(string.Empty, item.Project);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void EnsureReadableIdsReplacesGuidsAndDuplicates()
        {
            var state = new DocumentSetsState();
            var first = Folder("My Set");
            var second = Folder("My Set");
            first.Id = Guid.NewGuid().ToString();
            second.Id = "duplicate";
            var child = Bookmark("Child Item", "a.cs", BookmarkType.File);
            child.Id = "duplicate";
            second.Children.Add(child);
            state.Sets.Add(first);
            state.Sets.Add(second);

            var migration = state.EnsureReadableIds();
            var ids = new[] { first.Id, second.Id, child.Id };

            Assert.Equal(3, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.False(ids.Any(id => Guid.TryParse(id, out _)));
            Assert.True(migration.Count >= 1);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RegenerateAllReadableIdsUsesNamesAndReturnsMigration()
        {
            var state = new DocumentSetsState();
            var set = Folder("Main Set");
            set.Id = "old-set";
            var child = Bookmark("Do Work", "a.cs", BookmarkType.Symbol);
            child.Id = "old-child";
            set.Children.Add(child);
            state.Sets.Add(set);

            var migration = state.RegenerateAllReadableIds();

            Assert.Equal("main-set", set.Id);
            Assert.Equal("do-work", child.Id);
            Assert.Equal("main-set", migration["old-set"]);
            Assert.Equal("do-work", migration["old-child"]);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CloneIsDeepAndPreservesBookmarkMetadata()
        {
            var item = Folder("Folder");
            var child = Bookmark("Child", "a.cs", BookmarkType.File);
            child.CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-2);
            child.ModifiedAtUtc = DateTimeOffset.UtcNow;
            child.ModifiedInSolution = "S";
            child.EditorState = new EditorState { SelectedText = "abc" };
            item.Children.Add(child);

            var clone = item.Clone();

            Assert.NotSame(item, clone);
            Assert.NotSame(child, clone.Children[0]);
            Assert.NotSame(child.EditorState, clone.Children[0].EditorState);
            Assert.Equal(child.CreatedAtUtc, clone.Children[0].CreatedAtUtc);
            Assert.Equal("S", clone.Children[0].ModifiedInSolution);
            clone.Children[0].Name = "Changed";
            Assert.Equal("Child", child.Name);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CommentFirstLineNormalizesLineEndings()
        {
            var item = new DocumentItem { Comment = "  first  \r\nsecond" };
            Assert.Equal("first", item.CommentFirstLine);
            item.Comment = "single";
            Assert.Equal("single", item.CommentFirstLine);
            item.Comment = "  ";
            Assert.Equal(string.Empty, item.CommentFirstLine);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LineAndColumnAreClampedToOne()
        {
            var item = new DocumentItem { Line = -10, Column = 0 };
            Assert.Equal(1, item.Line);
            Assert.Equal(1, item.Column);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void TreeChangedReportsPropertyAddMoveAndRemove()
        {
            var state = new DocumentSetsState();
            var events = new List<DocumentTreeChangeKind>();
            state.Root.TreeChanged += (_, e) => events.Add(e.Kind);
            var set = Folder("Set");
            state.Sets.Add(set);
            var one = Bookmark("One", "1.cs", BookmarkType.File);
            var two = Bookmark("Two", "2.cs", BookmarkType.File);
            set.Children.Add(one);
            set.Children.Add(two);
            one.Name = "Renamed";
            set.Children.Move(0, 1);
            set.Children.Remove(one);

            Assert.True(events.Contains(DocumentTreeChangeKind.Added));
            Assert.True(events.Contains(DocumentTreeChangeKind.PropertyChanged));
            Assert.True(events.Contains(DocumentTreeChangeKind.Moved));
            Assert.True(events.Contains(DocumentTreeChangeKind.Removed));
            Assert.Null(one.Parent);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LegacyNestedSetsAreMigrated()
        {
            var json = "{'sets':[{'name':'Legacy','items':[{'id':'folder','name':'Folder','isFolder':true},{'id':'item','parent':'folder','name':'Item','type':'File','path':'a.cs','line':1,'column':1}]}]}".Replace('\'', '"');
            var state = JsonConvert.DeserializeObject<DocumentSetsState>(json);
            Assert.Equal("Legacy", state.Sets[0].Name);
            Assert.Equal(NodeType.Folder, state.Sets[0].Children[0].NodeType);
            Assert.Equal("Item", state.Sets[0].Children[0].Children[0].Name);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SolutionLocalStateRoundTripPreservesRecentAndAccordionSettings()
        {
            var state = new SolutionLocalState
            {
                Workspace = "shared.docsets.json",
                RecentCurrentSolutionOnly = true,
                PropertiesSectionOrder = new List<string> { "properties", "preview", "code" },
                ExpandedPropertiesSections = new List<string> { "preview", "code" },
                FilterColors = new List<BookmarkColor> { BookmarkColor.Red, BookmarkColor.Blue }
            };
            var restored = JsonConvert.DeserializeObject<SolutionLocalState>(JsonConvert.SerializeObject(state));
            Assert.True(restored.RecentCurrentSolutionOnly);
            Assert.SequenceEqual(state.PropertiesSectionOrder, restored.PropertiesSectionOrder);
            Assert.SequenceEqual(state.ExpandedPropertiesSections, restored.ExpandedPropertiesSections);
            Assert.SequenceEqual(state.FilterColors, restored.FilterColors);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void UiSettingsClampPanelAndColumnWidths()
        {
            var ui = new DocumentSetsUiSettings { PropertiesPanelHeight = 1 };
            var column = new ColumnLayout { Width = 1 };
            Assert.Equal(70, ui.PropertiesPanelHeight);
            Assert.Equal(20, column.Width);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void EditorStateCloneIsIndependent()
        {
            var state = new EditorState { SelectedText = "before", CaretColumn = 7 };
            var clone = state.Clone();
            clone.SelectedText = "after";
            Assert.NotSame(state, clone);
            Assert.Equal("before", state.SelectedText);
            Assert.Equal(7, clone.CaretColumn);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void FlatItemWithMissingParentBecomesRoot()
        {
            var json = "{'items':[{'id':'orphan','parent':'missing','name':'Orphan','type':'File','path':'a.cs','line':1,'column':1}]}".Replace('\'', '"');
            var state = JsonConvert.DeserializeObject<DocumentSetsState>(json);
            Assert.Equal(1, state.Sets.Count);
            Assert.Equal("Orphan", state.Sets[0].Name);
        }

        private static DocumentItem Folder(string name) => new DocumentItem { Name = name, NodeType = NodeType.Folder, Type = BookmarkType.Empty };

        private static DocumentItem Bookmark(string name, string path, BookmarkType type) => new DocumentItem
        {
            Name = name,
            NodeType = NodeType.Item,
            Type = type,
            Path = path,
            Line = 10,
            Column = 2
        };
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void EveryBookmarkColorHasDisplayMetadata()
        {
            foreach (BookmarkColor color in Enum.GetValues(typeof(BookmarkColor)))
            {
                var field = typeof(BookmarkColor).GetField(color.ToString());
                var attribute = (BookmarkColorInfoAttribute)Attribute.GetCustomAttribute(field, typeof(BookmarkColorInfoAttribute));
                Assert.NotNull(attribute);
                Assert.False(string.IsNullOrWhiteSpace(attribute.Name));
                Assert.True(attribute.Red >= 0 && attribute.Red <= 255);
                Assert.True(attribute.Green >= 0 && attribute.Green <= 255);
                Assert.True(attribute.Blue >= 0 && attribute.Blue <= 255);
            }
        }
    }
}
