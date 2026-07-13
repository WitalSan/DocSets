using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DocSets
{
    internal sealed class NavigationHistoryService
    {
        private const int HistoryLimit = 2000;
        private DocumentSetsState state;
        private string lastLocationKey = string.Empty;
        private string suppressedLocationKey = string.Empty;

        public DocumentItem Root { get; private set; }

        public void Attach(DocumentSetsState documentState, IEnumerable<NavigationHistoryLocalItem> savedItems)
        {
            state = documentState ?? throw new ArgumentNullException(nameof(documentState));
            Root = state.Sets.FirstOrDefault(item => item != null && item.IsHistoryRoot);
            if (Root == null)
            {
                Root = new DocumentItem
                {
                    Id = "history",
                    Name = "History",
                    NodeType = NodeType.Folder,
                    Type = BookmarkType.Empty,
                    IsLocalOnly = true,
                    IsHistoryRoot = true
                };
                state.Sets.Insert(0, Root);
            }

            Root.Children.Clear();
            foreach (var saved in savedItems ?? Enumerable.Empty<NavigationHistoryLocalItem>())
            {
                Root.Children.Add(new DocumentItem
                {
                    Id = saved.Id ?? string.Empty,
                    Name = saved.Name ?? string.Empty,
                    NodeType = NodeType.Item,
                    Type = saved.Type,
                    Symbol = saved.Symbol ?? string.Empty,
                    Project = saved.Project ?? string.Empty,
                    Path = saved.Path ?? string.Empty,
                    Line = saved.Line,
                    Column = saved.Column,
                    Comment = saved.Comment ?? string.Empty,
                    EditorState = saved.EditorState?.Clone(),
                    IsLocalOnly = true,
                    IsHistoryItem = true
                });
            }

            lastLocationKey = string.Empty;
            suppressedLocationKey = string.Empty;
        }

        public void ResetCurrentLocation()
        {
            lastLocationKey = string.Empty;
        }

        public void SuppressNext(DocumentItem item)
        {
            suppressedLocationKey = GetKey(item);
        }

        public bool Record(DocumentItem source, Func<DocumentItem, bool> isPinned, DateTime visitedAt)
        {
            if (Root == null || source == null)
            {
                return false;
            }

            var key = GetKey(source);
            if (string.IsNullOrWhiteSpace(key) ||
                string.Equals(lastLocationKey, key, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            lastLocationKey = key;
            if (string.Equals(suppressedLocationKey, key, StringComparison.OrdinalIgnoreCase))
            {
                suppressedLocationKey = string.Empty;
                return false;
            }

            suppressedLocationKey = string.Empty;
            var existing = Root.Children.FirstOrDefault(item =>
                string.Equals(GetKey(item), key, StringComparison.OrdinalIgnoreCase));
            var item = existing ?? new DocumentItem
            {
                Id = CreateReadableId(source.Name),
                NodeType = NodeType.Item,
                IsLocalOnly = true,
                IsHistoryItem = true
            };

            item.Name = source.Name;
            item.Type = string.IsNullOrWhiteSpace(source.Symbol) ? BookmarkType.File : BookmarkType.Symbol;
            item.Symbol = source.Symbol;
            item.Project = source.Project;
            item.Path = source.Path;
            item.Line = source.Line;
            item.Column = source.Column;
            item.Comment = visitedAt.ToString("yyyy-MM-dd HH:mm:ss");
            item.EditorState = source.EditorState?.Clone();

            if (existing != null)
            {
                Root.Children.Remove(existing);
            }
            Root.Children.Insert(0, item);

            while (Root.Children.Count > HistoryLimit)
            {
                var removable = Root.Children.LastOrDefault(candidate => isPinned?.Invoke(candidate) != true);
                if (removable == null)
                {
                    break;
                }
                Root.Children.Remove(removable);
            }

            return true;
        }

        public List<NavigationHistoryLocalItem> Export()
        {
            return Root?.Children.Select(item => new NavigationHistoryLocalItem
            {
                Id = item.Id,
                Name = item.Name,
                Type = item.Type,
                Symbol = item.Symbol,
                Project = item.Project,
                Path = item.Path,
                Line = item.Line,
                Column = item.Column,
                Comment = item.Comment,
                VisitedAt = DateTime.TryParse(item.Comment, out var visited) ? visited : DateTime.Now,
                EditorState = item.EditorState?.Clone()
            }).ToList() ?? new List<NavigationHistoryLocalItem>();
        }

        public static string GetKey(DocumentItem item)
        {
            if (item == null)
            {
                return string.Empty;
            }

            return !string.IsNullOrWhiteSpace(item.Symbol)
                ? (item.Project ?? string.Empty) + "|" + item.Symbol
                : item.Path ?? string.Empty;
        }

        private string CreateReadableId(string name)
        {
            var builder = new StringBuilder();
            var separatorPending = false;
            foreach (var character in (name ?? string.Empty).Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(character))
                {
                    if (separatorPending && builder.Length > 0)
                    {
                        builder.Append('-');
                    }
                    builder.Append(character);
                    separatorPending = false;
                }
                else
                {
                    separatorPending = true;
                }
            }

            var baseId = "history-" + (builder.Length == 0 ? "item" : builder.ToString().Trim('-'));
            var usedIds = new HashSet<string>(EnumerateStoredNodes().Select(item => item.Id), StringComparer.OrdinalIgnoreCase);
            var id = baseId;
            var index = 2;
            while (!usedIds.Add(id))
            {
                id = baseId + "-" + index++;
            }
            return id;
        }

        private IEnumerable<DocumentItem> EnumerateStoredNodes()
        {
            return state?.Sets == null
                ? Enumerable.Empty<DocumentItem>()
                : state.Sets.SelectMany(set => EnumerateNodes(set.Children));
        }

        private static IEnumerable<DocumentItem> EnumerateNodes(IEnumerable<DocumentItem> nodes)
        {
            if (nodes == null)
            {
                yield break;
            }

            foreach (var node in nodes)
            {
                if (node == null)
                {
                    continue;
                }

                yield return node;
                foreach (var child in EnumerateNodes(node.Children))
                {
                    yield return child;
                }
            }
        }
    }
}
