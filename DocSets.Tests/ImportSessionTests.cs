using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class ImportSessionTests
    {
        [TestMethod]
        public void SessionStoreRoundTripsAndMarksAbandonedRunInterrupted()
        {
            var directory = Path.Combine(Path.GetTempPath(), "import-session-" + Guid.NewGuid().ToString("N") + ".docsets");
            try
            {
                Directory.CreateDirectory(directory);
                var store = new ImportSessionStore();
                var session = new ImportSessionState
                {
                    Name = "OneNote - Test", SourceId = "notebook", Status = ImportSessionStatus.Running,
                    ProgressCurrent = 3, ProgressTotal = 8
                };
                session.Pages.Add(new ImportPageState { OneNotePageId = "page", DocSetsNodeId = "note", Status = ImportPageStatus.Imported });
                session.ObjectLinkCache.Add(new ImportObjectLinkCacheEntry { PageId = "page", SourceObjectId = "object", HyperlinkObjectId = "target", Succeeded = true });

                store.SaveAsync(directory, session).GetAwaiter().GetResult();
                var restored = store.LoadAllAsync(directory).GetAwaiter().GetResult().Single();

                Assert.Equal(session.Id, restored.Id);
                Assert.Equal(ImportSessionStatus.Interrupted, restored.Status);
                Assert.Equal("note", restored.Pages.Single().DocSetsNodeId);
                Assert.Equal("target", restored.ObjectLinkCache.Single().HyperlinkObjectId);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [TestMethod]
        public void ImportTreeUsesLocalSystemNodesAndDeletesOnlySessionMetadata()
        {
            var state = new DocumentSetsState();
            var service = new ImportSessionTreeService();
            var session = new ImportSessionState { Id = "job", Name = "OneNote - Test", Status = ImportSessionStatus.Paused };

            service.Attach(state, new[] { session });

            Assert.True(service.Root.IsImportsRoot);
            Assert.True(service.Root.IsLocalOnly);
            Assert.True(service.Root.Children.Single().IsImportSession);
            Assert.Equal("job", service.Root.Children.Single().ImportSessionId);
            Assert.True(ImportSessionTreeService.IsManagedNode(service.Root));
            Assert.True(ImportSessionTreeService.IsManagedNode(service.Root.Children.Single()));
            Assert.False(ImportSessionTreeService.IsManagedNode(new DocumentItem()));
            DocumentTreeChangedEventArgs removed = null;
            state.Root.TreeChanged += (_, args) =>
            {
                if (args.Kind == DocumentTreeChangeKind.Removed) removed = args;
            };
            Assert.True(service.RemoveNode("job"));
            Assert.Equal(0, service.Root.Children.Count);
            Assert.NotNull(removed);
            Assert.True(removed.Item.IsImportSession);
            Assert.Equal("job", removed.Item.ImportSessionId);
            Assert.NotNull(service.Find("job"));
            Assert.True(service.Forget("job"));
            Assert.True(service.Find("job") == null);
        }

        [TestMethod]
        public void ImportTreeSummaryUsesOverallProgressAsSingleSourceOfTruth()
        {
            var state = new DocumentSetsState();
            var service = new ImportSessionTreeService();
            var session = new ImportSessionState
            {
                Id = "job",
                Name = "OneNote - Test",
                Status = ImportSessionStatus.Running,
                ProgressCurrent = 10,
                ProgressTotal = 10,
                OverallProgressPercent = 90
            };

            service.Attach(state, new[] { session });

            var content = service.Root.Children.Single().Content;
            Assert.True(content.Contains("90%"));
            Assert.False(content.Contains("100%"));
        }

        [TestMethod]
        public void ResumeKeepsPersistedProgressUntilNewPagesAdvanceIt()
        {
            var session = new ImportSessionState
            {
                Status = ImportSessionStatus.Paused,
                ProgressCurrent = 7,
                ProgressTotal = 10
            };

            ImportSessionStateMachine.StartOrResume(session);
            ImportSessionStateMachine.ApplyProgress(session, 1, 10);

            Assert.Equal(ImportSessionStatus.Running, session.Status);
            Assert.Equal(7, session.ProgressCurrent);
            Assert.Equal(10, session.ProgressTotal);

            ImportSessionStateMachine.ApplyProgress(session, 8, 10);
            Assert.Equal(8, session.ProgressCurrent);
        }

        [TestMethod]
        public void PauseRequestsCancellationImmediatelyAndOnlyOnce()
        {
            var session = new ImportSessionState { Status = ImportSessionStatus.Running };
            var cancellationCalls = 0;
            ImportSessionStatus statusSeenByCancellation = ImportSessionStatus.Created;

            var requested = ImportSessionStateMachine.RequestPause(session, () =>
            {
                cancellationCalls++;
                statusSeenByCancellation = session.Status;
            });
            var requestedAgain = ImportSessionStateMachine.RequestPause(session, () => cancellationCalls++);

            Assert.True(requested);
            Assert.False(requestedAgain);
            Assert.Equal(1, cancellationCalls);
            Assert.Equal(ImportSessionStatus.Pausing, statusSeenByCancellation);
            Assert.Equal("Ожидание безопасной контрольной точки", session.Stage);

            ImportSessionStateMachine.CompletePause(session);
            Assert.Equal(ImportSessionStatus.Paused, session.Status);
        }

        [TestMethod]
        public void ObjectLinkInspectorFindsMissingAndResolvedAnchors()
        {
            var root = new DocumentItem { Id = "root" };
            var source = new DocumentItem
            {
                Id = "source",
                Content = "<a href=\"https://docsets.local/bookmark/target#onenote-object-required\">link</a>"
            };
            var target = new DocumentItem { Id = "target", Content = "<p>target</p>" };
            root.Children.Add(source);
            root.Children.Add(target);

            Assert.True(ImportSessionLinkInspector.HasUnresolvedObjectLinks(root));

            target.Content = "<p id=\"onenote-object-required\">target</p>";
            Assert.False(ImportSessionLinkInspector.HasUnresolvedObjectLinks(root));
        }
    }
}
