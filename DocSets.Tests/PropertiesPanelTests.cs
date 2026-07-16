using System;
using System.Reflection;
using System.Windows.Forms;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class PropertiesPanelTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LoadAndApplyWithoutEditsDoesNotChangeItem()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var item = Bookmark();
                panel.LoadItem(item, true);
                Assert.Same(item, panel.CurrentItem);
                Assert.False(panel.ApplyToCurrentItem());
                Assert.Null(panel.GetPendingChangeDescription());
                Assert.False(panel.RequestedPinState);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void AppliesEditedFieldsToSingleItem()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var item = Bookmark();
                panel.LoadItem(item);
                Field<TextBox>(panel, "nameTextBox").Text = "Renamed";
                Field<TextBox>(panel, "commentTextBox").Text = "new comment";
                Field<RadioButton>(panel, "fileButton").Checked = true;
                Field<TextBox>(panel, "pathTextBox").Text = "new.cs";
                Field<NumericUpDown>(panel, "lineBox").Value = 42;

                Assert.NotNull(panel.GetPendingChangeDescription());
                Assert.True(panel.ApplyToCurrentItem());
                Assert.Equal("Renamed", item.Name);
                Assert.Equal("new comment", item.Comment);
                Assert.Equal(BookmarkType.File, item.Type);
                Assert.Equal("new.cs", item.Path);
                Assert.Equal(42, item.Line);
                Assert.Equal(string.Empty, item.Symbol);
                Assert.False(panel.ApplyToCurrentItem());
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void MultipleSelectionDoesNotApplyOrdinaryFields()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var item = Bookmark();
                panel.LoadSelection(item, true, false, true, null, true);
                Field<TextBox>(panel, "nameTextBox").Text = "Must not apply";
                Assert.False(panel.ApplyToCurrentItem());
                Assert.Equal("Item", item.Name);
                Assert.True(Field<ExperimentalAccordionSection>(panel, "propertiesSection").ContentEnabled == false);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void PinRequestTogglesFromLoadedAllPinnedState()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var item = Bookmark();
                panel.LoadSelection(item, true, true, true, item.Color, true);
                Assert.False(panel.RequestedPinState);
                panel.LoadSelection(item, true, false, true, item.Color, true);
                Assert.True(panel.RequestedPinState);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void PreviewIsRequestedOnlyWhenSectionIsExpanded()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var requests = 0;
                panel.PreviewRequested += (_, __) => requests++;
                panel.LoadItem(Bookmark());
                Assert.Equal(0, requests);
                var preview = Field<ExperimentalAccordionSection>(panel, "previewSection");
                preview.Expanded = true;
                Assert.Equal(1, requests);
                panel.LoadItem(Bookmark());
                Assert.Equal(2, requests);
                preview.Expanded = false;
                panel.LoadItem(Bookmark());
                Assert.Equal(2, requests);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LivePreviewMethodsUpdateText()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                panel.LoadItem(Bookmark());
                panel.ShowLivePreviewLoading();
                Assert.Equal("Загрузка...", Field<RichTextBox>(panel, "livePreviewTextBox").Text);
                panel.ShowLivePreview("class C { }");
                Assert.Equal("class C { }", Field<RichTextBox>(panel, "livePreviewTextBox").Text);
                panel.ShowLivePreview(null);
                Assert.Equal("Превью недоступно.", Field<RichTextBox>(panel, "livePreviewTextBox").Text);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void BreadcrumbCreatesLinkForEverySymbolSegment()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var item = Bookmark();
                item.Symbol = "Namespace.Class.Method";
                panel.LoadItem(item);
                var label = Field<LinkLabel>(panel, "codeSymbolLabel");
                Assert.Equal("Namespace.Class.Method", label.Text);
                Assert.Equal(3, label.Links.Count);
                Assert.Equal("Namespace", label.Links[0].LinkData);
                Assert.Equal("Namespace.Class", label.Links[1].LinkData);
                Assert.Equal("Namespace.Class.Method", label.Links[2].LinkData);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void MarkdownCommentIsSecondViewOfExistingComment()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var item = Bookmark(); panel.LoadItem(item);
                var markdown = Field<MarkdownCommentControl>(panel, "markdownComment");
                Assert.Equal("old comment", markdown.CommentText);
                Assert.False(markdown.IsEditing);
                Field<RichTextBox>(markdown, "editor").Text = "**new** [[A.B.Run]]";
                Assert.Equal("old comment", item.Comment);
                Assert.True(panel.ApplyToCurrentItem());
                Assert.Equal("**new** [[A.B.Run]]", item.Comment);
                Field<TextBox>(panel, "commentTextBox").Text = "classic edit";
                panel.ApplySelectedContentTab("comment");
                var markdown2 = Field<MarkdownCommentControl>(panel, "markdownComment2");
                Assert.Equal("classic edit", markdown2.CommentText);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SecondCommentTabIsIsolatedExperimentalSurface()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var item = Bookmark();
                panel.LoadItem(item);
                var tabs = Field<TabControl>(panel, "contentTabs");
                var current = Field<MarkdownCommentControl>(panel, "markdownComment");
                var experimental = Field<MarkdownCommentControl>(panel, "markdownComment2");

                Assert.Equal(3, tabs.TabPages.Count);
                Assert.Equal("Комментарий-2", tabs.TabPages[1].Text);
                Assert.False(current.ExperimentalDragDrop);
                Assert.True(experimental.ExperimentalDragDrop);
                Assert.Equal("comment3", tabs.TabPages[2].Tag as string);
                var experimentalEditor = Field<RichTextBox>(experimental, "editor");
                Assert.Equal(11, experimentalEditor.Lines.Length);

                panel.ApplySelectedContentTab("comment2");
                Assert.Equal("comment2", panel.SelectedContentTab);
                Assert.Equal("old comment", experimental.CommentText);
                experimentalEditor.Text = "changed" + Environment.NewLine + Environment.NewLine;
                Assert.Equal("changed", experimental.CommentText);
                Assert.True(panel.ApplyToCurrentItem());
                Assert.Equal("changed", item.Comment);
            }
        }
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ContentTabsStayAvailableWithoutSelectedItem()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                panel.LoadItem(null);
                var tabs = Field<TabControl>(panel, "contentTabs");
                Assert.True(panel.Enabled);
                Assert.True(tabs.Enabled);
                tabs.SelectedIndex = 1;
                Assert.Equal("comment2", panel.SelectedContentTab);
            }
        }
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ContentTabSelectionCanBeRestored()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                panel.ApplySelectedContentTab("comment"); Assert.Equal("comment2", panel.SelectedContentTab);
                panel.ApplySelectedContentTab("properties"); Assert.Equal("properties", panel.SelectedContentTab);
            }
        }

        private static T Field<T>(object instance, string name) where T : class
        {
            return (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(instance);
        }

        private static DocumentItem Bookmark() => new DocumentItem
        {
            Name = "Item", NodeType = NodeType.Item, Type = BookmarkType.Symbol,
            Path = "old.cs", Symbol = "A.B", Project = "P", Line = 10, Column = 2,
            Comment = "old comment", Color = BookmarkColor.Blue
        };
    }
}
