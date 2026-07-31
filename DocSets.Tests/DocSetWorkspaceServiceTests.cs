using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class DocSetWorkspaceServiceTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void UnavailableSolutionLeavesWorkspaceClosed()
        {
            var context = new MutableSolutionContextService(SolutionContext.Unavailable);
            var workspace = new DocSetWorkspaceService(context);

            Assert.False(workspace.EnsureInitializedAsync().GetAwaiter().GetResult());
            Assert.False(workspace.HasOpenDocSet);
            Assert.Equal(0, workspace.GetWorkspacesAsync().GetAwaiter().GetResult().Count);
            workspace.SaveAsync(new DocumentSetsState()).GetAwaiter().GetResult();
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CreateSaveAndRestoreActiveDocSet()
        {
            WithTemporaryDirectory(root =>
            {
                var context = new MutableSolutionContextService(CreateContext(root, "First"));
                var docSetDirectory = Path.Combine(root, "First.DocSets");
                var workspace = new DocSetWorkspaceService(context);

                Assert.True(workspace.CreateDocSetAsync(docSetDirectory, "First").GetAwaiter().GetResult());
                var state = workspace.LoadAsync().GetAwaiter().GetResult();
                state.Sets.Add(new DocumentItem
                {
                    Id = "saved",
                    Name = "Saved",
                    NodeType = NodeType.Folder,
                    Type = BookmarkType.Empty
                });
                workspace.SaveAsync(state).GetAwaiter().GetResult();

                var restoredWorkspace = new DocSetWorkspaceService(context);
                var restored = restoredWorkspace.LoadAsync().GetAwaiter().GetResult();

                Assert.True(restoredWorkspace.HasOpenDocSet);
                Assert.True(restored.Sets.Any(x => x.Id == "saved"));
                Assert.True(File.Exists(restoredWorkspace.StateFilePath));
                Assert.False(restoredWorkspace.IsSharedWorkspace);
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ChangingSolutionClearsActiveDocSet()
        {
            WithTemporaryDirectory(root =>
            {
                var firstRoot = Path.Combine(root, "First");
                var secondRoot = Path.Combine(root, "Second");
                Directory.CreateDirectory(firstRoot);
                Directory.CreateDirectory(secondRoot);

                var context = new MutableSolutionContextService(CreateContext(firstRoot, "First"));
                var workspace = new DocSetWorkspaceService(context);
                Assert.True(workspace.CreateDocSetAsync(
                    Path.Combine(firstRoot, "First.DocSets"), "First").GetAwaiter().GetResult());

                context.Set(CreateContext(secondRoot, "Second"));
                Assert.True(workspace.EnsureInitializedAsync().GetAwaiter().GetResult());

                Assert.False(workspace.HasOpenDocSet);
                Assert.Equal("", workspace.StateFilePath);
                Assert.Equal(0, workspace.GetWorkspacesAsync().GetAwaiter().GetResult().Count);
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ImageAssetsRoundTripThroughCurrentDocSet()
        {
            WithTemporaryDirectory(root =>
            {
                var context = new MutableSolutionContextService(CreateContext(root, "Assets"));
                var workspace = new DocSetWorkspaceService(context);
                Assert.True(workspace.CreateDocSetAsync(
                    Path.Combine(root, "Assets.DocSets"), "Assets").GetAwaiter().GetResult());

                var content = new byte[] { 1, 2, 3, 4, 5 };
                var reference = workspace.SaveImageAssetAsync(content, "image/png", "image.png")
                    .GetAwaiter().GetResult();

                Assert.SequenceEqual(content, workspace.ReadAsset(reference));
                Assert.Equal("image/png", workspace.GetAssetMimeType(reference));
                Assert.True(workspace.FindAssetReferences("![image](" + reference + ")")
                    .Contains(reference));
            });
        }

        private static SolutionContext CreateContext(string directory, string name)
        {
            var solutionFile = Path.Combine(directory, name + ".sln");
            File.WriteAllText(solutionFile, "");
            return new SolutionContext(true, name, directory, solutionFile);
        }

        private static void WithTemporaryDirectory(Action<string> action)
        {
            var directory = Path.Combine(Path.GetTempPath(), "DocSetsWorkspaceTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                action(directory);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        private sealed class MutableSolutionContextService : ISolutionContextService
        {
            private SolutionContext _current;

            public MutableSolutionContextService(SolutionContext current)
            {
                _current = current;
            }

            public SolutionContext Current => _current;

            public Task<SolutionContext> GetCurrentAsync()
            {
                return Task.FromResult(_current);
            }

            public void Set(SolutionContext current)
            {
                _current = current;
            }
        }
    }
}
