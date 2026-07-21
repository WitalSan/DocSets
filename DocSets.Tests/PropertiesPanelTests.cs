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
                Field<RadioButton>(panel, "fileButton").Checked = true;
                Field<TextBox>(panel, "pathTextBox").Text = "new.cs";
                Field<NumericUpDown>(panel, "lineBox").Value = 42;

                Assert.NotNull(panel.GetPendingChangeDescription());
                Assert.True(panel.ApplyToCurrentItem());
                Assert.True(panel.ApplyCommittedComment(item, panel.CommentRevision, "new comment"));
                panel.AcceptCommittedComment(item, panel.CommentRevision, "new comment");
                Assert.Equal("Renamed", item.Name);
                Assert.Equal("new comment", item.Content);
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
                panel.ApplySelectedContentTab("preview");
                Assert.Equal(1, requests);
                panel.LoadItem(Bookmark());
                Assert.Equal(2, requests);
                panel.ApplySelectedContentTab("properties");
                panel.LoadItem(Bookmark());
                Assert.Equal(2, requests);            }
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
                Assert.Equal("old comment", item.Content);
                Assert.False(panel.ApplyToCurrentItem());
                Assert.True(panel.ApplyCommittedComment(item, panel.CommentRevision, markdown.CommentText));
                panel.AcceptCommittedComment(item, panel.CommentRevision, markdown.CommentText);
                Assert.Equal("**new** [[A.B.Run]]", item.Content);
                panel.ApplyCommittedComment(item, panel.CommentRevision, "classic edit");
                panel.AcceptCommittedComment(item, panel.CommentRevision, "classic edit");
                panel.LoadItem(item);
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
                var dock = Field<DockWorkspaceControl>(panel, "dockWorkspace");
                var current = Field<MarkdownCommentControl>(panel, "markdownComment");
                var experimental = Field<MarkdownCommentControl>(panel, "markdownComment2");

                Assert.True(dock.ContainsPanel("properties"));
                Assert.True(dock.ContainsPanel("comment2"));
                Assert.True(dock.ContainsPanel("comment3"));
                Assert.False(current.ExperimentalDragDrop);
                Assert.True(experimental.ExperimentalDragDrop);
                var experimentalEditor = Field<RichTextBox>(experimental, "editor");
                Assert.Equal(11, experimentalEditor.Lines.Length);

                panel.ApplySelectedContentTab("comment2");
                Assert.Equal("comment2", panel.SelectedContentTab);
                Assert.Equal("old comment", experimental.CommentText);
                experimentalEditor.Text = "changed" + Environment.NewLine + Environment.NewLine;
                Assert.Equal("changed", experimental.CommentText);
                Assert.False(panel.ApplyToCurrentItem());
                Assert.True(panel.ApplyCommittedComment(item, panel.CommentRevision, experimental.CommentText));
                panel.AcceptCommittedComment(item, panel.CommentRevision, experimental.CommentText);
                Assert.Equal("changed", item.Content);
            }
        }        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ContentTabsStayAvailableWithoutSelectedItem()
        {
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                panel.LoadItem(null);
                var dock = Field<DockWorkspaceControl>(panel, "dockWorkspace");
                Assert.True(panel.Enabled);
                Assert.True(dock.Enabled);
                panel.ApplySelectedContentTab("comment2");
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

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DockLayoutPreservesHiddenPanelsAndResetRestoresDefaults()
        {
            string layout;
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var dock = Field<DockWorkspaceControl>(panel, "dockWorkspace");
                dock.HidePanel("preview");
                Assert.False(dock.IsPanelVisible("preview"));
                layout = panel.CaptureDockLayout();
            }

            using (var restored = new BookmarkPropertiesPanelExperimental())
            {
                restored.RestoreDockLayout(layout);
                var dock = Field<DockWorkspaceControl>(restored, "dockWorkspace");
                Assert.False(dock.IsPanelVisible("preview"));
                restored.ResetDockLayout();
                Assert.True(dock.IsPanelVisible("preview"));
                Assert.Equal(1, dock.GroupCount);
            }
        }
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SplitterRatioIgnoresTransientTinySize()
        {
            using (var split = new SplitContainer
            {
                Orientation = Orientation.Horizontal,
                Size = new System.Drawing.Size(100, 100),
                Panel1MinSize = 40,
                Panel2MinSize = 40,
                SplitterWidth = 8,
                SplitterDistance = 46
            })
            {
                split.Height = 13;
                DockWorkspaceControl.ApplySplitterRatio(split, 0.5F);
                Assert.Equal(0, split.Panel1MinSize);
                Assert.Equal(0, split.Panel2MinSize);
                Assert.True(split.SplitterDistance >= 0);
            }
        }
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DockLayoutRestoresHorizontalSplit()
        {
            const string json = "{\"Version\":2,\"Root\":{\"Type\":\"split\",\"Orientation\":1,\"Ratio\":0.5," +
                "\"First\":{\"Type\":\"tabs\",\"Id\":\"left\",\"PanelIds\":[\"code\"],\"ActivePanelId\":\"code\"}," +
                "\"Second\":{\"Type\":\"tabs\",\"Id\":\"right\",\"PanelIds\":[\"preview\"],\"ActivePanelId\":\"preview\"}}}";
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                var previewRequests = 0;
                panel.PreviewRequested += (_, __) => previewRequests++;
                panel.RestoreDockLayout(json);
                panel.LoadItem(Bookmark());
                var dock = Field<DockWorkspaceControl>(panel, "dockWorkspace");
                Assert.Equal(1, previewRequests);
                Assert.True(dock.IsPanelDisplayed("preview"));
                Assert.Equal(2, dock.GroupCount);
                Assert.True(dock.IsPanelVisible("code"));
                Assert.True(dock.IsPanelVisible("preview"));
                Assert.True(panel.CaptureDockLayout().Contains("\"Orientation\":1"));
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DockLayoutMigratesLegacyVerticalGroups()
        {
            const string json = "{\"Groups\":[" +
                "{\"Id\":\"top\",\"Weight\":0.4,\"PanelIds\":[\"code\"],\"ActivePanelId\":\"code\"}," +
                "{\"Id\":\"bottom\",\"Weight\":0.6,\"PanelIds\":[\"preview\"],\"ActivePanelId\":\"preview\"}]}";
            using (var panel = new BookmarkPropertiesPanelExperimental())
            {
                panel.RestoreDockLayout(json);
                var dock = Field<DockWorkspaceControl>(panel, "dockWorkspace");
                Assert.Equal(2, dock.GroupCount);
                var migrated = panel.CaptureDockLayout();
                Assert.True(migrated.Contains("\"Version\":2"));
                Assert.True(migrated.Contains("\"Root\""));
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
            Content = "old comment", Color = BookmarkColor.Blue
        };
    }
}
