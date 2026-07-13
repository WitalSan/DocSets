using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace DocSets
{
    /// <summary>
    /// Pure tree algorithms for DocumentItem. This service does not own selection,
    /// persistence, dialogs, or Visual Studio integration.
    /// </summary>
    internal sealed class DocumentTreeService
    {
        public DocumentItem GetSetContainingNode(DocumentSetsState state, DocumentItem item)
        {
            if (state == null || item == null)
            {
                return null;
            }

            if (item.IsRootChild && item.NodeType == NodeType.Folder)
            {
                return item;
            }

            return state.Sets.FirstOrDefault(set => ContainsNode(set.Children, item));
        }

        public DocumentItem GetParentFolder(DocumentSetsState state, DocumentItem item)
        {
            if (state == null || item == null)
            {
                return null;
            }

            if (item.Parent != null && !ReferenceEquals(item.Parent, state.Root))
            {
                return item.Parent;
            }

            foreach (var set in state.Sets)
            {
                var parent = FindParentFolder(set.Children, item);
                if (parent != null)
                {
                    return parent;
                }
            }

            return null;
        }

        public DocumentTreeMovePlan CreateCopyPlan(
            DocumentSetsState state,
            DocumentItem selectedSet,
            IEnumerable<DocumentItem> selectedNodes,
            DocumentItem target,
            DropPosition position,
            bool fullTree)
        {
            var nodes = FilterOutDescendants(selectedNodes).ToList();
            if (state == null || (!fullTree && selectedSet == null) || nodes.Count == 0)
            {
                return null;
            }

            if (target == null)
            {
                var rootCollection = fullTree ? state.Root.Children : selectedSet.Children;
                return new DocumentTreeMovePlan(rootCollection, rootCollection.Count);
            }

            if (position == DropPosition.Inside && target.NodeType == NodeType.Folder)
            {
                return new DocumentTreeMovePlan(target.Children, target.Children.Count);
            }

            var owner = FindOwnerCollection(target);
            if (owner == null)
            {
                return null;
            }

            var targetIndex = owner.IndexOf(target);
            if (targetIndex < 0)
            {
                return null;
            }

            var index = position == DropPosition.After ? targetIndex + 1 : targetIndex;
            return new DocumentTreeMovePlan(owner, index);
        }

        public DocumentTreeMovePlan CreateMovePlan(
            DocumentSetsState state,
            DocumentItem selectedSet,
            IEnumerable<DocumentItem> selectedNodes,
            DocumentItem target,
            DropPosition position,
            bool fullTree)
        {
            var nodes = FilterOutDescendants(selectedNodes).ToList();
            if (state == null || (!fullTree && selectedSet == null) || nodes.Count == 0)
            {
                return null;
            }

            if (nodes.Any(node => ReferenceEquals(node, target) || IsDescendantOf(target, node)))
            {
                return null;
            }

            if (target == null)
            {
                var rootCollection = fullTree ? state.Root.Children : selectedSet.Children;
                return new DocumentTreeMovePlan(rootCollection, rootCollection.Count);
            }

            if (position == DropPosition.Inside && target.NodeType == NodeType.Folder)
            {
                return new DocumentTreeMovePlan(target.Children, target.Children.Count);
            }

            var owner = FindOwnerCollection(target);
            if (owner == null)
            {
                return null;
            }

            var targetIndex = owner.IndexOf(target);
            if (targetIndex < 0)
            {
                return null;
            }

            var selectedBeforeTarget = nodes.Count(node =>
                ReferenceEquals(FindOwnerCollection(node), owner) &&
                owner.IndexOf(node) >= 0 &&
                owner.IndexOf(node) < targetIndex);
            var index = position == DropPosition.After ? targetIndex + 1 : targetIndex;
            return new DocumentTreeMovePlan(owner, index - selectedBeforeTarget);
        }

        public ObservableCollection<DocumentItem> GetInsertCollection(DocumentItem set, DocumentItem target)
        {
            if (target?.NodeType == NodeType.Folder)
            {
                return target.Children;
            }

            return set?.Children ?? new ObservableCollection<DocumentItem>();
        }

        public void RemoveNodeReferenceFromAllSets(DocumentSetsState state, DocumentItem item)
        {
            if (state?.Sets == null || item == null)
            {
                return;
            }

            foreach (var set in state.Sets)
            {
                RemoveNodeReference(set.Children, item);
            }
        }

        public bool RemoveNodeReference(ObservableCollection<DocumentItem> nodes, DocumentItem item)
        {
            if (nodes == null || item == null)
            {
                return false;
            }

            var removed = false;
            for (var index = nodes.Count - 1; index >= 0; index--)
            {
                var node = nodes[index];
                if (ReferenceEquals(node, item))
                {
                    nodes.RemoveAt(index);
                    removed = true;
                    continue;
                }

                if (RemoveNodeReference(node.Children, item))
                {
                    removed = true;
                }
            }

            return removed;
        }

        public bool ContainsReference(IEnumerable<DocumentItem> nodes, DocumentItem item)
        {
            return nodes != null && item != null && nodes.Any(node => ReferenceEquals(node, item));
        }

        public bool ContainsNode(IEnumerable<DocumentItem> nodes, DocumentItem item)
        {
            if (nodes == null || item == null)
            {
                return false;
            }

            foreach (var node in nodes)
            {
                if (ReferenceEquals(node, item) || ContainsNode(node.Children, item))
                {
                    return true;
                }
            }

            return false;
        }

        public DocumentItem FindParentFolder(IEnumerable<DocumentItem> nodes, DocumentItem item)
        {
            if (nodes == null || item == null)
            {
                return null;
            }

            foreach (var node in nodes)
            {
                if (node.Children != null && node.Children.Contains(item))
                {
                    return node;
                }

                var nested = FindParentFolder(node.Children, item);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        public ObservableCollection<DocumentItem> FindOwnerCollection(DocumentItem item)
        {
            return item?.Parent?.Children;
        }

        public ObservableCollection<DocumentItem> FindOwnerCollection(
            IEnumerable<DocumentItem> nodes,
            DocumentItem item)
        {
            if (nodes == null || item == null)
            {
                return null;
            }

            var collection = nodes as ObservableCollection<DocumentItem>;
            if (collection != null && collection.Contains(item))
            {
                return collection;
            }

            foreach (var node in nodes)
            {
                var nested = FindOwnerCollection(node.Children, item);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        public IEnumerable<DocumentItem> Flatten(IEnumerable<DocumentItem> nodes, bool includeCollapsed)
        {
            if (nodes == null)
            {
                yield break;
            }

            foreach (var node in nodes)
            {
                yield return node;
                if (includeCollapsed || node.IsExpanded)
                {
                    foreach (var child in Flatten(node.Children, includeCollapsed))
                    {
                        yield return child;
                    }
                }
            }
        }

        public IReadOnlyList<DocumentItem> FilterOutDescendants(IEnumerable<DocumentItem> nodes)
        {
            var list = (nodes ?? Enumerable.Empty<DocumentItem>())
                .Where(node => node != null)
                .Distinct()
                .ToList();
            return list
                .Where(node => !list.Any(parent =>
                    !ReferenceEquals(parent, node) && IsDescendantOf(node, parent)))
                .ToList();
        }

        public bool IsDescendantOf(DocumentItem node, DocumentItem potentialParent)
        {
            if (node == null || potentialParent == null)
            {
                return false;
            }

            foreach (var child in potentialParent.Children)
            {
                if (ReferenceEquals(child, node) || IsDescendantOf(node, child))
                {
                    return true;
                }
            }

            return false;
        }

        public void NormalizeNodes(IEnumerable<DocumentItem> nodes)
        {
            if (nodes == null)
            {
                return;
            }

            foreach (var node in nodes)
            {
                if (node.Children == null)
                {
                    node.Children = new ObservableCollection<DocumentItem>();
                }

                NormalizeNodes(node.Children);
            }
        }

        public bool Move<T>(ObservableCollection<T> collection, T item, int delta) where T : class
        {
            if (!CanMove(collection, item, delta))
            {
                return false;
            }

            collection.Move(collection.IndexOf(item), collection.IndexOf(item) + delta);
            return true;
        }

        public bool CanMove<T>(ObservableCollection<T> collection, T item, int delta) where T : class
        {
            if (collection == null || item == null)
            {
                return false;
            }

            var index = collection.IndexOf(item);
            var newIndex = index + delta;
            return index >= 0 && newIndex >= 0 && newIndex < collection.Count;
        }
    }

    internal sealed class DocumentTreeMovePlan
    {
        public DocumentTreeMovePlan(ObservableCollection<DocumentItem> collection, int index)
        {
            Collection = collection ?? throw new ArgumentNullException(nameof(collection));
            Index = index;
        }

        public ObservableCollection<DocumentItem> Collection { get; }

        public int Index { get; }
    }
}
