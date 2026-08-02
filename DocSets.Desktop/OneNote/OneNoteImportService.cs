using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Diagnostics;
using Microsoft.Office.Interop.OneNote;

namespace DocSets.Desktop.OneNote;

/// <summary>Experimental OneNote Desktop COM reader. No OneNote types leak outside this service.</summary>
internal sealed class OneNoteImportService
{
    internal const string ProfileRoot = "Весь импорт";
    internal const string ProfileHierarchy = ProfileRoot + "/Загрузка иерархии через COM";
    internal const string ProfilePageContent = ProfileRoot + "/Импорт страниц/COM: GetPageContent";
    internal const string ProfileXml = ProfileRoot + "/Импорт страниц/Разбор XML";
    internal const string ProfileHtml = ProfileRoot + "/Импорт страниц/Конвертация в HTML (CPU)";
    internal const string ProfileLinks = ProfileRoot + "/Импорт страниц/Обработка внутренних ссылок";
    internal const string ProfileAnchorCom = ProfileRoot + "/Импорт страниц/COM: целевые объектные якоря";
    internal const string ProfileReportCom = ProfileRoot + "/Импорт страниц/COM: ссылки диагностического отчёта";
    internal const string ProfileTags = ProfileRoot + "/Импорт страниц/Импорт тегов";
    internal const string ProfileImages = ProfileRoot + "/Импорт страниц/Извлечение изображений";
    internal const string ProfileAttachments = ProfileRoot + "/Импорт страниц/Извлечение вложений";
    internal const string ProfileNodes = ProfileRoot + "/Создание узлов DocSets";
    internal const string ProfileAssets = ProfileRoot + "/Сохранение assets";
    internal const string ProfileSave = ProfileRoot + "/Сохранение DocSet";
    internal const string ProfileUi = ProfileRoot + "/Обновление дерева и UI";
    private const int HierarchyNotebooks = 2;
    private const int HierarchyPages = 4;
    private readonly Func<byte[], string, string, Task<string>> _saveImage;
    private readonly Func<byte[], string, Task<string>> _saveFile;
    private readonly OneNoteImportProfile _profile;
    private OneNotePageTiming _currentPageTiming;
    private readonly Dictionary<string, ImportedPageAnchorState> _importedAnchorPages =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PendingObjectAnchor> _pendingObjectAnchors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _sourceLinkCache =
        new(StringComparer.OrdinalIgnoreCase);
    private IApplication _reportApplication;
    private OneNoteImportResult _reportResult;
    private string _reportPageId = "";
    private string _reportNodeId = "";
    private IReadOnlyDictionary<string, string> _reportAnchors =
        new Dictionary<string, string>();
    private IReadOnlyDictionary<string, ImportedTagDefinition> _pageTagDefinitions =
        new Dictionary<string, ImportedTagDefinition>();
    private static readonly Regex HrefPattern = new(
        "(?<prefix>\\bhref\\s*=\\s*)(?<quote>[\\\"'])(?<value>.*?)(\\k<quote>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public OneNoteImportService(Func<byte[], string, string, Task<string>> saveImage,
        Func<byte[], string, Task<string>> saveFile = null,
        OneNoteImportProfile profile = null)
    {
        _saveImage = saveImage ?? throw new ArgumentNullException(nameof(saveImage));
        _saveFile = saveFile ?? ((_, name) => throw new InvalidOperationException(
            "Хранилище вложенных файлов не настроено: " + name));
        _profile = profile ?? new OneNoteImportProfile();
    }

