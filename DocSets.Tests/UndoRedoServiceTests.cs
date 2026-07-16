using System;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class UndoRedoServiceTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ConstructorRejectsInvalidLimit()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new UndoRedoService(0));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CaptureRejectsBlankAndDuplicateSnapshots()
        {
            var service = new UndoRedoService();
            Assert.False(service.Capture("blank", " "));
            Assert.True(service.Capture("one", "A"));
            Assert.False(service.Capture("duplicate", "A"));
            Assert.SequenceEqual(new[] { "one" }, service.UndoOperations);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void UndoRedoTransfersSnapshotsAndDescriptions()
        {
            var service = new UndoRedoService();
            service.Capture("first", "A");
            service.Capture("second", "B");
            Assert.True(service.TryUndo("C", out var target));
            Assert.Equal("B", target);
            Assert.True(service.CanRedo);
            Assert.SequenceEqual(new[] { "second" }, service.RedoOperations);
            Assert.True(service.TryRedo("B-current", out target));
            Assert.Equal("C", target);
            Assert.True(service.CanUndo);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void UndoRedoReturnsAffectedItemsForRestoredSide()
        {
            var service = new UndoRedoService();
            service.Capture("change", "before", new[] { "old-item" }, new[] { "new-item" }, "old-set", "new-set");

            Assert.True(service.TryUndo("after", out var snapshot, out var itemIds, out var setId));
            Assert.Equal("before", snapshot);
            Assert.SequenceEqual(new[] { "old-item" }, itemIds);
            Assert.Equal("old-set", setId);

            Assert.True(service.TryRedo("before-current", out snapshot, out itemIds, out setId));
            Assert.Equal("after", snapshot);
            Assert.SequenceEqual(new[] { "new-item" }, itemIds);
            Assert.Equal("new-set", setId);
        }
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CommentUndoRedoSequenceDoesNotCreateExtraOperationsOrNodes()
        {
            var service = new UndoRedoService();
            var before = "{\"nodes\":[{\"id\":\"a\",\"comment\":\"\"},{\"id\":\"b\",\"comment\":\"\"}]}";
            var after = "{\"nodes\":[{\"id\":\"a\",\"comment\":\"text\"},{\"id\":\"b\",\"comment\":\"\"}]}";
            service.Capture("comment", before, new[] { "a" }, new[] { "a" }, "set", "set");

            Assert.True(service.TryUndo(after, out var snapshot, out var ids, out _));
            Assert.Equal(before, snapshot);
            Assert.SequenceEqual(new[] { "a" }, ids);
            Assert.False(service.CanUndo);
            Assert.True(service.CanRedo);

            Assert.True(service.TryRedo(before, out snapshot, out ids, out _));
            Assert.Equal(after, snapshot);
            Assert.SequenceEqual(new[] { "a" }, ids);
            Assert.True(service.CanUndo);
            Assert.False(service.CanRedo);

            Assert.True(service.TryUndo(after, out snapshot));
            Assert.False(service.TryUndo(before, out _));
            Assert.True(service.TryRedo(before, out snapshot));
            Assert.Equal(after, snapshot);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(snapshot, "\\\"id\\\"").Count);
        }
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void NewCaptureClearsRedoHistory()
        {
            var service = new UndoRedoService();
            service.Capture("A", "a"); service.Capture("B", "b");
            service.TryUndo("c", out _);
            Assert.True(service.CanRedo);
            service.Capture("D", "d");
            Assert.False(service.CanRedo);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LimitTrimsOldestEntries()
        {
            var service = new UndoRedoService(2);
            service.Capture("A", "a"); service.Capture("B", "b"); service.Capture("C", "c");
            Assert.SequenceEqual(new[] { "C", "B" }, service.UndoOperations);
            Assert.True(service.TryUndo("d", out var target)); Assert.Equal("c", target);
            Assert.True(service.TryUndo("c", out target)); Assert.Equal("b", target);
            Assert.False(service.TryUndo("b", out _));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void BlankDescriptionUsesFallbackName()
        {
            var service = new UndoRedoService();
            service.Capture(null, "a");
            Assert.SequenceEqual(new[] { "Изменение" }, service.UndoOperations);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ClearResetsBothStacks()
        {
            var service = new UndoRedoService(); service.Capture("A", "a"); service.TryUndo("b", out _);
            service.Clear();
            Assert.False(service.CanUndo); Assert.False(service.CanRedo);
        }
    }
}
