using DocSets.Desktop.OneNote;
using Microsoft.Office.Interop.OneNote;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class OneNoteImportIntegrationTests
    {
        [TestMethod]
        public void ImportsLocalNotebookThroughComAndProductionService()
            => ImportsLocalNotebookThroughComAndProductionService(
                @"D:\-Projects-\VS\DocSets\-OneNote.Test-\SigmaIT-Собеседования");

        public void ImportsLocalNotebookThroughComAndProductionService(string notebookPath)
        {
            VerifyInternalLinkConversion();
            var notebookDirectory = Directory.Exists(notebookPath)
                ? notebookPath
                : File.Exists(notebookPath) && string.Equals(Path.GetExtension(notebookPath), ".onetoc2",
                    StringComparison.OrdinalIgnoreCase)
                    ? Path.GetDirectoryName(Path.GetFullPath(notebookPath))
                    : null;
            if (notebookDirectory == null)
                throw new DirectoryNotFoundException("Тестовая записная книжка не найдена: " + notebookPath);

            Console.WriteLine("OneNote test notebook: " + notebookPath);
            var hierarchyXml = RunStage("COM OpenHierarchy/GetHierarchy(section files)",
                () => ReadLocalSections(notebookDirectory));
            XDocument hierarchy;
            try { hierarchy = XDocument.Parse(hierarchyXml); }
            catch (System.Xml.XmlException)
            {
                hierarchy = XDocument.Parse("<root>" + hierarchyXml + "</root>");
            }
            var sections = hierarchy.Descendants().Count(x => x.Name.LocalName == "Section");
            var pages = hierarchy.Descendants().Where(x => x.Name.LocalName == "Page").ToList();
            var nestedPages = pages.Count(page =>
                int.TryParse(Attribute(page, "pageLevel"), out var level) && level > 1);
            if (pages.Count == 0)
                Console.WriteLine("Hierarchy without pages: " +
                    hierarchyXml.Substring(0, Math.Min(4000, hierarchyXml.Length)));
            Assert.True(sections > 0, "COM hierarchy не содержит разделов.");
            Assert.True(pages.Count > 0, "COM hierarchy не содержит страниц.");
            Console.WriteLine($"COM hierarchy: sections={sections}, pages={pages.Count}, " +
                              $"parents with nested pages={nestedPages}");

            RunStage("Inspect formatting and images", () =>
            {
                InspectPages(pages.Select(page => Attribute(page, "ID")).ToArray());
                return true;
            });

            var firstPageId = Attribute(pages[0], "ID");
            var pageXml = RunStage("COM GetPageContent(first page)", () => ReadPage(firstPageId));
            Assert.True(pageXml.IndexOf("<one:Page", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        pageXml.IndexOf(":Page", StringComparison.OrdinalIgnoreCase) >= 0,
                "GetPageContent не вернул XML страницы.");
            Console.WriteLine("First page XML: " + pageXml.Length + " chars");

            var savedImages = 0;
            var savedAttachments = 0;
            var service = new OneNoteImportService((bytes, mime, name) =>
            {
                Assert.True(bytes != null && bytes.Length > 0, "Импортёр передал пустое изображение.");
                savedImages++;
                return System.Threading.Tasks.Task.FromResult("asset:test/" + name);
            }, (bytes, name) =>
            {
                Assert.True(bytes != null && bytes.Length > 0,
                    "Импортёр передал пустой вложенный файл.");
                Assert.False(string.IsNullOrWhiteSpace(name),
                    "Импортёр потерял исходное имя вложенного файла.");
                savedAttachments++;
                return System.Threading.Tasks.Task.FromResult("asset:files/test-" + name);
            });
            var result = RunStage("Production ImportLocalNotebookAsync", () =>
                service.ImportLocalNotebookAsync(notebookPath, new Progress<OneNoteImportProgress>(value =>
                    Console.WriteLine($"  page {value.Current}/{value.Total}: {value.Message}")),
                CancellationToken.None).GetAwaiter().GetResult());
            Assert.NotNull(result.Root, "Импортёр не создал корневую папку.");
            Assert.True(result.Pages > 0, "Импортёр не создал ни одной заметки.");
            Assert.True(result.Images > 0, "Импортёр не сохранил изображения, хотя они есть в тестовой notebook.");
            Assert.Equal(result.Images, Enumerate(result.Root).Sum(item =>
                    CountOccurrences(item.Content, "<img ")),
                "Не для каждого сохранённого изображения создан HTML-элемент img.");
            if (nestedPages > 0)
            {
                Assert.False(Enumerate(result.Root).Any(item =>
                        item.Children.Count > 0 && item.NodeType != NodeType.Folder),
                    "Узел с вложенными страницами не отмечен как Folder.");
                Assert.True(Enumerate(result.Root).Any(item =>
                        item.NodeType == NodeType.Folder && item.Children.Count > 0 &&
                        item.ContentFormat == ContentFormat.Html &&
                        !string.IsNullOrWhiteSpace(item.Content)),
                    "Родительская страница OneNote не преобразована в Folder.");
            }
            Assert.True(Enumerate(result.Root).Any(item =>
                    (item.Content ?? "").IndexOf("margin-left:", StringComparison.OrdinalIgnoreCase) >= 0),
                "Импортированный HTML не содержит сохранённых отступов OneNote.");
            VerifyImportedInternalLinks(result);
            Assert.Equal(result.Images, savedImages, "Счётчик сохранённых изображений не совпал.");
            Assert.True(result.Attachments > 0,
                "Импортёр не обнаружил вложенный документ в тестовой книге OneNote.");
            Assert.Equal(result.Attachments, savedAttachments,
                "Не все вложенные документы были переданы в asset-хранилище.");
            Assert.Equal(result.Attachments, Enumerate(result.Root).Sum(item =>
                    CountOccurrences(item.Content, "data-docsets-attachment=")),
                "Не для каждого вложения создан HTML-элемент DocSets.");
            Console.WriteLine($"Import result: root={result.Root.Name}, folders={result.Folders}, " +
                              $"pages={result.Pages}, images={result.Images}, attachments={result.Attachments}, links={result.InternalLinks}, " +
                              $"unresolved links={result.UnresolvedInternalLinks}, failed={result.FailedPages}");
            if (result.Errors.Count > 0)
                Console.WriteLine(string.Join(Environment.NewLine, result.Errors));
        }

        private static void VerifyImportedInternalLinks(OneNoteImportResult result)
        {
            var items = Enumerate(result.Root).ToList();
            var targets = items.Where(item => !string.IsNullOrWhiteSpace(item.Id))
                .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var links = items.SelectMany(item => Regex.Matches(item.Content ?? string.Empty,
                    "href=[\\\"'](?<url>https://docsets\\.local/bookmark/[^\\\"']+)[\\\"']",
                    RegexOptions.IgnoreCase).Cast<Match>())
                .Select(match => new Uri(match.Groups["url"].Value))
                .ToList();

            Assert.Equal(3, links.Count,
                "В -OneNote.Test- должны импортироваться три внутренние ссылки.");
            Assert.Equal(3, result.InternalLinks,
                "Счётчик разрешённых ссылок не совпал с тестовой книгой.");
            Assert.Equal(0, result.UnresolvedInternalLinks,
                "Внутренние ссылки тестовой книги разрешены не полностью.");
            Assert.False(items.Any(item => Regex.IsMatch(item.Content ?? string.Empty,
                    "href=[\\\"']onenote:", RegexOptions.IgnoreCase)),
                "После импорта остались не преобразованные внутренние ссылки OneNote.");

            foreach (var link in links)
            {
                var targetId = Uri.UnescapeDataString(
                    link.AbsolutePath.Substring("/bookmark/".Length));
                Assert.True(targets.TryGetValue(targetId, out var target),
                    "Ссылка указывает на отсутствующий узел DocSets: " + targetId);
                if (string.IsNullOrWhiteSpace(link.Fragment)) continue;
                var anchor = Uri.UnescapeDataString(link.Fragment.TrimStart('#'));
                Assert.True((target.Content ?? string.Empty).IndexOf(
                                "id=\"" + anchor + "\"", StringComparison.OrdinalIgnoreCase) >= 0,
                    "Ссылка указывает на отсутствующий текстовый объект: " + anchor +
                    ". Фактические якоря: " + string.Join(", ", Regex.Matches(
                            target.Content ?? string.Empty, "id=\"(?<id>[^\"]+)\"")
                        .Cast<Match>().Select(match => match.Groups["id"].Value).Take(20)));
            }
        }

        private static void VerifyInternalLinkConversion()
        {
            const string pageId = "{83409AAA-3104-43DA-A05C-B9AE83813310}";
            const string sectionId = "{BA1973BB-DD6B-4CAC-9769-B927D6E22690}";
            const string objectId = "{233C1803-0552-08ED-2DDA-774AFE125A5B}";
            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [pageId] = "docsets-page",
                [sectionId] = "docsets-section"
            };
            var source = "<a href=\"onenote:#Page&amp;section-id=" + sectionId +
                         "&amp;page-id=" + pageId + "&amp;object-id=" + objectId +
                         "&amp;12&amp;base-path=D:\\Notebook\\Section.one\">page object</a> " +
                         "<a href='onenote:#Section&amp;section-id=" + sectionId +
                         "&amp;end'>section</a> " +
                         "<a href=\"onenote:#Outside&amp;page-id={OUTSIDE}&amp;end\">outside</a>";

            var converted = OneNoteImportService.RewriteOneNoteLinks(
                source, targets, out var resolved, out var unresolved);

            Assert.Equal(2, resolved, "Не разрешены ссылки OneNote на страницу и раздел.");
            Assert.Equal(1, unresolved, "Не учтена ссылка за пределы импортируемой книги.");
            Assert.True(converted.Contains(
                    "https://docsets.local/bookmark/docsets-page#onenote-object-233c1803-0552-08ed-2dda-774afe125a5b"),
                "Ссылка на объект страницы не содержит DocSets bookmark и HTML-якорь.");
            Assert.True(converted.Contains("https://docsets.local/bookmark/docsets-section"),
                "Ссылка на раздел не преобразована в DocSets bookmark.");
            Assert.True(converted.Contains("page-id={OUTSIDE}"),
                "Неразрешённая ссылка OneNote должна быть сохранена без потери адреса.");
        }

        private static string ReadLocalSections(string path)
            => WithApplication(application =>
            {
                var tableOfContents = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .FirstOrDefault(file => string.Equals(
                        Path.GetExtension(file), ".onetoc2", StringComparison.OrdinalIgnoreCase));
                var sectionPaths = Directory.GetFiles(path, "*", SearchOption.TopDirectoryOnly)
                    .Where(file => string.Equals(
                        Path.GetExtension(file), ".one", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                foreach (var sectionPath in sectionPaths)
                {
                    application.OpenHierarchy(sectionPath, null, out var openedSectionId,
                        CreateFileType.cftNone);
                    Console.WriteLine("  opened section: " + Path.GetFileName(sectionPath) +
                                      " => " + openedSectionId);
                }

                if (tableOfContents != null)
                {
                    application.GetHierarchy(null, HierarchyScope.hsNotebooks, out var notebooksXml,
                        XMLSchema.xs2013);
                    var notebooks = XDocument.Parse(notebooksXml);
                    foreach (var available in notebooks.Descendants().Where(element =>
                                 element.Name.LocalName == "Notebook"))
                        Console.WriteLine("  available notebook: " + Attribute(available, "name") +
                                          " | " + Attribute(available, "path"));
                    var expectedName = Path.GetFileNameWithoutExtension(tableOfContents);
                    var notebook = notebooks.Descendants().FirstOrDefault(element =>
                        element.Name.LocalName == "Notebook" &&
                        (string.Equals(Attribute(element, "name"), expectedName,
                             StringComparison.OrdinalIgnoreCase) ||
                         IsSameDirectory(Attribute(element, "path"), path)));
                    if (notebook != null)
                    {
                        var notebookId = Attribute(notebook, "ID");
                        application.GetHierarchy(notebookId, HierarchyScope.hsPages,
                            out var notebookXml, XMLSchema.xs2013);
                        Console.WriteLine("  notebook: " + Attribute(notebook, "name") +
                                          " => " + notebookId);
                        return notebookXml;
                    }
                }

                var documents = new System.Text.StringBuilder();
                foreach (var sectionPath in sectionPaths)
                {
                    application.OpenHierarchy(sectionPath, null, out var sectionId, CreateFileType.cftNone);
                    var xml = ReadHierarchyWhenPagesAreAvailable(application, sectionId);
                    var document = XDocument.Parse(xml);
                    documents.Append(document.Root);
                    Console.WriteLine("  section: " + Path.GetFileName(sectionPath) + " => " + sectionId);
                }
                return documents.ToString();
            });

        private static string ReadHierarchyWhenPagesAreAvailable(
            IApplication application, string sectionId)
        {
            string xml = "";
            for (var attempt = 0; attempt < 20; attempt++)
            {
                application.GetHierarchy(sectionId, HierarchyScope.hsPages, out xml,
                    XMLSchema.xs2013);
                var hierarchy = XDocument.Parse(xml);
                if (hierarchy.Descendants().Any(element => element.Name.LocalName == "Page") ||
                    string.Equals(Attribute(hierarchy.Root, "areAllPagesAvailable"), "true",
                        StringComparison.OrdinalIgnoreCase))
                    return xml;
                Thread.Sleep(500);
            }
            return xml;
        }

        private static bool IsSameDirectory(string notebookPath, string expectedDirectory)
        {
            if (string.IsNullOrWhiteSpace(notebookPath)) return false;
            try
            {
                var candidate = Directory.Exists(notebookPath)
                    ? notebookPath
                    : Path.GetDirectoryName(notebookPath);
                return string.Equals(
                    Path.GetFullPath(candidate ?? "").TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(expectedDirectory).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static string ReadPage(string pageId)
            => WithApplication(application =>
            {
                application.GetPageContent(pageId, out var xml, PageInfo.piBasic, XMLSchema.xs2013);
                return xml;
            });

        private static void InspectPages(string[] pageIds)
        {
            WithApplication(application =>
            {
                var imageCount = 0;
                var imageDataCount = 0;
                var styledOutlineCount = 0;
                string imageSample = null;
                string outlineSample = null;
                foreach (var pageId in pageIds)
                {
                    application.GetPageContent(pageId, out var xml, PageInfo.piAll, XMLSchema.xs2013);
                    var document = XDocument.Parse(xml);
                    foreach (var image in document.Descendants().Where(x => x.Name.LocalName == "Image"))
                    {
                        imageCount++;
                        if (image.Elements().Any(x => x.Name.LocalName == "Data" && !string.IsNullOrWhiteSpace(x.Value)))
                            imageDataCount++;
                        if (imageSample == null) imageSample = image.ToString().Substring(0, Math.Min(1200, image.ToString().Length));
                    }
                    foreach (var outline in document.Descendants().Where(x => x.Name.LocalName == "OE" &&
                                 (x.Attribute("style") != null || x.Attribute("alignment") != null)))
                    {
                        styledOutlineCount++;
                        if (outlineSample == null) outlineSample = outline.ToString().Substring(0, Math.Min(1200, outline.ToString().Length));
                    }
                }
                Console.WriteLine($"Formatting scan: styled OE={styledOutlineCount}, images={imageCount}, images with Data={imageDataCount}");
                Assert.True(imageCount > 0, "В тестовой notebook ожидались изображения.");
                Assert.Equal(imageCount, imageDataCount, "Не для всех изображений OneNote вернул Data при piAll.");
                if (outlineSample != null) Console.WriteLine("OE sample: " + outlineSample);
                if (imageSample != null) Console.WriteLine("Image sample: " + imageSample);
                return true;
            });
        }

        private static T WithApplication<T>(Func<IApplication, T> action)
        {
            object instance = null;
            try
            {
                instance = new Application();
                return action((IApplication)instance);
            }
            finally
            {
                if (instance != null && Marshal.IsComObject(instance))
                    Marshal.FinalReleaseComObject(instance);
            }
        }

        private static T RunStage<T>(string name, Func<T> action)
        {
            Console.WriteLine("START " + name);
            try
            {
                var value = action();
                Console.WriteLine("PASS  " + name);
                return value;
            }
            catch (COMException exception)
            {
                throw new InvalidOperationException(
                    $"FAIL {name}: HRESULT 0x{exception.HResult:X8}: {exception.Message}", exception);
            }
        }

        private static string Attribute(XElement element, string localName)
            => element.Attributes().FirstOrDefault(x => x.Name.LocalName == localName)?.Value ?? "";

        private static int CountOccurrences(string value, string marker)
        {
            var count = 0;
            var offset = 0;
            while (!string.IsNullOrEmpty(value) &&
                   (offset = value.IndexOf(marker, offset, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                offset += marker.Length;
            }
            return count;
        }

        private static System.Collections.Generic.IEnumerable<DocumentItem> Enumerate(DocumentItem item)
        {
            if (item == null) yield break;
            yield return item;
            foreach (var child in item.Children)
                foreach (var descendant in Enumerate(child))
                    yield return descendant;
        }
    }
}
