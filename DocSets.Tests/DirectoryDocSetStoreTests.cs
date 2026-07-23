using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using DocSets.Import;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class DirectoryDocSetStoreTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void CreateSaveAndOpenRoundTripUsesDirectoryManifest()
        {
            WithTemporaryDocSet((store, directory) =>
            {
                var created = store.CreateAsync(directory, "vital", "Vital").GetAwaiter().GetResult();
                created.Sources.Add(new CodeSource
                {
                    Id = "backend",
                    Name = "Backend",
                    Type = CodeSourceType.Solution,
                    Root = "../Backend",
                    Path = "Backend.sln"
                });
                created.Items.Add(new DocSetItemStorageDto
                {
                    Id = "save",
                    Name = "Save",
                    SourceId = "backend",
                    Symbol = "Backend.Service.Save",
                    Content = "**Important**",
                    ContentFormat = ContentFormat.Markdown
                });

                store.SaveAsync(directory, created).GetAwaiter().GetResult();
                var restored = store.OpenAsync(directory).GetAwaiter().GetResult();

                Assert.Equal("DocSets", restored.Format);
                Assert.Equal(1, restored.FormatVersion);
                Assert.Equal("../Backend", restored.Sources[0].Root);
                Assert.Equal("**Important**", restored.Items[0].Content);
                Assert.True(File.Exists(Path.Combine(directory, DirectoryDocSetStore.ManifestFileName)));
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void OpenRejectsLegacyJsonWithoutFormatMarker()
        {
            WithTemporaryDocSet((store, directory) =>
            {
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, DirectoryDocSetStore.ManifestFileName),
                    "{\"items\":[],\"activeSet\":\"old\"}");

                Assert.Throws<InvalidDataException>(() =>
                    store.OpenAsync(directory).GetAwaiter().GetResult());
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SaveRejectsInlineAndExternalContentTogether()
        {
            WithTemporaryDocSet((store, directory) =>
            {
                var manifest = new DocSetManifest { Id = "vital", Name = "Vital" };
                manifest.Items.Add(new DocSetItemStorageDto
                {
                    Id = "note",
                    Name = "Note",
                    Content = "inline",
                    ContentPath = "content/note.docx",
                    ContentFormat = ContentFormat.Docx
                });

                Assert.Throws<InvalidDataException>(() =>
                    store.SaveAsync(directory, manifest).GetAwaiter().GetResult());
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void StoreRequiresExplicitDocSetsDirectory()
        {
            var store = new DirectoryDocSetStore();
            var path = Path.Combine(Path.GetTempPath(), "ordinary-directory-" + Guid.NewGuid().ToString("N"));
            Assert.Throws<ArgumentException>(() =>
                store.CreateAsync(path, "id", "Name").GetAwaiter().GetResult());
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ManifestSerializesContentAndNeverLegacyComment()
        {
            var manifest = new DocSetManifest { Id = "vital", Name = "Vital" };
            manifest.Items.Add(new DocSetItemStorageDto
            {
                Id = "note",
                Name = "Note",
                Content = "text"
            });

            var json = JsonConvert.SerializeObject(manifest);

            Assert.True(json.Contains("\"content\":\"text\""));
            Assert.False(json.Contains("\"comment\""));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LegacyImporterMovesCommentProjectTagsAndEditorStateToNewManifest()
        {
            var legacy = new DocumentSetsState();
            legacy.Tags.Add(new TagDefinition { Id = "todo", Name = "Todo", Icon = "Task" });
            var folder = new DocumentItem { Id = "services", Name = "Services", NodeType = NodeType.Folder, Type = BookmarkType.Empty };
            folder.Children.Add(new DocumentItem
            {
                Id = "save",
                Name = "Save",
                Type = BookmarkType.Symbol,
                Symbol = "Backend.Service.Save",
                Project = "Backend.Core",
                Content = "legacy note",
                TagIds = new System.Collections.Generic.List<string> { "todo" },
                EditorState = new EditorState { CodePreview = "void Save()" }
            });
            legacy.Sets.Add(folder);

            var converted = LegacyDocSetConverter.Convert(legacy, new ImportOptions
            {
                InputPath = "Vital.docsets.json",
                SourceId = "backend",
                SourceName = "Backend",
                SourceRoot = "../Backend",
                SolutionPath = "Backend.sln"
            });

            Assert.Equal("Vital", converted.Name);
            Assert.Equal("backend", converted.Sources[0].Id);
            Assert.Equal(2, converted.Items.Count);
            Assert.Equal("legacy note", converted.Items[1].Content);
            Assert.Equal(ContentFormat.Markdown, converted.Items[1].ContentFormat);
            Assert.Equal("Backend.Core", converted.Items[1].Project);
            Assert.Equal("todo", converted.Items[1].TagIds[0]);
            Assert.Equal("void Save()", converted.Items[1].EditorState.CodePreview);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SourceLocatorResolvesRootRelativeToDocSetDirectoryAndLogsStatus()
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.Tests", Guid.NewGuid().ToString("N"));
            var docSet = Path.Combine(root, "Vital.DocSets");
            var solution = Path.Combine(root, "Vital.sln");
            Directory.CreateDirectory(docSet);
            File.WriteAllText(solution, "");
            try
            {
                var logger = new RecordingLogger();
                var status = new CodeSourceLocator(logger).Locate(docSet, new CodeSource
                {
                    Id = "vital",
                    Name = "Vital",
                    Type = CodeSourceType.Solution,
                    Root = "..",
                    Path = "Vital.sln"
                });

                Assert.True(status.Exists);
                Assert.Equal(Path.GetFullPath(solution), status.ResolvedPath);
                Assert.True(logger.LastMessage.Contains("найден"));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void EmptySourceRootMeansDocSetDirectory()
        {
            var directory = Path.Combine(Path.GetTempPath(), "Example.DocSets");
            var status = new CodeSourceLocator(new RecordingLogger()).Locate(directory, new CodeSource
            {
                Id = "local",
                Type = CodeSourceType.Directory,
                Root = ""
            });

            Assert.Equal(Path.GetFullPath(directory), status.ResolvedRoot);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void SourceLocatorResolvesItemsBySourceIdAndChoosesMostSpecificSourceForNewFile()
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.Tests", Guid.NewGuid().ToString("N"));
            var docSet = Path.Combine(root, "Example.DocSets");
            var sourceA = Path.Combine(root, "Code");
            var sourceB = Path.Combine(sourceA, "Nested");
            Directory.CreateDirectory(docSet);
            Directory.CreateDirectory(sourceB);
            try
            {
                var locator = new CodeSourceLocator(new RecordingLogger());
                var statuses = locator.LocateAll(docSet, new[]
                {
                    new CodeSource { Id = "a", Type = CodeSourceType.Directory, Root = "../Code" },
                    new CodeSource { Id = "b", Type = CodeSourceType.Directory, Root = "../Code/Nested" }
                });
                var file = Path.Combine(sourceB, "Program.cs");

                Assert.Equal(file, locator.ResolveItemPath(statuses, "b", "Program.cs", docSet));
                Assert.Equal("b", locator.FindForFile(statuses, file).Source.Id);
                Assert.Equal("Program.cs", locator.MakeRelativePath(statuses[1], file));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void EmptySourceIdAlwaysUsesDefaultSource()
        {
            var locator = new CodeSourceLocator(new RecordingLogger());
            var root = Path.Combine(Path.GetTempPath(), "DocSets.Tests", Guid.NewGuid().ToString("N"));
            var statuses = new[]
            {
                new CodeSourceStatus { Source = new CodeSource { Id = "other" }, ResolvedRoot = Path.Combine(root, "Other") },
                new CodeSourceStatus { Source = new CodeSource { Id = "default" }, ResolvedRoot = Path.Combine(root, "Main") }
            };

            Assert.Equal(Path.Combine(root, "Main", "Program.cs"),
                locator.ResolveItemPath(statuses, "", "Program.cs", root));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void JsonTransferRebasesDefaultReferenceBetweenDifferentDocSetRoots()
        {
            var service = new SourceReferenceService();
            var repository = Path.Combine(Path.GetTempPath(), "Repository");
            var source = Context(new SourceReferenceRoot { Id = "default", Root = repository, IsDefault = true });
            var target = Context(new SourceReferenceRoot
            {
                Id = "default", Root = Path.Combine(repository, "src"), IsDefault = true
            });
            var sourceId = "";
            var path = Path.Combine("src", "Program.cs");

            service.Rebase(source, target, ref sourceId, ref path);

            Assert.Equal("", sourceId);
            Assert.Equal("Program.cs", path);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void JsonTransferUsesSecondarySourceAndTransformsMarkdownFileLink()
        {
            var service = new SourceReferenceService();
            var repository = Path.Combine(Path.GetTempPath(), "SharedRepository");
            var source = Context(new SourceReferenceRoot { Id = "default", Root = repository, IsDefault = true });
            var target = Context(
                new SourceReferenceRoot { Id = "default", Root = Path.Combine(Path.GetTempPath(), "Other"), IsDefault = true },
                new SourceReferenceRoot { Id = "shared", Root = repository });
            var sourceId = "";
            var path = Path.Combine("lib", "Shared.cs");

            service.Rebase(source, target, ref sourceId, ref path);
            var markdown = service.RebaseMarkdownFileLinks(
                "См. [Shared](file:lib\\Shared.cs)", source, target);

            Assert.Equal("shared", sourceId);
            Assert.Equal(Path.Combine("lib", "Shared.cs"), path);
            Assert.Equal("См. [Shared](file:shared|lib\\Shared.cs)", markdown);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void JsonTransferTransformsHtmlFileLinkForHtmlNotes()
        {
            var service = new SourceReferenceService();
            var repository = Path.Combine(Path.GetTempPath(), "SharedRepository");
            var source = Context(new SourceReferenceRoot { Id = "default", Root = repository, IsDefault = true });
            var target = Context(
                new SourceReferenceRoot { Id = "default", Root = Path.Combine(Path.GetTempPath(), "Other"), IsDefault = true },
                new SourceReferenceRoot { Id = "shared", Root = repository });

            var html = service.RebaseMarkdownFileLinks(
                "<p><a href=\"file:lib\\Shared.cs\">Shared</a></p>", source, target);

            Assert.Equal("<p><a href=\"file:shared|lib\\Shared.cs\">Shared</a></p>", html);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void JsonTransferKeepsAbsolutePathOnlyWhenTargetHasNoMatchingSource()
        {
            var service = new SourceReferenceService();
            var sourceRoot = Path.Combine(Path.GetTempPath(), "SourceRepository");
            var source = Context(new SourceReferenceRoot { Id = "default", Root = sourceRoot, IsDefault = true });
            var target = Context(new SourceReferenceRoot
            {
                Id = "default", Root = Path.Combine(Path.GetTempPath(), "TargetRepository"), IsDefault = true
            });
            var sourceId = "";
            var path = "Program.cs";

            service.Rebase(source, target, ref sourceId, ref path);

            Assert.Equal("", sourceId);
            Assert.Equal(Path.Combine(sourceRoot, "Program.cs"), path);
        }

        private static SourceReferenceContext Context(params SourceReferenceRoot[] sources)
            => new SourceReferenceContext { Sources = sources.ToList() };

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ImageAssetsUseContentHashDeduplicateAndRejectPathEscape()
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.Tests", Guid.NewGuid().ToString("N"), "Images.DocSets");
            Directory.CreateDirectory(root);
            try
            {
                var service = new AssetStorageService();
                var bytes = new byte[] { 1, 2, 3, 4, 5 };
                var first = service.SaveImageAsync(root, bytes, "image/png", "screen.png").GetAwaiter().GetResult();
                var second = service.SaveImageAsync(root, bytes, "image/png", "other.png").GetAwaiter().GetResult();

                Assert.Equal(first, second);
                Assert.True(first.StartsWith("asset:images/"));
                Assert.True(File.Exists(service.ResolveAssetPath(root, first)));
                Assert.Equal(first, service.FindReferences("Текст ![image](" + first + ")")[0]);
                Assert.True(bytes.SequenceEqual(service.Read(root, first)));
                var embedded = "Текст ![image](data:image/png;base64," + Convert.ToBase64String(bytes) + ")";
                var normalized = service.ImportEmbeddedImagesAsync(root, embedded).GetAwaiter().GetResult();
                Assert.False(normalized.Contains("data:image"));
                Assert.True(normalized.Contains(first));
                var html = "<img src=\"data:image/png;base64," + Convert.ToBase64String(bytes) +
                    "\" alt=\"Схема\" width=\"640\" height=\"480\">";
                var normalizedHtml = service.ImportEmbeddedImagesAsync(root, html).GetAwaiter().GetResult();
                Assert.Equal("<img src=\"" + first +
                    "\" alt=\"Схема\" width=\"640\" height=\"480\">", normalizedHtml);
                Assert.Equal(normalizedHtml,
                    service.ImportEmbeddedImagesAsync(root, normalizedHtml).GetAwaiter().GetResult());
                Assert.Throws<System.IO.InvalidDataException>(() =>
                    service.ResolveAssetPath(root, "asset:../docsets.json"));
            }
            finally
            {
                var testDirectory = Directory.GetParent(root)?.FullName;
                if (!string.IsNullOrWhiteSpace(testDirectory) && Directory.Exists(testDirectory))
                    Directory.Delete(testDirectory, true);
            }
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DocumentRepositoryMapsManifestTreeContentAndSourcesToRuntimeAndBack()
        {
            WithTemporaryDocSet((store, directory) =>
            {
                var manifest = store.CreateAsync(directory, "vital", "Vital").GetAwaiter().GetResult();
                manifest.Sources.Add(new CodeSource { Id = "src", Name = "Source", Type = CodeSourceType.Directory, Root = "." });
                manifest.Items.Add(new DocSetItemStorageDto
                {
                    Id = "folder", Name = "Folder", NodeType = NodeType.Folder, Type = BookmarkType.Empty
                });
                manifest.Items.Add(new DocSetItemStorageDto
                {
                    Id = "note", ParentId = "folder", Name = "Note", SourceId = "src",
                    Content = "initial", ContentFormat = ContentFormat.Markdown
                });
                store.SaveAsync(directory, manifest).GetAwaiter().GetResult();

                var repository = new DocSetDocumentRepository(store, null, new RecordingLogger());
                var document = repository.OpenAsync(directory).GetAwaiter().GetResult();

                Assert.Equal("src", document.Sources[0].Id);
                Assert.Equal("initial", document.State.Sets[0].Children[0].Content);
                document.State.Sets[0].Children[0].Content = "changed";
                repository.SaveAsync(document).GetAwaiter().GetResult();

                var restored = store.OpenAsync(directory).GetAwaiter().GetResult();
                Assert.Equal("changed", restored.Items[1].Content);
                Assert.Equal(ContentFormat.Markdown, restored.Items[1].ContentFormat);
                Assert.Equal("src", restored.Items[1].SourceId);
            });
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ContentServiceRejectsFormatsNotSupportedByVersionOne()
        {
            var service = new DocSetContentService();
            Assert.Throws<NotSupportedException>(() => service.LoadAsync(".", new DocSetItemStorageDto
            {
                Id = "docx", ContentFormat = ContentFormat.Docx, ContentPath = "content/docx.docx"
            }).GetAwaiter().GetResult());
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DocumentRepositoryRoundTripsEmbeddedHtmlWithoutTreatingItAsMarkdown()
        {
            WithTemporaryDocSet((store, directory) =>
            {
                var manifest = store.CreateAsync(directory, "html", "HTML notes").GetAwaiter().GetResult();
                manifest.Items.Add(new DocSetItemStorageDto
                {
                    Id = "rich-note",
                    Name = "Rich note",
                    ContentFormat = ContentFormat.Html,
                    Content = "<h2>Заголовок</h2><table><tbody><tr><td>Ячейка</td></tr></tbody></table>"
                });
                store.SaveAsync(directory, manifest).GetAwaiter().GetResult();

                var repository = new DocSetDocumentRepository(store, null, new RecordingLogger());
                var document = repository.OpenAsync(directory).GetAwaiter().GetResult();
                var item = document.State.Sets[0];

                Assert.Equal(ContentFormat.Html, item.ContentFormat);
                Assert.True(item.Content.Contains("<table>"));
                item.Content += "<p style=\"color:red\">Текст</p>";
                repository.SaveAsync(document).GetAwaiter().GetResult();

                var restored = store.OpenAsync(directory).GetAwaiter().GetResult().Items[0];
                Assert.Equal(ContentFormat.Html, restored.ContentFormat);
                Assert.True(restored.Content.Contains("color:red"));
            });
        }

        private static void WithTemporaryDocSet(Action<DirectoryDocSetStore, string> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.Tests", Guid.NewGuid().ToString("N"));
            var directory = Path.Combine(root, "Vital.DocSets");
            try
            {
                action(new DirectoryDocSetStore(), directory);
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        private sealed class RecordingLogger : IDocSetsLogger
        {
            public string LastMessage { get; private set; } = "";
            public void Trace(string category, string message) => LastMessage = message ?? "";
            public void Info(string category, string message) => LastMessage = message ?? "";
            public void Warning(string category, string message) => LastMessage = message ?? "";
            public void Error(string category, string message, Exception exception = null) => LastMessage = message ?? "";
        }
    }
}
