using System;
using System.Collections.Generic;
using System.Linq;

namespace DocSets
{
    internal sealed class PinService
    {
        private DocumentSetsState state;

        public DocumentItem Root { get; private set; }

        public void Attach(
            DocumentSetsState documentState,
            IEnumerable<PinLocalItem> savedPins,
            DocumentItem insertAfter)
        {
            state = documentState ?? throw new ArgumentNullException(nameof(documentState));
            Root = state.Sets.FirstOrDefault(item => item != null && item.IsPinRoot);
            if (Root == null)
            {
                Root = new DocumentItem
                {
                    Id = "pin",
                    Name = "Pin",
                    NodeType = NodeType.Folder,
                    Type = BookmarkType.Empty,
                    IsLocalOnly = true,
                    IsPinRoot = true
                };
                var previousIndex = insertAfter == null ? -1 : state.Sets.IndexOf(insertAfter);
                state.Sets.Insert(Math.Max(0, previousIndex + 1), Root);
            }

            Root.Children.Clear();
            foreach (var saved in savedPins ?? Enumerable.Empty<PinLocalItem>())
            {
                Root.Children.Add(new DocumentItem
                {
                    Id = saved.Id ?? string.Empty,
                    NodeType = NodeType.Item,
                    Type = BookmarkType.Pin,
                    TargetId = saved.TargetId ?? string.Empty,
                    IsLocalOnly = true,
                    IsPinItem = true
                });
            }
        }

        public DocumentItem Resolve(DocumentItem item)
        {
            if (item == null || !item.IsPinItem)
            {
                return item;
            }

            return EnumerateNodes().FirstOrDefault(candidate =>
                !candidate.IsPinItem &&
                string.Equals(candidate.Id, item.TargetId, StringComparison.OrdinalIgnoreCase));
        }

        public bool IsPinned(DocumentItem item)
        {
            return item != null && Root != null && Root.Children.Any(pin =>
                string.Equals(pin.TargetId, item.Id, StringComparison.OrdinalIgnoreCase));
        }

        public bool CanToggle(DocumentItem item)
        {
            if (item == null || item.IsPinRoot || item.IsHistoryRoot || item.IsRecentRoot)
            {
                return false;
            }

            return item.IsPinItem || !string.IsNullOrWhiteSpace(item.Id);
        }

        public void Toggle(DocumentItem item)
        {
            if (Root == null || item == null)
            {
                return;
            }

            if (item.IsPinItem)
            {
                Root.Children.Remove(item);
                return;
            }

            var existing = Root.Children.FirstOrDefault(pin =>
                string.Equals(pin.TargetId, item.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                Root.Children.Remove(existing);
                return;
            }

            Root.Children.Add(new DocumentItem
            {
                Id = "pin-" + Guid.NewGuid().ToString("N"),
                NodeType = NodeType.Item,
                Type = BookmarkType.Pin,
                TargetId = item.Id,
                IsLocalOnly = true,
                IsPinItem = true
            });
        }

        public void RemoveTargets(ISet<string> targetIds)
        {
            if (Root == null || targetIds == null || targetIds.Count == 0)
            {
                return;
            }

            foreach (var pin in Root.Children.Where(item => targetIds.Contains(item.TargetId)).ToList())
            {
                Root.Children.Remove(pin);
            }
        }

        public List<PinLocalItem> Export()
        {
            return Root?.Children.Select(pin => new PinLocalItem
            {
                Id = pin.Id,
                TargetId = pin.TargetId
            }).ToList() ?? new List<PinLocalItem>();
        }

        public void ApplyIdMigration(IDictionary<string, string> idMap)
        {
            if (idMap == null || idMap.Count == 0 || Root == null)
            {
                return;
            }

            foreach (var pin in Root.Children)
            {
                if (!string.IsNullOrWhiteSpace(pin.TargetId) && idMap.TryGetValue(pin.TargetId, out var mapped))
                {
                    pin.TargetId = mapped;
                }
            }
        }

        private IEnumerable<DocumentItem> EnumerateNodes()
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
