using System;
using System.Collections.Generic;
using System.Linq;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class NavigationHistoryServiceTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void AttachCreatesLocalRootAndRestoresItems()
        {
            var state = new DocumentSetsState();
            state.Sets.Add(Folder("Stored"));
            var saved = new NavigationHistoryLocalItem
            {
                Id = "h1", Name = "Visited", Type = BookmarkType.Symbol, Symbol = "A.B", Path = "a.cs", Line = 8,
                Comment = "comment", EditorState = new EditorState { CodePreview = "code" }
            };
            var service = new NavigationHistoryService();
            service.Attach(state, new[] { saved });

            Assert.Same(service.Root, state.Sets[0]);
            Assert.True(service.Root.IsHistoryRoot);
            Assert.True(service.Root.IsLocalOnly);
            Assert.Equal(1, service.Root.Children.Count);
            Assert.True(service.Root.Children[0].IsHistoryItem);
            Assert.Equal("code", service.Root.Children[0].EditorState.CodePreview);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RecordDeduplicatesConsecutiveLocationsAndMovesRevisitedToFront()
        {
            var service = Attached();
            var a = Bookmark("A", "a.cs");
            var b = Bookmark("B", "b.cs");
            Assert.True(service.Record(a, _ => false, DateTime.Now.AddMinutes(-2)));
            Assert.False(service.Record(a, _ => false, DateTime.Now.AddMinutes(-1)));
            Assert.True(service.Record(b, _ => false, DateTime.Now));
            Assert.True(service.Record(a, _ => false, DateTime.Now));
            Assert.Equal(2, service.Root.Children.Count);
            Assert.Equal("A", service.Root.Children[0].Name);
            Assert.Equal("B", service.Root.Children[1].Name);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SuppressNextSkipsOnlyMatchingLocationOnce()
        {
            var service = Attached();
            var a = Bookmark("A", "a.cs");
            service.SuppressNext(a);
            Assert.False(service.Record(a, _ => false, DateTime.Now));
            service.ResetCurrentLocation();
            Assert.True(service.Record(a, _ => false, DateTime.Now));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SymbolKeyUsesProjectAndSymbolWhileFileKeyUsesPath()
        {
            var symbol = Bookmark("S", "ignored.cs"); symbol.Symbol = "A.B"; symbol.Project = "P";
            Assert.Equal("P|A.B", NavigationHistoryService.GetKey(symbol));
            var file = Bookmark("F", "C:/f.cs");
            Assert.Equal("C:/f.cs", NavigationHistoryService.GetKey(file));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void HistoryLimitRemovesOldestUnpinnedEntry()
        {
            var service = Attached();
            for (var i = 0; i < 2001; i++)
                service.Record(Bookmark("B" + i, i + ".cs"), item => item.Path == "0.cs", DateTime.Now.AddSeconds(i));

            Assert.Equal(2000, service.Root.Children.Count);
            Assert.True(service.Root.Children.Any(item => item.Path == "0.cs"));
            Assert.False(service.Root.Children.Any(item => item.Path == "1.cs"));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void HistoryMayExceedLimitWhenEveryEntryIsPinned()
        {
            var service = Attached();
            for (var i = 0; i < 2001; i++)
                service.Record(Bookmark("B" + i, i + ".cs"), _ => true, DateTime.Now.AddSeconds(i));
            Assert.Equal(2001, service.Root.Children.Count);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ExportRoundTripsEditorStateAndVisitedTime()
        {
            var service = Attached();
            var item = Bookmark("A", "a.cs"); item.EditorState = new EditorState { SelectedText = "x" };
            var visited = new DateTime(2026, 7, 14, 1, 2, 3);
            service.Record(item, _ => false, visited);
            var exported = service.Export().Single();
            Assert.Equal(visited, exported.VisitedAt);
            Assert.Equal("x", exported.EditorState.SelectedText);
            Assert.NotSame(item.EditorState, exported.EditorState);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ReattachingAutomaticGroupsAfterSnapshotRestoreDoesNotDuplicateRoots()
        {
            var state = new DocumentSetsState();
            var stored = Folder("Stored"); stored.Id = "stored";
            var bookmark = Bookmark("Bookmark", "a.cs"); bookmark.Id = "bookmark";
            bookmark.CreatedAtUtc = DateTimeOffset.UtcNow;
            stored.Children.Add(bookmark);
            state.Sets.Add(stored);
            var historyItems = new[] { new NavigationHistoryLocalItem { Id = "history-item", Name = "History item", Path = "h.cs", Type = BookmarkType.File } };
            var pins = new[] { new PinLocalItem { Id = "pin-item", TargetId = "bookmark" } };

            for (var iteration = 0; iteration < 6; iteration++)
            {
                state = Newtonsoft.Json.JsonConvert.DeserializeObject<DocumentSetsState>(
                    Newtonsoft.Json.JsonConvert.SerializeObject(state));
                var history = new NavigationHistoryService(); history.Attach(state, historyItems);
                var recent = new RecentBookmarksService(); recent.Attach(state, history.Root);
                var pin = new PinService(); pin.Attach(state, pins, recent.Root);

                Assert.Equal(1, state.Sets.Count(x => x.IsHistoryRoot));
                Assert.Equal(1, state.Sets.Count(x => x.IsRecentRoot));
                Assert.Equal(1, state.Sets.Count(x => x.IsPinRoot));
                Assert.Equal(1, state.Sets.Count(x => x.Id == "stored"));
                Assert.Equal(4, state.Sets.Count);
                Assert.Equal(1, history.Root.Children.Count);
                Assert.Equal(1, pin.Root.Children.Count);
            }
        }
        private static NavigationHistoryService Attached()
        {
            var service = new NavigationHistoryService();
            service.Attach(new DocumentSetsState(), null);
            return service;
        }

        private static DocumentItem Folder(string name) => new DocumentItem { Name = name, NodeType = NodeType.Folder, Type = BookmarkType.Empty };
        private static DocumentItem Bookmark(string name, string path) => new DocumentItem { Name = name, NodeType = NodeType.Item, Type = BookmarkType.File, Path = path, Line = 1, Column = 1 };
    }

    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class PinServiceTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void AttachCreatesRootAfterRequestedAutomaticGroup()
        {
            var state = new DocumentSetsState();
            var history = Folder("History"); history.IsLocalOnly = true; history.IsHistoryRoot = true;
            var recent = Folder("Recent"); recent.IsLocalOnly = true; recent.IsRecentRoot = true;
            state.Sets.Add(history); state.Sets.Add(recent); state.Sets.Add(Folder("Stored"));
            var service = new PinService();
            service.Attach(state, null, recent);
            Assert.Same(service.Root, state.Sets[2]);
            Assert.True(service.Root.IsPinRoot);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ToggleAddsResolvesAndRemovesPin()
        {
            var state = new DocumentSetsState();
            var set = Folder("Set"); var target = Bookmark("Target", "target"); target.Id = "target";
            set.Children.Add(target); state.Sets.Add(set);
            var service = new PinService(); service.Attach(state, null, null);
            service.Toggle(target);
            var pin = service.Root.Children.Single();
            Assert.True(pin.IsPinItem);
            Assert.True(service.IsPinned(target));
            Assert.Same(target, service.Resolve(pin));
            service.Toggle(pin);
            Assert.Equal(0, service.Root.Children.Count);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ToggleExistingTargetRemovesItsPin()
        {
            var state = new DocumentSetsState(); var set = Folder("Set"); var target = Bookmark("T", "t"); target.Id = "t";
            set.Children.Add(target); state.Sets.Add(set); var service = new PinService(); service.Attach(state, null, null);
            service.Toggle(target); service.Toggle(target);
            Assert.False(service.IsPinned(target));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RemoveTargetsAndIdMigrationMaintainReferences()
        {
            var state = new DocumentSetsState(); var service = new PinService();
            service.Attach(state, new[] { new PinLocalItem { Id = "p1", TargetId = "old" }, new PinLocalItem { Id = "p2", TargetId = "remove" } }, null);
            service.ApplyIdMigration(new Dictionary<string, string> { ["old"] = "new" });
            Assert.Equal("new", service.Root.Children[0].TargetId);
            service.RemoveTargets(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "remove" });
            Assert.SequenceEqual(new[] { "new" }, service.Export().Select(x => x.TargetId));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void AutomaticRootsCannotBePinned()
        {
            var service = new PinService(); service.Attach(new DocumentSetsState(), null, null);
            var history = Folder("H"); history.IsHistoryRoot = true;
            var recent = Folder("R"); recent.IsRecentRoot = true;
            Assert.False(service.CanToggle(service.Root));
            Assert.False(service.CanToggle(history));
            Assert.False(service.CanToggle(recent));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void MissingPinTargetResolvesToNull()
        {
            var service = new PinService();
            service.Attach(new DocumentSetsState(), new[] { new PinLocalItem { Id = "p", TargetId = "missing" } }, null);
            Assert.Null(service.Resolve(service.Root.Children[0]));
        }

        private static DocumentItem Folder(string name) => new DocumentItem { Name = name, NodeType = NodeType.Folder, Type = BookmarkType.Empty };
        private static DocumentItem Bookmark(string name, string id) => new DocumentItem { Name = name, Id = id, NodeType = NodeType.Item, Type = BookmarkType.File, Path = name + ".cs" };
    }

    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class RecentBookmarksServiceTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void AttachCreatesRootAfterHistoryAndBuildsProxies()
        {
            var state = StateWithSet(out var set);
            var history = Folder("History"); history.IsHistoryRoot = true; history.IsLocalOnly = true; state.Sets.Insert(0, history);
            var target = RecentTarget("One", "one", DateTimeOffset.UtcNow, "SolutionA"); set.Children.Add(target);
            var service = new RecentBookmarksService(); service.Attach(state, history);
            Assert.Same(service.Root, state.Sets[1]);
            var proxy = service.Root.Children.Single();
            Assert.True(proxy.IsRecentItem);
            Assert.True(proxy.IsLocalOnly);
            Assert.Equal("SolutionA.One", proxy.Name);
            Assert.Equal("one", proxy.TargetId);
            Assert.Same(target, service.Resolve(proxy));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RefreshSortsByModifiedThenCreatedAndExcludesUnsupportedNodes()
        {
            var state = StateWithSet(out var set);
            var old = RecentTarget("Old", "old", DateTimeOffset.UtcNow.AddDays(-2), "S");
            var newest = RecentTarget("Newest", "new", DateTimeOffset.UtcNow, "S");
            var createdOnly = RecentTarget("Created", "created", DateTimeOffset.UtcNow.AddDays(-1), "S"); createdOnly.ModifiedAtUtc = null;
            var noDate = RecentTarget("NoDate", "none", DateTimeOffset.UtcNow, "S"); noDate.CreatedAtUtc = null; noDate.ModifiedAtUtc = null;
            var empty = RecentTarget("Empty", "empty", DateTimeOffset.UtcNow.AddDays(1), "S"); empty.Type = BookmarkType.Empty;
            set.Children.Add(old); set.Children.Add(noDate); set.Children.Add(createdOnly); set.Children.Add(empty); set.Children.Add(newest);
            var service = new RecentBookmarksService(); service.Attach(state, null);
            Assert.SequenceEqual(new[] { "new", "created", "old" }, service.Root.Children.Select(x => x.TargetId));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RefreshLimitsResultToOneHundred()
        {
            var state = StateWithSet(out var set);
            var start = DateTimeOffset.UtcNow.AddHours(-200);
            for (var i = 0; i < 125; i++) set.Children.Add(RecentTarget("B" + i, "id" + i, start.AddHours(i), "S"));
            var service = new RecentBookmarksService(); service.Attach(state, null);
            Assert.Equal(100, service.Root.Children.Count);
            Assert.Equal("id124", service.Root.Children[0].TargetId);
            Assert.Equal("id25", service.Root.Children[99].TargetId);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RefreshReflectsRenameSolutionAndDeletionWithoutStoredRecentList()
        {
            var state = StateWithSet(out var set); var target = RecentTarget("Before", "id", DateTimeOffset.UtcNow, "A"); set.Children.Add(target);
            var service = new RecentBookmarksService(); service.Attach(state, null);
            target.Name = "After"; target.ModifiedInSolution = "B"; service.Refresh();
            Assert.Equal("B.After", service.Root.Children.Single().Name);
            set.Children.Remove(target); service.Refresh();
            Assert.Equal(0, service.Root.Children.Count);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void MissingTargetResolvesToNull()
        {
            var state = StateWithSet(out _); var service = new RecentBookmarksService(); service.Attach(state, null);
            var proxy = new DocumentItem { IsRecentItem = true, TargetId = "missing" };
            Assert.Null(service.Resolve(proxy));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void EmptySolutionDoesNotPrefixDisplayName()
        {
            var state = StateWithSet(out var set);
            set.Children.Add(RecentTarget("Name", "id", DateTimeOffset.UtcNow, string.Empty));
            var service = new RecentBookmarksService(); service.Attach(state, null);
            Assert.Equal("Name", service.Root.Children.Single().Name);
        }

        private static DocumentSetsState StateWithSet(out DocumentItem set)
        {
            var state = new DocumentSetsState(); set = Folder("Set"); set.Id = "set"; state.Sets.Add(set); return state;
        }

        private static DocumentItem RecentTarget(string name, string id, DateTimeOffset modified, string solution) => new DocumentItem
        {
            Id = id, Name = name, NodeType = NodeType.Item, Type = BookmarkType.File, Path = name + ".cs",
            CreatedAtUtc = modified.AddHours(-1), ModifiedAtUtc = modified, ModifiedInSolution = solution
        };
        private static DocumentItem Folder(string name) => new DocumentItem { Name = name, NodeType = NodeType.Folder, Type = BookmarkType.Empty };
    }
}
