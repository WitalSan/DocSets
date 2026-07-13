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

        public bool Capture(string description, string snapshot)
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

            undoEntries.Add(new Entry(description, snapshot));
            TrimOldest(undoEntries);
            redoEntries.Clear();
            return true;
        }

        public bool TryUndo(string currentSnapshot, out string targetSnapshot)
        {
            return TryTransfer(undoEntries, redoEntries, currentSnapshot, out targetSnapshot);
        }

        public bool TryRedo(string currentSnapshot, out string targetSnapshot)
        {
            return TryTransfer(redoEntries, undoEntries, currentSnapshot, out targetSnapshot);
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
            out string targetSnapshot)
        {
            targetSnapshot = null;
            if (source.Count == 0 || string.IsNullOrWhiteSpace(currentSnapshot))
            {
                return false;
            }

            var lastIndex = source.Count - 1;
            var target = source[lastIndex];
            source.RemoveAt(lastIndex);
            destination.Add(new Entry(target.Description, currentSnapshot));
            TrimOldest(destination);
            targetSnapshot = target.Snapshot;
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
            public Entry(string description, string snapshot)
            {
                Description = string.IsNullOrWhiteSpace(description) ? "Изменение" : description;
                Snapshot = snapshot;
            }

            public string Description { get; }

            public string Snapshot { get; }
        }
    }
}
