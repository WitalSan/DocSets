using System;
using System.Collections.Generic;
using System.Linq;

namespace DocSets
{
    internal sealed class RecentBookmarksService
    {
        private const int RecentLimit = 100;
        private DocumentSetsState state;

        public DocumentItem Root { get; private set; }

        public void Attach(DocumentSetsState documentState, DocumentItem insertAfter)
        {
            state = documentState ?? throw new ArgumentNullException(nameof(documentState));
            Root = state.Sets.FirstOrDefault(item => item != null && item.IsRecentRoot);
            if (Root == null)
            {
                Root = new DocumentItem
                {
                    Id = "recent",
                    Name = "Недавние",
                    NodeType = NodeType.Folder,
                    Type = BookmarkType.Empty,
                    IsLocalOnly = true,
                    IsRecentRoot = true
                };
                var previousIndex = insertAfter == null ? -1 : state.Sets.IndexOf(insertAfter);
                state.Sets.Insert(Math.Max(0, previousIndex + 1), Root);
            }

            Refresh();
        }

        public void Refresh()
        {
            if (Root == null || state == null) return;

            var recent = EnumerateNodes(state.Sets)
                .Where(IsRecentBookmark)
                .OrderByDescending(GetActivityDate)
                .Take(RecentLimit)
                .ToList();

            Root.Children.Clear();
            foreach (var target in recent)
            {
                Root.Children.Add(new DocumentItem
                {
                    Id = "recent-" + target.Id,
                    Name = FormatDisplayName(target),
                    NodeType = NodeType.Item,
                    Type = BookmarkType.Pin,
                    TargetId = target.Id,
                    IsLocalOnly = true,
                    IsRecentItem = true
                });
            }
        }

        public DocumentItem Resolve(DocumentItem item)
        {
            if (item == null || !item.IsRecentItem) return item;
            return EnumerateNodes(state?.Sets)
                .FirstOrDefault(candidate => candidate != null && !candidate.IsLocalOnly &&
                    string.Equals(candidate.Id, item.TargetId, StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsRecentBookmark(DocumentItem item)
        {
            return item != null && !item.IsLocalOnly && item.NodeType == NodeType.Item &&
                   (item.Type == BookmarkType.Symbol || item.Type == BookmarkType.File) &&
                   (item.ModifiedAtUtc.HasValue || item.CreatedAtUtc.HasValue);
        }

        private static DateTimeOffset GetActivityDate(DocumentItem item)
        {
            return item.ModifiedAtUtc ?? item.CreatedAtUtc ?? DateTimeOffset.MinValue;
        }

        private static string FormatDisplayName(DocumentItem item)
        {
            return string.IsNullOrWhiteSpace(item.ModifiedInSolution)
                ? item.Name ?? string.Empty
                : item.ModifiedInSolution + "." + (item.Name ?? string.Empty);
        }

        private static IEnumerable<DocumentItem> EnumerateNodes(IEnumerable<DocumentItem> nodes)
        {
            if (nodes == null) yield break;
            foreach (var node in nodes)
            {
                if (node == null) continue;
                yield return node;
                foreach (var child in EnumerateNodes(node.Children)) yield return child;
            }
        }
    }
}
