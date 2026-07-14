using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Newtonsoft.Json;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class TagServiceTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void AddsReadableUniqueIdsAndRejectsDuplicateNames()
        {
            var state = new DocumentSetsState(); var service = new TagService();
            Assert.Equal("code-review", service.Add(state, "Code Review").Id);
            Assert.Equal("code-review-2", service.Add(state, "Code/Review 2").Id);
            Assert.Throws<InvalidOperationException>(() => service.Add(state, "code review"));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void StandardTagsAreAddedOnlyToEmptyCatalog()
        {
            var state = new DocumentSetsState(); var service = new TagService();
            service.EnsureStandardTags(state); service.EnsureStandardTags(state);
            Assert.SequenceEqual(new[] { "bug", "todo", "critical", "review" }, state.Tags.Select(x => x.Id));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ToggleSupportsMixedMultipleSelectionAndFolders()
        {
            var service = new TagService();
            var first = new DocumentItem { NodeType = NodeType.Folder, TagIds = new List<string> { "bug" } };
            var second = new DocumentItem();
            service.Toggle(new[] { first, second }, "bug");
            Assert.True(first.TagIds.Contains("bug") && second.TagIds.Contains("bug"));
            service.Toggle(new[] { first, second }, "bug");
            Assert.Equal(0, first.TagIds.Count + second.TagIds.Count);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DeleteRemovesAssignmentsRecursively()
        {
            var child = new DocumentItem { TagIds = new List<string> { "bug", "todo" } };
            var root = new DocumentItem { NodeType = NodeType.Folder, TagIds = new List<string> { "bug" }, Children = new ObservableCollection<DocumentItem> { child } };
            var state = new DocumentSetsState { Tags = new List<TagDefinition> { new TagDefinition { Id = "bug" }, new TagDefinition { Id = "todo" } }, Sets = new ObservableCollection<DocumentItem> { root } };
            new TagService().Delete(state, "bug");
            Assert.SequenceEqual(new[] { "todo" }, child.TagIds); Assert.Equal(0, root.TagIds.Count);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void WorkspaceRoundTripPreservesDefinitionsAndAssignments()
        {
            var state = new DocumentSetsState { Tags = new List<TagDefinition> { new TagDefinition { Id = "bug", Name = "Bug", Color = "#f00", Icon = "bug" } } };
            state.Sets.Add(new DocumentItem { Name = "Folder", NodeType = NodeType.Folder, TagIds = new List<string> { "bug" } });
            var restored = JsonConvert.DeserializeObject<DocumentSetsState>(JsonConvert.SerializeObject(state));
            Assert.Equal("bug", restored.Tags[0].Icon); Assert.SequenceEqual(new[] { "bug" }, restored.Sets[0].TagIds);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CloneCopiesTagListIndependently()
        {
            var item = new DocumentItem { TagIds = new List<string> { "bug" } }; var clone = item.Clone();
            clone.TagIds.Add("todo"); Assert.SequenceEqual(new[] { "bug" }, item.TagIds);
        }
    }
}