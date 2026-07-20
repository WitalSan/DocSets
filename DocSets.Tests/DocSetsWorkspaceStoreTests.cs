using System;
using System.IO;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class DocSetsWorkspaceStoreTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SolutionWorkspaceLivesUnderDotVsAndUsesSolutionAsPathBase()
        {
            var solution = Path.Combine("D:\\Projects", "Vital", "Vital.sln");
            var location = DocSetsWorkspaceLocation.ForSolution(solution);

            Assert.Equal(Path.GetFullPath(Path.Combine("D:\\Projects", "Vital")), location.BaseDirectory);
            Assert.Equal(Path.GetFullPath(Path.Combine("D:\\Projects", "Vital", ".vs", "DocSets", "workspace.json")), location.FilePath);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ManagerStoresInternalDocSetsRelativelyAndExternalDocSetsAbsolutely()
        {
            WithWorkspace((location, workspace, root) =>
            {
                var manager = new DocSetsWorkspaceManager(location, workspace);
                var internalDocSet = Path.Combine(root, "docs", "Vital.DocSets");
                var externalDocSet = Path.Combine(Path.GetTempPath(), "Shared-" + Guid.NewGuid().ToString("N") + ".DocSets");

                Assert.True(manager.Open(internalDocSet));
                Assert.True(manager.Open(externalDocSet, makeActive: false));

                Assert.False(Path.IsPathRooted(workspace.OpenDocSets[0]));
                Assert.True(Path.IsPathRooted(workspace.OpenDocSets[1]));
                Assert.Equal(Path.GetFullPath(internalDocSet), manager.ResolveActiveDocSet());
                Assert.SequenceEqual(new[] { Path.GetFullPath(internalDocSet), Path.GetFullPath(externalDocSet) },
                    manager.ResolveOpenDocSets());
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ManagerClosesActiveDocSetAndActivatesRemainingOne()
        {
            WithWorkspace((location, workspace, root) =>
            {
                var manager = new DocSetsWorkspaceManager(location, workspace);
                var first = Path.Combine(root, "First.DocSets");
                var second = Path.Combine(root, "Second.DocSets");
                manager.Open(first);
                manager.Open(second);

                Assert.True(manager.Close(second));
                Assert.Equal(Path.GetFullPath(first), manager.ResolveActiveDocSet());
                Assert.False(manager.Close(second));
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void WorkspaceStoreRoundTripPreservesSessionAndUiState()
        {
            WithWorkspace((location, workspace, root) =>
            {
                workspace.OpenDocSets.Add("docs\\Vital.DocSets");
                workspace.ActiveDocSet = "docs\\Vital.DocSets";
                workspace.SourceRootOverrides["vital/backend"] = "D:\\Sources\\Backend";
                workspace.Ui.ActiveViewId = "recent";
                workspace.Ui.PropertiesDockLayout = "layout";
                workspace.Ui.SelectedItemIds["vital"] = new System.Collections.Generic.List<string> { "save" };
                var store = new JsonDocSetsWorkspaceStore(new RecordingLogger());

                store.SaveAsync(location, workspace).GetAwaiter().GetResult();
                var restored = store.LoadAsync(location).GetAwaiter().GetResult();

                Assert.Equal("docs\\Vital.DocSets", restored.ActiveDocSet);
                Assert.Equal("D:\\Sources\\Backend", restored.SourceRootOverrides["vital/backend"]);
                Assert.Equal("recent", restored.Ui.ActiveViewId);
                Assert.Equal("layout", restored.Ui.PropertiesDockLayout);
                Assert.Equal("save", restored.Ui.SelectedItemIds["vital"][0]);
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void WorkspaceStoreRejectsUnsupportedVersion()
        {
            WithWorkspace((location, workspace, root) =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(location.FilePath));
                File.WriteAllText(location.FilePath,
                    "{\"format\":\"DocSetsWorkspace\",\"formatVersion\":99,\"openDocSets\":[]}");
                Assert.Throws<NotSupportedException>(() =>
                    new JsonDocSetsWorkspaceStore(new RecordingLogger()).LoadAsync(location).GetAwaiter().GetResult());
            });
        }

        private static void WithWorkspace(Action<DocSetsWorkspaceLocation, DocSetsWorkspace, string> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var location = DocSetsWorkspaceLocation.ForSolution(Path.Combine(root, "Vital.sln"));
                action(location, new DocSetsWorkspace(), root);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private sealed class RecordingLogger : IDocSetsLogger
        {
            public void Trace(string category, string message) { }
            public void Info(string category, string message) { }
            public void Warning(string category, string message) { }
            public void Error(string category, string message, Exception exception = null) { }
        }
    }
}
