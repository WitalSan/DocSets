using System.Collections.ObjectModel;
using System.Linq;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class DocumentTreeServiceTests
    {
        private readonly DocumentTreeService service = new DocumentTreeService();

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void FindsContainingSetAndParentFolder()
        {
            var state = State(out var set, out var folder, out var child, out _);
            Assert.Same(set, service.GetSetContainingNode(state, child));
            Assert.Same(folder, service.GetParentFolder(state, child));
            Assert.Same(set, service.GetSetContainingNode(state, set));
            Assert.Null(service.GetParentFolder(state, set));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ContainsAndOwnerCollectionUseReferenceIdentity()
        {
            var state = State(out var set, out var folder, out var child, out _);
            var sameValues = Item(child.Name);
            Assert.True(service.ContainsNode(set.Children, child));
            Assert.False(service.ContainsNode(set.Children, sameValues));
            Assert.Same(folder.Children, service.FindOwnerCollection(child));
            Assert.Same(folder.Children, service.FindOwnerCollection(set.Children, child));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void FlattenHonorsExpansionState()
        {
            State(out var set, out var folder, out var child, out var sibling);
            folder.IsExpanded = false;
            Assert.SequenceEqual(new[] { folder.Name, sibling.Name }, service.Flatten(set.Children, false).Select(x => x.Name));
            folder.IsExpanded = true;
            Assert.SequenceEqual(new[] { folder.Name, child.Name, sibling.Name }, service.Flatten(set.Children, false).Select(x => x.Name));
            folder.IsExpanded = false;
            Assert.SequenceEqual(new[] { folder.Name, child.Name, sibling.Name }, service.Flatten(set.Children, true).Select(x => x.Name));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void FilterOutDescendantsKeepsOnlyTopmostSelections()
        {
            State(out _, out var folder, out var child, out var sibling);
            var result = service.FilterOutDescendants(new[] { child, folder, sibling, child });
            Assert.SequenceEqual(new[] { folder, sibling }, result);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void MovePlanRejectsMovingNodeIntoItselfOrDescendant()
        {
            var state = State(out var set, out var folder, out var child, out _);
            Assert.Null(service.CreateMovePlan(state, set, new[] { folder }, folder, DropPosition.Inside, false));
            Assert.Null(service.CreateMovePlan(state, set, new[] { folder }, child, DropPosition.Inside, false));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void MovePlanAdjustsIndexWhenSelectedItemsPrecedeTarget()
        {
            var state = new DocumentSetsState();
            var set = Folder("Set");
            var a = Item("A"); var b = Item("B"); var c = Item("C"); var d = Item("D");
            set.Children.Add(a); set.Children.Add(b); set.Children.Add(c); set.Children.Add(d); state.Sets.Add(set);
            var plan = service.CreateMovePlan(state, set, new[] { a, b }, d, DropPosition.After, false);
            Assert.Same(set.Children, plan.Collection);
            Assert.Equal(2, plan.Index);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CopyPlanTargetsFolderOrSiblingPosition()
        {
            var state = State(out var set, out var folder, out var child, out var sibling);
            var inside = service.CreateCopyPlan(state, set, new[] { sibling }, folder, DropPosition.Inside, false);
            Assert.Same(folder.Children, inside.Collection);
            Assert.Equal(1, inside.Index);
            var before = service.CreateCopyPlan(state, set, new[] { child }, sibling, DropPosition.Before, false);
            Assert.Same(set.Children, before.Collection);
            Assert.Equal(1, before.Index);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RemoveNodeReferenceRemovesAllOccurrencesRecursively()
        {
            var state = State(out var set, out var folder, out var child, out _);
            set.Children.Add(child);
            Assert.True(service.RemoveNodeReference(set.Children, child));
            Assert.False(service.ContainsNode(set.Children, child));
            Assert.False(service.RemoveNodeReference(set.Children, child));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void MoveAndCanMoveRespectBounds()
        {
            var list = new ObservableCollection<DocumentItem> { Item("A"), Item("B"), Item("C") };
            Assert.False(service.CanMove(list, list[0], -1));
            Assert.True(service.Move(list, list[0], 1));
            Assert.SequenceEqual(new[] { "B", "A", "C" }, list.Select(x => x.Name));
            Assert.False(service.Move(list, list[2], 1));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void GetInsertCollectionUsesFolderChildrenOtherwiseSetRoot()
        {
            State(out var set, out var folder, out var child, out _);
            Assert.Same(folder.Children, service.GetInsertCollection(set, folder));
            Assert.Same(set.Children, service.GetInsertCollection(set, child));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void FullTreeNullTargetPlansAgainstRootSets()
        {
            var state = State(out var set, out _, out var child, out _);
            var copy = service.CreateCopyPlan(state, null, new[] { child }, null, DropPosition.After, true);
            Assert.Same(state.Root.Children, copy.Collection);
            Assert.Equal(state.Sets.Count, copy.Index);
            Assert.Null(service.CreateCopyPlan(state, null, new[] { child }, null, DropPosition.After, false));
        }

        private static DocumentSetsState State(out DocumentItem set, out DocumentItem folder, out DocumentItem child, out DocumentItem sibling)
        {
            var state = new DocumentSetsState();
            set = Folder("Set"); folder = Folder("Folder"); child = Item("Child"); sibling = Item("Sibling");
            folder.Children.Add(child); set.Children.Add(folder); set.Children.Add(sibling); state.Sets.Add(set);
            return state;
        }

        private static DocumentItem Folder(string name) => new DocumentItem { Name = name, NodeType = NodeType.Folder, Type = BookmarkType.Empty };
        private static DocumentItem Item(string name) => new DocumentItem { Name = name, NodeType = NodeType.Item, Type = BookmarkType.File, Path = name + ".cs" };
    }
}