    public Task<IReadOnlyList<OneNoteNotebook>> GetNotebooksAsync(CancellationToken cancellationToken)
        => RunStaAsync(application =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var hierarchyXml = (string)GetHierarchy(application, null, HierarchyNotebooks,
                cancellationToken, "получение списка записных книжек");
            var document = XDocument.Parse(hierarchyXml, LoadOptions.PreserveWhitespace);
            return (IReadOnlyList<OneNoteNotebook>)document.Descendants()
                .Where(element => element.Name.LocalName == "Notebook")
                .Select(element => new OneNoteNotebook(
                    Attribute(element, "ID"), NameOf(element, "Записная книжка")))
                .Where(notebook => !string.IsNullOrWhiteSpace(notebook.Id))
                .ToList();
        }, cancellationToken);

    public Task<OneNoteImportResult> ImportAsync(
        OneNoteNotebook notebook,
        IProgress<OneNoteImportProgress> progress,
        CancellationToken cancellationToken)
        => RunStaAsync<OneNoteImportResult>(
            application => (OneNoteImportResult)ImportCore(application, notebook, progress, cancellationToken),
            cancellationToken);

    internal Task<OneNoteImportResult> ImportLocalNotebookAsync(
        string notebookPath,
        IProgress<OneNoteImportProgress> progress,
        CancellationToken cancellationToken)
    {
        var notebookDirectory = ResolveNotebookDirectory(notebookPath);
        return RunStaAsync<OneNoteImportResult>(application =>
            (OneNoteImportResult)ImportLocalCore(application, notebookDirectory, progress, cancellationToken),
            cancellationToken);
    }

    private static string ResolveNotebookDirectory(string notebookPath)
    {
        if (!string.IsNullOrWhiteSpace(notebookPath) && Directory.Exists(notebookPath))
            return Path.GetFullPath(notebookPath);
        if (!string.IsNullOrWhiteSpace(notebookPath) && File.Exists(notebookPath) &&
            string.Equals(Path.GetExtension(notebookPath), ".onetoc2", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(Path.GetFullPath(notebookPath));
        throw new DirectoryNotFoundException("Записная книжка OneNote не найдена: " + notebookPath);
    }

    private OneNoteImportResult ImportLocalCore(IApplication application, string notebookDirectory,
        IProgress<OneNoteImportProgress> progress, CancellationToken cancellationToken)
    {
        ResetDeferredAnchors();
        string[] sectionPaths;
        using (_profile.Measure(ProfileHierarchy))
            sectionPaths = Directory.GetFiles(notebookDirectory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".one", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        OneNoteImportResult result;
        using (_profile.Measure(ProfileNodes))
            result = new OneNoteImportResult { Root = Folder(Path.GetFileName(notebookDirectory)) };
        result.Report.Profile = _profile;
        result.Report.NotebookName = result.Root.Name;
        result.Report.ImportedRootNodeId = result.Root.Id;
        RecordHierarchy(result, application, "Notebook", result.Root.Name, "", result.Root.Id);
        var sectionDocuments = new List<XDocument>();
        var totalPages = 0;
        foreach (var sectionPath in sectionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string xml;
            using (_profile.Measure(ProfileHierarchy))
            {
                application.OpenHierarchy(sectionPath, null, out string sectionId, CreateFileType.cftNone);
                xml = (string)GetHierarchy(application, sectionId, HierarchyPages,
                    cancellationToken, "чтение раздела " + Path.GetFileName(sectionPath));
            }
            XDocument document;
            using (_profile.Measure(ProfileXml))
                document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            sectionDocuments.Add(document);
            totalPages += document.Descendants().Count(x => x.Name.LocalName == "Page");
        }

        OneNoteLinkMap links;
        using (_profile.Measure(ProfileHierarchy))
            links = OneNoteLinkMap.Create(application, sectionDocuments.SelectMany(document =>
                document.Root == null ? Enumerable.Empty<XElement>() : document.Root.DescendantsAndSelf()));

        var currentPage = 0;
        for (var index = 0; index < sectionDocuments.Count; index++)
        {
            var document = sectionDocuments[index];
            var section = document.Root?.Name.LocalName == "Section"
                ? document.Root
                : document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Section");
            if (section == null) continue;
            DocumentItem folder;
            using (_profile.Measure(ProfileNodes))
            {
                folder = Folder(NameOf(section, Path.GetFileNameWithoutExtension(sectionPaths[index])),
                    links.IdFor(section));
                result.Root.Children.Add(folder);
            }
            result.Folders++;
            RecordHierarchy(result, application, "Section", folder.Name,
                Attribute(section, "ID"), folder.Id);
            ImportHierarchyChildren(application, section.Elements(), folder, links, result, totalPages,
                ref currentPage, progress, cancellationToken);
        }
        ResolveDeferredAnchors(application, cancellationToken);
        return result;
    }

    private OneNoteImportResult ImportCore(
        IApplication application,
        OneNoteNotebook notebook,
        IProgress<OneNoteImportProgress> progress,
        CancellationToken cancellationToken)
    {
        ResetDeferredAnchors();
        string hierarchyXml;
        using (_profile.Measure(ProfileHierarchy))
            hierarchyXml = (string)GetHierarchy(application, notebook.Id, HierarchyPages,
                cancellationToken, "чтение структуры записной книжки");
        XDocument hierarchy;
        using (_profile.Measure(ProfileXml))
            hierarchy = XDocument.Parse(hierarchyXml, LoadOptions.PreserveWhitespace);
        var sourceRoot = hierarchy.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Notebook" && Attribute(element, "ID") == notebook.Id)
            ?? hierarchy.Descendants().FirstOrDefault(element => element.Name.LocalName == "Notebook")
            ?? throw new InvalidOperationException("OneNote не вернул структуру выбранной записной книжки.");

        OneNoteLinkMap links;
        using (_profile.Measure(ProfileHierarchy))
            links = OneNoteLinkMap.Create(application, sourceRoot.DescendantsAndSelf());
        OneNoteImportResult result;
        using (_profile.Measure(ProfileNodes))
            result = new OneNoteImportResult
            {
                Root = Folder(NameOf(sourceRoot, notebook.Name), links.IdFor(sourceRoot))
            };
        result.Report.Profile = _profile;
        result.Report.NotebookName = result.Root.Name;
        result.Report.NotebookId = notebook.Id;
        result.Report.ImportedRootNodeId = result.Root.Id;
        RecordHierarchy(result, application, "Notebook", result.Root.Name, notebook.Id, result.Root.Id);
        var pages = sourceRoot.Descendants().Count(element => element.Name.LocalName == "Page");
        var current = 0;
        ImportHierarchyChildren(application, sourceRoot.Elements(), result.Root, links, result, pages,
            ref current, progress, cancellationToken);
        ResolveDeferredAnchors(application, cancellationToken);
        result.Cancelled = cancellationToken.IsCancellationRequested;
        return result;
    }

    private void ImportHierarchyChildren(
        IApplication application,
        IEnumerable<XElement> children,
        DocumentItem targetParent,
        OneNoteLinkMap links,
        OneNoteImportResult result,
        int totalPages,
        ref int currentPage,
        IProgress<OneNoteImportProgress> progress,
        CancellationToken cancellationToken)
    {
        var pageParents = new List<(int Level, DocumentItem Item)>();
        foreach (var child in children)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (child.Name.LocalName != "Page")
            {
                pageParents.Clear();
                ImportHierarchyNode(application, child, targetParent, links, result, totalPages,
                    ref currentPage, progress, cancellationToken);
                continue;
            }

            var level = PageLevel(child);
            while (pageParents.Count > 0 && pageParents[pageParents.Count - 1].Level >= level)
                pageParents.RemoveAt(pageParents.Count - 1);
            var parent = pageParents.Count == 0
                ? targetParent
                : pageParents[pageParents.Count - 1].Item;
            var imported = ImportHierarchyNode(application, child, parent, links, result, totalPages,
                ref currentPage, progress, cancellationToken);
            if (imported != null)
            {
                PromoteToFolderWhenItHasChildren(parent, result);
                pageParents.Add((level, imported));
            }
        }
    }

    private static void PromoteToFolderWhenItHasChildren(
        DocumentItem item,
        OneNoteImportResult result)
    {
        if (item.NodeType == NodeType.Folder || item.Children.Count == 0) return;

        item.NodeType = NodeType.Folder;
        item.IsExpanded = true;
        result.Folders++;
    }

    private DocumentItem ImportHierarchyNode(
        IApplication application,
        XElement source,
        DocumentItem targetParent,
        OneNoteLinkMap links,
        OneNoteImportResult result,
        int totalPages,
        ref int currentPage,
        IProgress<OneNoteImportProgress> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var kind = source.Name.LocalName;
        if (kind is "SectionGroup" or "Section")
        {
            DocumentItem folder;
            using (_profile.Measure(ProfileNodes))
            {
                folder = Folder(NameOf(source, kind == "Section" ? "Раздел" : "Группа разделов"),
                    links.IdFor(source));
                targetParent.Children.Add(folder);
            }
            result.Folders++;
            RecordHierarchy(result, application, kind, folder.Name, Attribute(source, "ID"), folder.Id);
            ImportHierarchyChildren(application, source.Elements(), folder, links, result, totalPages,
                ref currentPage, progress, cancellationToken);
            return folder;
        }

        if (kind != "Page") return null;
        currentPage++;
        var pageName = NameOf(source, "Страница");
        progress?.Report(new OneNoteImportProgress
        {
            Current = currentPage,
            Total = totalPages,
            Message = pageName
        });
        DocumentItem importedPage = null;
        var pageTiming = new OneNotePageTiming
        {
            PageName = pageName,
            OneNotePageId = Attribute(source, "ID")
        };
        var pageStopwatch = Stopwatch.StartNew();
        var imagesBefore = result.Images;
        var attachmentsBefore = result.Attachments;
        _currentPageTiming = pageTiming;
        try
        {
            var pageId = Attribute(source, "ID");
            var nodeId = links.IdFor(source);
            string pageXml;
            using (_profile.Measure(ProfilePageContent, pageTiming, "COM: GetPageContent"))
                application.GetPageContent(pageId, out pageXml,
                    PageInfo.piAll, XMLSchema.xs2013);
            pageTiming.XmlBytes = Encoding.UTF8.GetByteCount(pageXml ?? "");
            XDocument page;
            using (_profile.Measure(ProfileXml, pageTiming, "Разбор XML"))
                page = XDocument.Parse(pageXml, LoadOptions.PreserveWhitespace);
            pageTiming.Images = page.Descendants().Count(element => element.Name.LocalName == "Image");
            pageTiming.Attachments = page.Descendants().Count(element =>
                element.Name.LocalName == "InsertedFile");
            var reportStart = result.Report.Entries.Count;
            var conversionStopwatch = Stopwatch.StartNew();
            var measuredBefore = MeasuredPageChildren(pageTiming);
            var html = ConvertPage(application, pageId, nodeId, page, pageName,
                links, result, cancellationToken);
            conversionStopwatch.Stop();
            var converterCpu = Math.Max(0, conversionStopwatch.Elapsed.TotalMilliseconds -
                (MeasuredPageChildren(pageTiming) - measuredBefore));
            var converterCpuElapsed = TimeSpan.FromMilliseconds(converterCpu);
            _profile.Record(ProfileHtml, converterCpuElapsed);
            _profile.RecordPageStage(pageTiming, "Конвертация в HTML (CPU)", converterCpuElapsed);
            using (_profile.Measure(ProfileNodes, pageTiming, "Создание Note"))
            {
                importedPage = new DocumentItem
                {
                    Name = pageName,
                    Id = nodeId,
                    NodeType = NodeType.Item,
                    Type = BookmarkType.Empty,
                    ContentFormat = ContentFormat.Html,
                    Content = html
                };
                targetParent.Children.Add(importedPage);
            }
            RegisterImportedPage(pageId, importedPage, page);
            result.Pages++;
            var pageProblems = PrimaryProblemsSince(result.Report, reportStart);
            var pageEntry = AddReport(result, application, "Page", pageName,
                pageProblems.Count == 0 ? OneNoteImportStatus.Imported :
                    OneNoteImportStatus.ImportedWithWarnings,
                AggregateReason(pageProblems),
                pageId, "", nodeId, "");
            SetAggregate(pageEntry, pageProblems);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            result.FailedPages++;
            result.Errors.Add(pageName + ": " + exception.Message);
            DocSetsLog.Current.Error("OneNote", "Не удалось импортировать страницу '" + pageName + "'.", exception);
            var failedNodeId = links.IdFor(source);
            var reportEntry = AddReport(result, application, "Page", pageName,
                OneNoteImportStatus.NotImported, exception.Message, Attribute(source, "ID"), "",
                failedNodeId, "");
            using (_profile.Measure(ProfileNodes, pageTiming, "Создание Note"))
            {
                importedPage = new DocumentItem
                {
                    Name = pageName,
                    Id = failedNodeId,
                    NodeType = NodeType.Item,
                    Type = BookmarkType.Empty,
                    ContentFormat = ContentFormat.Html,
                    Content = DiagnosticPlaceholder(reportEntry)
                };
                targetParent.Children.Add(importedPage);
            }
            result.Pages++;
        }
        finally
        {
            pageStopwatch.Stop();
            if (pageTiming.Images == 0) pageTiming.Images = result.Images - imagesBefore;
            if (pageTiming.Attachments == 0) pageTiming.Attachments = result.Attachments - attachmentsBefore;
            pageTiming.TotalMilliseconds = pageStopwatch.Elapsed.TotalMilliseconds;
            _profile.Record(ProfileRoot + "/Импорт страниц", pageStopwatch.Elapsed);
            _profile.AddPage(pageTiming);
            _currentPageTiming = null;
            progress?.Report(new OneNoteImportProgress
            {
                Current = currentPage, Total = totalPages, Message = pageName,
                ReportSnapshot = SnapshotReport(result.Report)
            });
        }

        // В OneNote дочерние страницы представлены вложенными элементами Page.
        // DocumentItem поддерживает дочерние узлы и для заметок, поэтому сохраняем
        // эту структуру напрямую. При ошибке родительской страницы её дети всё равно
        // импортируются на ближайший успешно созданный уровень.
        var nestedPages = source.Elements().Where(element =>
            element.Name.LocalName == "Page").ToList();
        if (nestedPages.Count > 0)
            ImportHierarchyChildren(application, nestedPages, importedPage ?? targetParent, links, result,
                totalPages, ref currentPage, progress, cancellationToken);
        return importedPage;
    }

    private static double MeasuredPageChildren(OneNotePageTiming page)
        => page?.Stages.Where(pair => pair.Key is "Обработка внутренних ссылок" or
                "Импорт тегов" or "Извлечение изображений" or "Извлечение вложений" or
                "Сохранение assets" or "COM: ссылки диагностического отчёта")
            .Sum(pair => pair.Value) ?? 0;

    private OneNoteImportReport SnapshotReport(OneNoteImportReport report)
        => new()
        {
            Version = report.Version,
            CreatedUtc = report.CreatedUtc,
            NotebookName = report.NotebookName,
            NotebookId = report.NotebookId,
            ImportedRootNodeId = report.ImportedRootNodeId,
            Entries = report.Entries.ToList(),
            Profile = _profile.Snapshot()
        };

    private static int PageLevel(XElement page)
        => int.TryParse(Attribute(page, "pageLevel"), NumberStyles.Integer,
               CultureInfo.InvariantCulture, out var level)
            ? Math.Max(1, level)
            : 1;

    private string ConvertPage(IApplication application, string pageId, string nodeId, XDocument page,
        string pageName, OneNoteLinkMap links,
        OneNoteImportResult result,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        IReadOnlyDictionary<string, string> objectAnchors;
        objectAnchors = BuildPageObjectAnchors(page);
        _reportApplication = application;
        _reportResult = result;
        _reportPageId = pageId ?? "";
        _reportNodeId = nodeId ?? "";
        _reportAnchors = objectAnchors;
        using (_profile.Measure(ProfileTags, _currentPageTiming, "Импорт тегов"))
            _pageTagDefinitions = ReadTagDefinitions(page, result);
        var pageElement = page.Descendants().FirstOrDefault(x => x.Name.LocalName == "Page");
        var title = pageElement?.Elements().FirstOrDefault(x => x.Name.LocalName == "Title");
        if (title != null)
        {
            var titleText = string.Concat(title.Descendants().Where(x => x.Name.LocalName == "T").Select(x => x.Value));
            if (!string.IsNullOrWhiteSpace(titleText))
            {
                var titleOutline = title.Descendants().FirstOrDefault(x => x.Name.LocalName == "OE");
                var titleContent = titleOutline == null
                    ? WebUtility.HtmlEncode(titleText)
                    : ConvertOutlineElement(titleOutline, links, objectAnchors, result,
                        cancellationToken, 0);
                if (titleOutline != null)
                    titleContent = WrapNoteTags(titleOutline, titleContent, result);
                body.Append("<h1>").Append(titleContent).Append("</h1>");
            }
            AddCurrentReport("TextBlock", "Заголовок страницы", OneNoteImportStatus.Imported, "", title);
        }
        else if (!string.IsNullOrWhiteSpace(pageName))
        {
            body.Append("<h1>").Append(WebUtility.HtmlEncode(pageName)).Append("</h1>");
        }

        var outlines = page.Descendants().Where(x => x.Name.LocalName == "Outline").ToList();
        var positions = outlines.Select(GetOutlineX).Where(x => x.HasValue).Select(x => x.Value).ToList();
        var leftEdge = positions.Count == 0 ? 0 : positions.Min();
        foreach (var outline in outlines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var children = outline.Elements().FirstOrDefault(x => x.Name.LocalName == "OEChildren");
            if (children == null) continue;
            var x = GetOutlineX(outline);
            var margin = x.HasValue ? Math.Max(0, Math.Min(600, x.Value - leftEdge)) : 0;
            if (margin > 0) body.Append("<div style=\"margin-left:").Append(margin.ToString("0.##", CultureInfo.InvariantCulture)).Append("px\">");
            body.Append(ConvertChildren(children, links, objectAnchors, result, cancellationToken, 0));
            if (margin > 0) body.Append("</div>");
        }
        var unsupportedTopLevel = pageElement?.Elements().Where(element =>
            element.Name.LocalName is "InkDrawing" or "InkParagraph" or "InkWord" or
                "Media" or "Audio" or "Video" or "Math" or "Equation").ToList()
            ?? new List<XElement>();
        foreach (var unsupported in unsupportedTopLevel)
        {
            var entry = AddCurrentReport(IsFormula(unsupported) ? "Formula" :
                    unsupported.Name.LocalName.StartsWith("Ink", StringComparison.OrdinalIgnoreCase)
                        ? "Ink" : unsupported.Name.LocalName,
                unsupported.Name.LocalName, OneNoteImportStatus.NotImported,
                "Тип объекта OneNote пока не поддерживается.", unsupported);
            body.Append(DiagnosticPlaceholder(entry));
        }
        return body.ToString();
    }

    private string ConvertChildren(XElement children, OneNoteLinkMap links,
        IReadOnlyDictionary<string, string> objectAnchors, OneNoteImportResult result,
        CancellationToken cancellationToken, int depth)
    {
        var output = new StringBuilder();
        string openList = null;
        foreach (var element in children.Elements())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (element.Name.LocalName != "OE") continue;
            var list = element.Elements().FirstOrDefault(x => x.Name.LocalName == "List");
            var listKind = list == null ? null : (list.Elements().Any(x => x.Name.LocalName == "Number") ? "ol" : "ul");
            if (openList != listKind)
            {
                if (openList != null) output.Append("</").Append(openList).Append('>');
                if (listKind != null) output.Append('<').Append(listKind).Append('>');
                openList = listKind;
            }
            var reportStart = result.Report.Entries.Count;
            var content = ConvertOutlineElement(element, links, objectAnchors, result,
                cancellationToken, depth);
            content = WrapNoteTags(element, content, result);
            var style = BuildParagraphStyle(element, listKind == null && depth > 0 ? 24 : 0);
            var anchor = BuildObjectAnchorAttribute(element, objectAnchors);
            output.Append(listKind == null ? "<div" + anchor + style + ">" : "<li" + anchor + style + ">").Append(content)
                .Append(listKind == null ? "</div>" : "</li>");
            var objectId = Attribute(element, "objectID");
            var blockProblems = PrimaryProblemsSince(result.Report, reportStart);
            var blockEntry = AddReport(result, _reportApplication, "TextBlock", "Текстовый блок",
                blockProblems.Count == 0 ? OneNoteImportStatus.Imported :
                    OneNoteImportStatus.ImportedWithWarnings,
                AggregateReason(blockProblems),
                _reportPageId, objectId,
                _reportNodeId, AnchorFor(objectId));
            SetAggregate(blockEntry, blockProblems);
        }
        if (openList != null) output.Append("</").Append(openList).Append('>');
        return output.ToString();
    }

    private string ConvertOutlineElement(XElement element, OneNoteLinkMap links,
        IReadOnlyDictionary<string, string> objectAnchors, OneNoteImportResult result,
        CancellationToken cancellationToken, int depth)
    {
        var output = new StringBuilder();
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "T":
                    if (child.Value.IndexOf("onenote:", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        using (_profile.Measure(ProfileLinks, _currentPageTiming,
                                   "Обработка внутренних ссылок"))
                        {
                            TrackObjectLinkTargets(child.Value);
                            var convertedText = RewriteOneNoteLinks(child.Value, links.Targets,
                                out var resolved, out var unresolved);
                            output.Append(convertedText);
                            result.InternalLinks += resolved;
                            result.UnresolvedInternalLinks += unresolved;
                            RecordTextDetails(child, links, convertedText);
                        }
                    }
                    else
                    {
                        output.Append(child.Value);
                        RecordTextDetails(child, links, child.Value);
                    }
                    break;
                case "Table":
                    output.Append(ConvertTable(child, links, objectAnchors, result,
                        cancellationToken, depth));
                    break;
                case "Image":
                    output.Append(ConvertImage(child, result, cancellationToken));
                    break;
                case "InsertedFile":
                    output.Append(ConvertInsertedFile(child, result, cancellationToken));
                    break;
                case "OEChildren":
                    output.Append(ConvertChildren(child, links, objectAnchors, result,
                        cancellationToken, depth + 1));
                    break;
                case "List":
                case "Meta":
                case "Tag":
                    break;
                default:
                    var unsupported = AddCurrentReport(
                        IsFormula(child) ? "Formula" :
                        child.Name.LocalName.StartsWith("Ink", StringComparison.OrdinalIgnoreCase) ? "Ink" : child.Name.LocalName,
                        child.Name.LocalName, OneNoteImportStatus.NotImported,
                        "Тип объекта OneNote пока не поддерживается.", child);
                    output.Append(DiagnosticPlaceholder(unsupported));
                    break;
            }
        }
        return output.Length == 0 ? "<br>" : output.ToString();
    }

    private IReadOnlyDictionary<string, ImportedTagDefinition> ReadTagDefinitions(
        XDocument page, OneNoteImportResult result)
    {
        var definitions = new Dictionary<string, ImportedTagDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in page.Descendants().Where(x => x.Name.LocalName == "TagDef"))
        {
            var index = Attribute(element, "index");
            if (string.IsNullOrWhiteSpace(index)) continue;
            var type = ParseInt(Attribute(element, "type"));
            var symbol = ParseInt(Attribute(element, "symbol"));
            var name = Attribute(element, "name");
            if (string.IsNullOrWhiteSpace(name)) name = "OneNote tag " + index;
            var behavior = IsOneNoteCheckboxType(type)
                ? NoteTagBehavior.Checkbox : NoteTagBehavior.Marker;
            var icon = OneNoteTagIcon(type, symbol);
            var color = OneNoteTagColor(type, Attribute(element, "fontColor"),
                Attribute(element, "highlightColor"));
            var signature = string.Join("|", name, type, symbol, color, behavior);
            var style = new NoteTagStyle
            {
                Id = "onenote-" + StableId(signature),
                Name = name,
                Icon = icon,
                Color = color,
                Behavior = behavior,
                Source = "onenote",
                SourceId = "type=" + type + ";symbol=" + symbol
            };
            var existing = result.NoteTagStyles.FirstOrDefault(x =>
                string.Equals(x.Id, style.Id, StringComparison.OrdinalIgnoreCase));
            if (existing == null)
            {
                result.NoteTagStyles.Add(style);
                existing = style;
                AddCurrentReport("NoteTagStyle", name, OneNoteImportStatus.Imported, "", element);
            }
            definitions[index] = new ImportedTagDefinition(existing);
        }
        return definitions;
    }

    private string WrapNoteTags(XElement outlineElement, string content, OneNoteImportResult result)
    {
        var tags = outlineElement.Elements().Where(x => x.Name.LocalName == "Tag").ToList();
        if (tags.Count == 0) return content;
        using var timing = _profile.Measure(ProfileTags, _currentPageTiming, "Импорт тегов");
        var objectId = Attribute(outlineElement, "objectID");
        foreach (var element in tags.AsEnumerable().Reverse())
        {
            var index = Attribute(element, "index");
            if (!_pageTagDefinitions.TryGetValue(index, out var definition))
            {
                var missing = AddNoteTagReport("OneNote tag " + index,
                    OneNoteImportStatus.NotImported,
                    "Tag ссылается на отсутствующее определение TagDef index=" + index + ".",
                    element, objectId);
                content = DiagnosticPlaceholder(missing) + content;
                continue;
            }

            var disabled = ParseBool(Attribute(element, "disabled"));
            var sourceId = _reportPageId + "/" + objectId + "/tag-" + index;
            var style = definition.Style;
            var completed = style.Behavior == NoteTagBehavior.Marker
                ? (bool?)null : ParseBool(Attribute(element, "completed"));
            var tag = new NoteTag
            {
                Id = Guid.NewGuid().ToString("N"),
                StyleId = style.Id,
                IsCompleted = completed,
                CompletedAt = completed == true ? ParseDate(Attribute(element, "completionDate")) : null,
                Source = "onenote",
                SourceId = sourceId
            };
            var state = disabled ? "disabled" : tag.IsCompleted == true ? "completed" : "active";
            var attributes = new StringBuilder()
                .Append(" class=\"docsets-note-tag\"")
                .Append(" data-docsets-note-tag-id=\"").Append(tag.Id).Append("\"")
                .Append(" data-docsets-tag-style-id=\"").Append(WebUtility.HtmlEncode(tag.StyleId)).Append("\"")
                .Append(" data-docsets-tag-state=\"").Append(state).Append("\"")
                .Append(" data-docsets-tag-behavior=\"").Append(style.Behavior.ToString().ToLowerInvariant()).Append("\"")
                .Append(" data-docsets-tag-icon=\"").Append(WebUtility.HtmlEncode(style.Icon)).Append("\"")
                .Append(" data-docsets-tag-name=\"").Append(WebUtility.HtmlEncode(style.Name)).Append("\"")
                .Append(" data-docsets-note-tag-source=\"").Append(WebUtility.HtmlEncode(tag.Source)).Append("\"")
                .Append(" data-docsets-note-tag-source-id=\"").Append(WebUtility.HtmlEncode(tag.SourceId)).Append("\"");
            if (tag.CompletedAt.HasValue)
                attributes.Append(" data-docsets-note-tag-completed-at=\"")
                    .Append(tag.CompletedAt.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)).Append("\"");
            if (Regex.IsMatch(style.Color ?? "", "^(#[0-9a-fA-F]{3,8}|[a-zA-Z]+)$"))
                attributes.Append(" style=\"--docsets-note-tag-color:")
                    .Append(WebUtility.HtmlEncode(style.Color)).Append("\"");
            content = "<span" + attributes + "><span class=\"docsets-note-tag-icon\" contenteditable=\"false\" title=\"" +
                      WebUtility.HtmlEncode(style.Name) + "\" aria-label=\"" + WebUtility.HtmlEncode(style.Name) +
                      "\">" + WebUtility.HtmlEncode(NoteTagGlyph(style.Icon, tag.IsCompleted == true)) +
                      "</span><span class=\"docsets-note-tag-content\">" + content + "</span></span>";
            result.NoteTags++;
            AddNoteTagReport(style.Name, OneNoteImportStatus.Imported, "", element, objectId);
        }
        return content;
    }

    private OneNoteImportReportEntry AddNoteTagReport(string name, OneNoteImportStatus status,
        string reason, XElement element, string objectId)
        => AddReport(_reportResult, _reportApplication, "NoteTag", name, status, reason,
            _reportPageId, objectId, _reportNodeId, AnchorFor(objectId));

    private static int ParseInt(string value)
        => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : -1;

    private static bool ParseBool(string value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static DateTimeOffset? ParseDate(string value)
        => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var result)
            ? result : (DateTimeOffset?)null;

    private static string OneNoteTagIcon(int type, int symbol)
    {
        var builtIn = new[]
        {
            "checkbox", "important", "question", "definition", "highlight", "contact",
            "address", "phone", "website", "idea", "password", "critical", "project-a",
            "project-b", "remember", "movie", "book", "music", "article", "blog",
            "discuss", "discuss", "discuss", "email", "meeting", "callback",
            "priority-1", "priority-2", "client-request"
        };
        if (type >= 0 && type < builtIn.Length) return builtIn[type];
        return symbol switch
        {
            3 => "checkbox",
            13 => "important",
            15 => "question",
            _ => "tag"
        };
    }

    private static bool IsOneNoteCheckboxType(int type)
        => type == 0 || (type >= 20 && type <= 22) || (type >= 24 && type <= 28);

    private static string OneNoteTagColor(int type, string fontColor, string highlightColor)
    {
        if (!string.IsNullOrWhiteSpace(highlightColor) &&
            !string.Equals(highlightColor, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(highlightColor, "automatic", StringComparison.OrdinalIgnoreCase))
            return highlightColor;
        if (!string.IsNullOrWhiteSpace(fontColor) &&
            !string.Equals(fontColor, "none", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(fontColor, "automatic", StringComparison.OrdinalIgnoreCase))
            return fontColor;
        return type switch { 0 => "#107c10", 1 => "#d13438", 2 => "#0078d4", _ => "#605e5c" };
    }

    private static string NoteTagGlyph(string icon, bool completed)
        => (icon ?? "").ToLowerInvariant() switch
        {
            "checkbox" => completed ? "☑" : "☐", "important" => "★", "question" => "?",
            "definition" => "≡", "highlight" => "✎", "contact" => "●", "client-request" => "●",
            "address" => "⌂", "phone" => "☎", "callback" => "☎", "website" => "◎",
            "idea" => "☀", "password" => "⚿", "critical" => "⚠", "project-a" => "A",
            "project-b" => "B", "remember" => "●", "movie" => "▶", "book" => "▤",
            "music" => "♪", "article" => "▧", "blog" => "✎", "discuss" => "◉",
            "email" => "✉", "meeting" => "▦", "priority-1" => "1", "priority-2" => "2",
            _ => "◆"
        };

    private static string StableId(string value)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? ""))
            .Take(8).Select(x => x.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private sealed class ImportedTagDefinition
    {
        public ImportedTagDefinition(NoteTagStyle style)
        {
            Style = style;
        }

        public NoteTagStyle Style { get; }
    }

    private string ConvertTable(XElement table, OneNoteLinkMap links,
        IReadOnlyDictionary<string, string> objectAnchors, OneNoteImportResult result,
        CancellationToken cancellationToken, int depth)
    {
        var output = new StringBuilder("<table><tbody>");
        foreach (var row in table.Elements().Where(x => x.Name.LocalName == "Row"))
        {
            output.Append("<tr>");
            foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "Cell"))
            {
                var children = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "OEChildren");
                output.Append("<td>").Append(children == null ? "" : ConvertChildren(children,
                    links, objectAnchors, result, cancellationToken, depth)).Append("</td>");
            }
            output.Append("</tr>");
        }
        AddCurrentReport("Table", "Таблица", OneNoteImportStatus.Imported, "", table);
        return output.Append("</tbody></table>").ToString();
    }

    private string ConvertImage(XElement image, OneNoteImportResult result,
        CancellationToken cancellationToken)
    {
        var extraction = Stopwatch.StartNew();
        var extractionRecorded = false;
        try
        {
            var data = image.Elements().FirstOrDefault(x => x.Name.LocalName == "Data")?.Value;
            if (string.IsNullOrWhiteSpace(data))
                throw new InvalidDataException("OneNote не вернул данные изображения.");
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = Convert.FromBase64String(data);
            var mime = Attribute(image, "format");
            if (string.IsNullOrWhiteSpace(mime)) mime = "image/png";
            if (!mime.Contains('/')) mime = "image/" + mime.ToLowerInvariant().TrimStart('.');
            var extension = mime.Split('/').LastOrDefault() ?? "png";
            var sourceExtension = extension.ToUpperInvariant();
            var convertedWithLoss = false;
            if (string.Equals(extension, "emf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(extension, "wmf", StringComparison.OrdinalIgnoreCase))
            {
                using var input = new MemoryStream(bytes);
                using var bitmap = Image.FromStream(input);
                using var output = new MemoryStream();
                bitmap.Save(output, ImageFormat.Png);
                bytes = output.ToArray();
                mime = "image/png";
                extension = "png";
                convertedWithLoss = true;
            }
            extraction.Stop();
            _profile.Record(ProfileImages, extraction.Elapsed);
            _profile.RecordPageStage(_currentPageTiming, "Извлечение изображений", extraction.Elapsed);
            extractionRecorded = true;
            string reference;
            using (_profile.Measure(ProfileAssets, _currentPageTiming, "Сохранение assets"))
                reference = _saveImage(bytes, mime, "onenote-image." + extension)
                    .GetAwaiter().GetResult();
            result.Images++;
            AddCurrentReport("Image", "Изображение (" + sourceExtension + ")",
                convertedWithLoss ? OneNoteImportStatus.ConvertedWithLoss : OneNoteImportStatus.Imported,
                convertedWithLoss ? "Метафайл преобразован в PNG." : "", image);
            var alt = image.Elements().FirstOrDefault(x => x.Name.LocalName == "OCRText")?.Value ?? "";
            return "<img src=\"" + WebUtility.HtmlEncode(reference) + "\" alt=\"" + WebUtility.HtmlEncode(alt) + "\">";
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            if (!extractionRecorded)
            {
                extraction.Stop();
                _profile.Record(ProfileImages, extraction.Elapsed);
                _profile.RecordPageStage(_currentPageTiming, "Извлечение изображений", extraction.Elapsed);
            }
            var entry = AddCurrentReport("Image", "Изображение", OneNoteImportStatus.NotImported,
                exception.Message, image);
            result.Errors.Add("Изображение: " + exception.Message);
            return DiagnosticPlaceholder(entry);
        }
    }

    private string ConvertInsertedFile(XElement file, OneNoteImportResult result,
        CancellationToken cancellationToken)
    {
        var extraction = Stopwatch.StartNew();
        var extractionRecorded = false;
        cancellationToken.ThrowIfCancellationRequested();
        var name = Attribute(file, "preferredName");
        var cachePath = Attribute(file, "pathCache");
        var sourcePath = Attribute(file, "pathSource");
        if (string.IsNullOrWhiteSpace(name))
            name = Path.GetFileName(!string.IsNullOrWhiteSpace(sourcePath) ? sourcePath : cachePath);
        if (string.IsNullOrWhiteSpace(name)) name = "attachment.bin";
        try
        {
            byte[] bytes = null;
            var data = file.Elements().FirstOrDefault(element => element.Name.LocalName == "Data")?.Value;
            if (!string.IsNullOrWhiteSpace(data)) bytes = Convert.FromBase64String(data);
            var availablePath = new[] { cachePath, sourcePath }
                .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
            if (bytes == null && availablePath != null) bytes = File.ReadAllBytes(availablePath);
            if (bytes == null || bytes.Length == 0)
                throw new FileNotFoundException("OneNote не предоставил содержимое вложения.", name);
            extraction.Stop();
            _profile.Record(ProfileAttachments, extraction.Elapsed);
            _profile.RecordPageStage(_currentPageTiming, "Извлечение вложений", extraction.Elapsed);
            extractionRecorded = true;
            string reference;
            using (_profile.Measure(ProfileAssets, _currentPageTiming, "Сохранение assets"))
                reference = _saveFile(bytes, name).GetAwaiter().GetResult();
            result.Attachments++;
            AddCurrentReport("Attachment", name, OneNoteImportStatus.Imported, "", file);
            var extension = Path.GetExtension(name).TrimStart('.').ToUpperInvariant();
            var type = string.IsNullOrWhiteSpace(extension) ? "FILE" : extension;
            return "<span class=\"docsets-attachment\" data-docsets-attachment=\"" +
                WebUtility.HtmlEncode(reference) + "\" data-docsets-attachment-name=\"" +
                WebUtility.HtmlEncode(name) + "\" contenteditable=\"false\" title=\"" +
                WebUtility.HtmlEncode("Двойной щелчок — открыть " + name) +
                "\"><span class=\"docsets-attachment-icon\">📄</span>" +
                "<span class=\"docsets-attachment-type\">" + WebUtility.HtmlEncode(type) +
                "</span><span class=\"docsets-attachment-name\">" + WebUtility.HtmlEncode(name) +
                "</span></span>";
        }
        catch (Exception exception) when (!(exception is OperationCanceledException))
        {
            if (!extractionRecorded)
            {
                extraction.Stop();
                _profile.Record(ProfileAttachments, extraction.Elapsed);
                _profile.RecordPageStage(_currentPageTiming, "Извлечение вложений", extraction.Elapsed);
            }
            result.Errors.Add("Вложение " + name + ": " + exception.Message);
            DocSetsLog.Current.Warning("OneNote", "Не удалось импортировать вложение '" + name + "': " + exception.Message);
            var entry = AddCurrentReport("Attachment", name, OneNoteImportStatus.NotImported,
                exception.Message, file);
            return DiagnosticPlaceholder(entry);
        }
    }

    private void RecordTextDetails(XElement text, OneNoteLinkMap links, string convertedText)
    {
        if (IsFormula(text) || Regex.IsMatch(text.Value ?? "", "<(?:mml:)?math\\b", RegexOptions.IgnoreCase))
            AddCurrentReport("Formula", "Формула", OneNoteImportStatus.ConvertedWithLoss,
                "Формула сохранена как HTML без гарантии полного соответствия OneNote.", text);

        foreach (Match match in HrefPattern.Matches(text.Value ?? string.Empty))
        {
            var href = WebUtility.HtmlDecode(match.Groups["value"].Value);
            var status = OneNoteImportStatus.Imported;
            var reason = "";
            if (href.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase))
            {
                var pageId = LinkParameter(href, "page-id");
                var sectionId = LinkParameter(href, "section-id");
                if (!TryResolveTarget(links.Targets,
                        string.IsNullOrWhiteSpace(pageId) ? sectionId : pageId, out _))
                {
                    status = OneNoteImportStatus.NotImported;
                    reason = "Внутренняя ссылка OneNote не разрешена в импортированном дереве.";
                }
            }
            AddCurrentReport("Link", href, status, reason, text);
        }
    }

    private static bool IsFormula(XElement element)
        => element != null && (element.Name.LocalName.IndexOf("Math", StringComparison.OrdinalIgnoreCase) >= 0 ||
            element.Name.LocalName.IndexOf("Equation", StringComparison.OrdinalIgnoreCase) >= 0);

    private OneNoteImportReportEntry AddCurrentReport(string type, string name,
        OneNoteImportStatus status, string reason, XElement source)
    {
        var objectId = Attribute(source, "objectID");
        if (string.IsNullOrWhiteSpace(objectId))
            objectId = source?.AncestorsAndSelf()
                .Select(element => Attribute(element, "objectID"))
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        return AddReport(_reportResult, _reportApplication, type, name, status, reason,
            _reportPageId, objectId, _reportNodeId, AnchorFor(objectId));
    }

    private string AnchorFor(string objectId)
        => string.IsNullOrWhiteSpace(objectId) ? "" :
            (_reportAnchors.TryGetValue(objectId, out var anchor) ? anchor : ObjectAnchor(objectId));

    private void RecordHierarchy(OneNoteImportResult result, IApplication application,
        string type, string name, string sourceId, string nodeId)
        => AddReport(result, application, type, name, OneNoteImportStatus.Imported, "",
            sourceId, "", nodeId, "");

    private static List<OneNoteImportReportEntry> PrimaryProblemsSince(
        OneNoteImportReport report, int startIndex)
        => report.Entries.Skip(Math.Max(0, startIndex)).Where(entry =>
            entry.Status != OneNoteImportStatus.Imported && !entry.IsAggregate).ToList();

    private static string AggregateReason(IReadOnlyList<OneNoteImportReportEntry> problems)
    {
        if (problems == null || problems.Count == 0) return "";
        var descriptions = problems.Select(problem => problem.ObjectType +
                (string.IsNullOrWhiteSpace(problem.Name) ? "" : " «" + problem.Name + "»") +
                (string.IsNullOrWhiteSpace(problem.Reason) ? "" : ": " + problem.Reason))
            .Distinct(StringComparer.Ordinal).Take(3).ToList();
        var suffix = problems.Count > descriptions.Count ? $"; ещё {problems.Count - descriptions.Count}" : "";
        return "Содержит: " + string.Join("; ", descriptions) + suffix;
    }

    private static void SetAggregate(OneNoteImportReportEntry entry,
        IReadOnlyList<OneNoteImportReportEntry> problems)
    {
        if (entry == null || problems == null || problems.Count == 0) return;
        entry.IsAggregate = true;
        entry.RelatedProblemIds = problems.Select(problem => problem.Id).Distinct().ToList();
    }

    private OneNoteImportReportEntry AddReport(OneNoteImportResult result,
        IApplication application, string type, string name, OneNoteImportStatus status,
        string reason, string pageId, string objectId, string nodeId, string anchorId)
    {
        if (result == null) return null;
        var entry = new OneNoteImportReportEntry
        {
            ObjectType = type ?? "Unknown",
            Name = name ?? "",
            Status = status,
            Reason = reason ?? "",
            OneNotePageId = pageId ?? "",
            OneNoteObjectId = objectId ?? "",
            DocSetsNodeId = nodeId ?? "",
            DocSetsAnchorId = anchorId ?? "",
            OneNoteLink = status == OneNoteImportStatus.Imported
                ? "" : GetSourceLink(application, pageId, objectId)
        };
        result.Report.Entries.Add(entry);
        return entry;
    }

    private string GetSourceLink(IApplication application, string pageId, string objectId)
    {
        if (application == null || string.IsNullOrWhiteSpace(pageId)) return "";
        var key = pageId + "|" + (objectId ?? "");
        if (_sourceLinkCache.TryGetValue(key, out var cached)) return cached;
        try
        {
            using (_profile.Measure(ProfileReportCom, _currentPageTiming,
                       "COM: ссылки диагностического отчёта"))
                application.GetHyperlinkToObject(pageId,
                    string.IsNullOrWhiteSpace(objectId) ? null : objectId, out string hyperlink);
            return _sourceLinkCache[key] = hyperlink ?? "";
        }
        catch (COMException) { return _sourceLinkCache[key] = ""; }
    }

    private static string DiagnosticPlaceholder(OneNoteImportReportEntry entry)
    {
        if (entry == null) return "";
        return "<div class=\"docsets-onenote-diagnostic\" data-onenote-report-id=\"" +
            WebUtility.HtmlEncode(entry.Id) + "\" contenteditable=\"false\"><strong>OneNote: " +
            WebUtility.HtmlEncode(entry.ObjectType) + " не импортирован</strong><br>" +
            WebUtility.HtmlEncode(entry.Reason) + "</div>";
    }

    internal static string RewriteOneNoteLinks(
        string html,
        IReadOnlyDictionary<string, string> targets,
        out int resolved,
        out int unresolved)
    {
        var resolvedCount = 0;
        var unresolvedCount = 0;
        var rewritten = HrefPattern.Replace(html ?? string.Empty, match =>
        {
            var href = WebUtility.HtmlDecode(match.Groups["value"].Value);
            if (!href.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase))
                return match.Value;

            var pageId = LinkParameter(href, "page-id");
            var sectionId = LinkParameter(href, "section-id");
            var sourceId = string.IsNullOrWhiteSpace(pageId) ? sectionId : pageId;
            if (!TryResolveTarget(targets, sourceId, out var targetId))
            {
                unresolvedCount++;
                return match.Value;
            }

            var target = "https://docsets.local/bookmark/" + Uri.EscapeDataString(targetId);
            var objectId = LinkParameter(href, "object-id");
            if (!string.IsNullOrWhiteSpace(objectId))
                target += "#" + Uri.EscapeDataString(ObjectLinkAnchor(href, objectId));
            resolvedCount++;
            return match.Groups["prefix"].Value + match.Groups["quote"].Value +
                   WebUtility.HtmlEncode(target) + match.Groups["quote"].Value;
        });
        resolved = resolvedCount;
        unresolved = unresolvedCount;
        return rewritten;
    }

    private static bool TryResolveTarget(
        IReadOnlyDictionary<string, string> targets,
        string sourceId,
        out string targetId)
    {
        targetId = string.Empty;
        if (string.IsNullOrWhiteSpace(sourceId) || targets == null) return false;
        if (targets.TryGetValue(sourceId, out targetId)) return true;
        var canonical = CanonicalOneNoteId(sourceId);
        return !string.Equals(canonical, sourceId, StringComparison.OrdinalIgnoreCase) &&
               targets.TryGetValue(canonical, out targetId);
    }

    private static string LinkParameter(string link, string name)
    {
        var match = Regex.Match(link ?? string.Empty,
            "(?:[?&#]|^)" + Regex.Escape(name) + "=([^&#]+)",
            RegexOptions.IgnoreCase);
        if (!match.Success) return string.Empty;
        try { return Uri.UnescapeDataString(match.Groups[1].Value.Replace("+", "%20")); }
        catch (UriFormatException) { return match.Groups[1].Value; }
    }

    private static string BuildObjectAnchorAttribute(
        XElement element,
        IReadOnlyDictionary<string, string> objectAnchors)
    {
        var objectId = Attribute(element, "objectID");
        var anchor = !string.IsNullOrWhiteSpace(objectId) && objectAnchors != null &&
                     objectAnchors.TryGetValue(objectId, out var mapped)
            ? mapped
            : ObjectAnchor(objectId);
        return string.IsNullOrWhiteSpace(objectId)
            ? string.Empty
            : " id=\"" + WebUtility.HtmlEncode(anchor) + "\"";
    }

    private static IReadOnlyDictionary<string, string> BuildPageObjectAnchors(XDocument page)
    {
        var anchors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in page.Descendants().Where(element =>
                     !string.IsNullOrWhiteSpace(Attribute(element, "objectID"))))
        {
            var objectId = Attribute(element, "objectID");
            if (!anchors.ContainsKey(objectId)) anchors[objectId] = ObjectAnchor(objectId);
        }
        return anchors;
    }

    private static string ObjectLinkAnchor(string link, string objectId) => ObjectAnchor(objectId);

    private static string ObjectAnchor(string objectId)
    {
        var normalized = Regex.Replace((objectId ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", "-")
            .Trim('-');
        return "onenote-object-" + normalized;
    }

    private void ResetDeferredAnchors()
    {
        _importedAnchorPages.Clear();
        _pendingObjectAnchors.Clear();
        _sourceLinkCache.Clear();
    }

    private void TrackObjectLinkTargets(string html)
    {
        foreach (Match match in HrefPattern.Matches(html ?? ""))
        {
            var href = WebUtility.HtmlDecode(match.Groups["value"].Value);
            if (!href.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase)) continue;
            var pageId = LinkParameter(href, "page-id");
            var objectId = LinkParameter(href, "object-id");
            if (string.IsNullOrWhiteSpace(pageId) || string.IsNullOrWhiteSpace(objectId)) continue;
            var canonicalPageId = CanonicalOneNoteId(pageId);
            var key = canonicalPageId + "|" + CanonicalOneNoteId(objectId);
            if (!_pendingObjectAnchors.ContainsKey(key))
                _pendingObjectAnchors[key] = new PendingObjectAnchor(pageId, objectId);
        }
    }

    private void RegisterImportedPage(string pageId, DocumentItem item, XDocument page)
    {
        if (item == null || string.IsNullOrWhiteSpace(pageId) || page == null) return;
        var state = new ImportedPageAnchorState(pageId, item,
            page.Descendants().Select(element => Attribute(element, "objectID"))
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        _importedAnchorPages[pageId] = state;
        var canonical = CanonicalOneNoteId(pageId);
        if (!string.IsNullOrWhiteSpace(canonical)) _importedAnchorPages[canonical] = state;
    }

    private void ResolveDeferredAnchors(IApplication application, CancellationToken cancellationToken)
    {
        foreach (var pending in _pendingObjectAnchors.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_importedAnchorPages.TryGetValue(pending.PageId, out var state) &&
                !_importedAnchorPages.TryGetValue(CanonicalOneNoteId(pending.PageId), out state))
                continue;
            var targetObjectId = CanonicalOneNoteId(pending.ObjectId);
            var desiredAnchor = ObjectAnchor(targetObjectId);
            if ((state.Item.Content ?? "").IndexOf("id=\"" + desiredAnchor + "\"",
                    StringComparison.OrdinalIgnoreCase) >= 0)
                continue;

            foreach (var sourceObjectId in state.SourceObjectIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string hyperlinkObjectId;
                using (_profile.Measure(ProfileAnchorCom))
                {
                    try
                    {
                        application.GetHyperlinkToObject(state.PageId, sourceObjectId,
                            out string hyperlink);
                        hyperlinkObjectId = CanonicalOneNoteId(LinkParameter(
                            WebUtility.HtmlDecode(hyperlink), "object-id"));
                    }
                    catch (COMException) { continue; }
                }
                if (!string.Equals(hyperlinkObjectId, targetObjectId,
                        StringComparison.OrdinalIgnoreCase)) continue;
                var fallbackAnchor = ObjectAnchor(sourceObjectId);
                state.Item.Content = Regex.Replace(state.Item.Content ?? "",
                    Regex.Escape("id=\"" + fallbackAnchor + "\""),
                    _ => "id=\"" + desiredAnchor + "\"", RegexOptions.IgnoreCase);
                foreach (var entry in _reportResult?.Report?.Entries ??
                             Enumerable.Empty<OneNoteImportReportEntry>())
                    if (string.Equals(CanonicalOneNoteId(entry.OneNotePageId),
                            CanonicalOneNoteId(state.PageId), StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(entry.OneNoteObjectId, sourceObjectId,
                            StringComparison.OrdinalIgnoreCase))
                        entry.DocSetsAnchorId = desiredAnchor;
                break;
            }
        }
    }

    private sealed class ImportedPageAnchorState
    {
        public ImportedPageAnchorState(string pageId, DocumentItem item, List<string> sourceObjectIds)
        {
            PageId = pageId;
            Item = item;
            SourceObjectIds = sourceObjectIds;
        }
        public string PageId { get; }
        public DocumentItem Item { get; }
        public List<string> SourceObjectIds { get; }
    }

    private sealed class PendingObjectAnchor
    {
        public PendingObjectAnchor(string pageId, string objectId)
        {
            PageId = pageId;
            ObjectId = objectId;
        }
        public string PageId { get; }
        public string ObjectId { get; }
    }

    private static string CanonicalOneNoteId(string sourceId)
    {
        var match = Regex.Match(sourceId ?? string.Empty,
            "\\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\}",
            RegexOptions.IgnoreCase);
        return match.Success ? match.Value : sourceId ?? string.Empty;
    }

    private static DocumentItem Folder(string name, string id = "") => new()
    {
        Id = id,
        Name = name,
        NodeType = NodeType.Folder,
        Type = BookmarkType.Empty,
        ContentFormat = ContentFormat.Html,
        IsExpanded = true
    };

    private sealed class OneNoteLinkMap
    {
        private readonly Dictionary<string, string> _targets;

        private OneNoteLinkMap(Dictionary<string, string> targets) => _targets = targets;

        public IReadOnlyDictionary<string, string> Targets => _targets;

        public string IdFor(XElement element)
        {
            var sourceId = Attribute(element, "ID");
            return !string.IsNullOrWhiteSpace(sourceId) && _targets.TryGetValue(sourceId, out var id)
                ? id
                : string.Empty;
        }

        public static OneNoteLinkMap Create(IApplication application, IEnumerable<XElement> elements)
        {
            var targets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var importId = Guid.NewGuid().ToString("N");
            var index = 0;
            foreach (var element in elements ?? Enumerable.Empty<XElement>())
            {
                if (element.Name.LocalName is not ("Notebook" or "SectionGroup" or "Section" or "Page"))
                    continue;
                var sourceId = Attribute(element, "ID");
                if (string.IsNullOrWhiteSpace(sourceId) || targets.ContainsKey(sourceId)) continue;
                var targetId = "onenote-" + importId + "-" + ++index;
                targets[sourceId] = targetId;
                var canonical = CanonicalOneNoteId(sourceId);
                if (!string.IsNullOrWhiteSpace(canonical) && !targets.ContainsKey(canonical))
                    targets[canonical] = targetId;
                AddHyperlinkAliases(application, sourceId, targetId, targets);
            }
            return new OneNoteLinkMap(targets);
        }

        private static void AddHyperlinkAliases(
            IApplication application,
            string hierarchyId,
            string targetId,
            IDictionary<string, string> targets)
        {
            if (application == null || string.IsNullOrWhiteSpace(hierarchyId)) return;
            try
            {
                application.GetHyperlinkToObject(hierarchyId, null, out string hyperlink);
                foreach (var name in new[] { "section-id", "page-id" })
                {
                    var alias = LinkParameter(WebUtility.HtmlDecode(hyperlink), name);
                    if (!string.IsNullOrWhiteSpace(alias) && !targets.ContainsKey(alias))
                        targets[alias] = targetId;
                }
            }
            catch (COMException exception)
            {
                DocSetsLog.Current.Warning("OneNote",
                    "Не удалось получить постоянный hyperlink ID узла " + hierarchyId +
                    ": " + exception.Message);
            }
        }
    }

    private static string Attribute(XElement element, string localName)
        => element?.Attributes().FirstOrDefault(x => x.Name.LocalName == localName)?.Value ?? "";

    private static string NameOf(XElement element, string fallback)
        => string.IsNullOrWhiteSpace(Attribute(element, "name")) ? fallback : Attribute(element, "name");

    private static double? GetOutlineX(XElement outline)
    {
        var value = outline.Elements().FirstOrDefault(x => x.Name.LocalName == "Position")?
            .Attributes().FirstOrDefault(x => x.Name.LocalName == "x")?.Value;
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : null;
    }

    private static string BuildParagraphStyle(XElement element, int nestedMargin)
    {
        var styles = new List<string>();
        var sourceStyle = Attribute(element, "style");
        if (!string.IsNullOrWhiteSpace(sourceStyle)) styles.Add(sourceStyle.Trim().TrimEnd(';'));
        var alignment = Attribute(element, "alignment");
        if (alignment is "left" or "center" or "right" or "justify")
            styles.Add("text-align:" + alignment);
        if (nestedMargin > 0) styles.Add("margin-left:" + nestedMargin + "px");
        return styles.Count == 0 ? "" : " style=\"" + WebUtility.HtmlEncode(string.Join(";", styles)) + "\"";
    }

    private static string GetHierarchy(IApplication application, string startNodeId, int scope,
        CancellationToken cancellationToken, string stage)
    {
        // OneNote is an out-of-process COM server. Directly after activation it can
        // temporarily answer E_FAIL while the profile and open notebooks are loading.
        // The small bounded retry also handles an already running OneNote during sync.
        COMException lastError = null;
        for (var attempt = 1; attempt <= 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                application.GetHierarchy(startNodeId, (HierarchyScope)scope, out string hierarchyXml,
                    XMLSchema.xs2013);
                return hierarchyXml;
            }
            catch (COMException exception) when (IsTransientOneNoteError(exception))
            {
                lastError = exception;
                if (attempt < 8) Thread.Sleep(500);
            }
        }

        throw new InvalidOperationException(
            $"OneNote не смог выполнить этап «{stage}». " +
            "Откройте OneNote Desktop, дождитесь появления записных книжек и повторите импорт.",
            lastError);
    }

    private static bool IsTransientOneNoteError(COMException exception)
        => exception.HResult == unchecked((int)0x80004005) || // E_FAIL during OneNote startup
           exception.HResult == unchecked((int)0x80010001) || // RPC_E_CALL_REJECTED
           exception.HResult == unchecked((int)0x8001010A);   // RPC_E_SERVERCALL_RETRYLATER

    private static Task<T> RunStaAsync<T>(Func<IApplication, T> action, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object application = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                application = new Microsoft.Office.Interop.OneNote.Application();
                completion.TrySetResult(action((IApplication)application));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
            finally
            {
                if (application != null && Marshal.IsComObject(application))
                    Marshal.FinalReleaseComObject(application);
            }
        }) { IsBackground = true, Name = "DocSets OneNote import" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
