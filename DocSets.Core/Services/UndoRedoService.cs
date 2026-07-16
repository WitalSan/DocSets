using System;
using System.Collections.Generic;
using System.Linq;

namespace DocSets
{
    internal sealed class UndoRedoService
    {
        private readonly int limit;
        private readonly List<Entry> undoEntries = new List<Entry>();
        private readonly List<Entry> redoEntries = new List<Entry>();

        public UndoRedoService(int limit = 100)
        {
            if (limit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit));
            }

            this.limit = limit;
        }

        public bool CanUndo => undoEntries.Count > 0;

        public bool CanRedo => redoEntries.Count > 0;

        public IReadOnlyList<string> UndoOperations => undoEntries
            .AsEnumerable()
            .Reverse()
            .Select(entry => entry.Description)
            .ToArray();

        public IReadOnlyList<string> RedoOperations => redoEntries
            .AsEnumerable()
            .Reverse()
            .Select(entry => entry.Description)
            .ToArray();

        public bool Capture(
            string description,
            string snapshot,
            IEnumerable<string> undoItemIds = null,
            IEnumerable<string> redoItemIds = null,
            string undoSetId = null,
            string redoSetId = null)
        {
            if (string.IsNullOrWhiteSpace(snapshot))
            {
                return false;
            }

            if (undoEntries.Count > 0 &&
                string.Equals(undoEntries[undoEntries.Count - 1].Snapshot, snapshot, StringComparison.Ordinal))
            {
                return false;
            }

            undoEntries.Add(new Entry(description, snapshot, undoItemIds, redoItemIds, undoSetId, redoSetId));
            TrimOldest(undoEntries);
            redoEntries.Clear();
            return true;
        }

        public bool TryUndo(string currentSnapshot, out string targetSnapshot)
        {
            return TryUndo(currentSnapshot, out targetSnapshot, out _, out _);
        }

        public bool TryUndo(string currentSnapshot, out string targetSnapshot, out IReadOnlyList<string> focusItemIds, out string focusSetId)
        {
            return TryTransfer(undoEntries, redoEntries, currentSnapshot, true, out targetSnapshot, out focusItemIds, out focusSetId);
        }

        public bool TryRedo(string currentSnapshot, out string targetSnapshot)
        {
            return TryRedo(currentSnapshot, out targetSnapshot, out _, out _);
        }

        public bool TryRedo(string currentSnapshot, out string targetSnapshot, out IReadOnlyList<string> focusItemIds, out string focusSetId)
        {
            return TryTransfer(redoEntries, undoEntries, currentSnapshot, false, out targetSnapshot, out focusItemIds, out focusSetId);
        }

        public void Clear()
        {
            undoEntries.Clear();
            redoEntries.Clear();
        }

        private bool TryTransfer(
            IList<Entry> source,
            IList<Entry> destination,
            string currentSnapshot,
            bool restoreUndoSide,
            out string targetSnapshot,
            out IReadOnlyList<string> focusItemIds,
            out string focusSetId)
        {
            targetSnapshot = null;
            focusItemIds = Array.Empty<string>();
            focusSetId = null;
            if (source.Count == 0 || string.IsNullOrWhiteSpace(currentSnapshot))
            {
                return false;
            }

            var lastIndex = source.Count - 1;
            var target = source[lastIndex];
            source.RemoveAt(lastIndex);
            destination.Add(target.WithSnapshot(currentSnapshot));
            TrimOldest(destination);
            targetSnapshot = target.Snapshot;
            focusItemIds = restoreUndoSide ? target.UndoItemIds : target.RedoItemIds;
            focusSetId = restoreUndoSide ? target.UndoSetId : target.RedoSetId;
            return true;
        }

        private void TrimOldest(IList<Entry> entries)
        {
            while (entries.Count > limit)
            {
                entries.RemoveAt(0);
            }
        }

        private sealed class Entry
        {
            public Entry(
                string description,
                string snapshot,
                IEnumerable<string> undoItemIds = null,
                IEnumerable<string> redoItemIds = null,
                string undoSetId = null,
                string redoSetId = null)
            {
                Description = string.IsNullOrWhiteSpace(description) ? "Изменение" : description;
                Snapshot = snapshot;
                UndoItemIds = NormalizeIds(undoItemIds);
                RedoItemIds = NormalizeIds(redoItemIds);
                UndoSetId = undoSetId;
                RedoSetId = redoSetId;
            }

            public string Description { get; }
            public string Snapshot { get; }
            public IReadOnlyList<string> UndoItemIds { get; }
            public IReadOnlyList<string> RedoItemIds { get; }
            public string UndoSetId { get; }
            public string RedoSetId { get; }

            public Entry WithSnapshot(string snapshot) =>
                new Entry(Description, snapshot, UndoItemIds, RedoItemIds, UndoSetId, RedoSetId);

            private static IReadOnlyList<string> NormalizeIds(IEnumerable<string> ids) =>
                (ids ?? Enumerable.Empty<string>())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
        }
    }
}
