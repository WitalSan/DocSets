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
using Microsoft.Office.Interop.OneNote;

namespace DocSets.Desktop.OneNote;

/// <summary>Experimental OneNote Desktop COM reader. No OneNote types leak outside this service.</summary>
internal sealed class OneNoteImportService
{
    private const int HierarchyNotebooks = 2;
    private const int HierarchyPages = 4;
    private readonly Func<byte[], string, string, Task<string>> _saveImage;

    public OneNoteImportService(Func<byte[], string, string, Task<string>> saveImage)
    {
        _saveImage = saveImage ?? throw new ArgumentNullException(nameof(saveImage));
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
        string notebookDirectory,
        IProgress<OneNoteImportProgress> progress,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notebookDirectory) || !Directory.Exists(notebookDirectory))
            throw new DirectoryNotFoundException("Папка записной книжки не найдена: " + notebookDirectory);
        return RunStaAsync<OneNoteImportResult>(application =>
            (OneNoteImportResult)ImportLocalCore(application, notebookDirectory, progress, cancellationToken),
            cancellationToken);
    }

    private OneNoteImportResult ImportLocalCore(IApplication application, string notebookDirectory,
        IProgress<OneNoteImportProgress> progress, CancellationToken cancellationToken)
    {
        var sectionPaths = Directory.GetFiles(notebookDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(path => string.Equals(Path.GetExtension(path), ".one", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var result = new OneNoteImportResult { Root = Folder(Path.GetFileName(notebookDirectory)) };
        var sectionDocuments = new List<XDocument>();
        var totalPages = 0;
        foreach (var sectionPath in sectionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            application.OpenHierarchy(sectionPath, null, out string sectionId, CreateFileType.cftNone);
            var xml = (string)GetHierarchy(application, sectionId, HierarchyPages,
                cancellationToken, "чтение раздела " + Path.GetFileName(sectionPath));
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            sectionDocuments.Add(document);
            totalPages += document.Descendants().Count(x => x.Name.LocalName == "Page");
        }

        var currentPage = 0;
        for (var index = 0; index < sectionDocuments.Count; index++)
        {
            var document = sectionDocuments[index];
            var section = document.Root?.Name.LocalName == "Section"
                ? document.Root
                : document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Section");
            if (section == null) continue;
            var folder = Folder(NameOf(section, Path.GetFileNameWithoutExtension(sectionPaths[index])));
            result.Root.Children.Add(folder);
            result.Folders++;
            foreach (var child in section.Elements())
                ImportHierarchyNode(application, child, folder, result, totalPages, ref currentPage,
                    progress, cancellationToken);
        }
        return result;
    }

    private OneNoteImportResult ImportCore(
        IApplication application,
        OneNoteNotebook notebook,
        IProgress<OneNoteImportProgress> progress,
        CancellationToken cancellationToken)
    {
        var hierarchyXml = (string)GetHierarchy(application, notebook.Id, HierarchyPages,
            cancellationToken, "чтение структуры записной книжки");
        var hierarchy = XDocument.Parse(hierarchyXml, LoadOptions.PreserveWhitespace);
        var sourceRoot = hierarchy.Descendants().FirstOrDefault(element =>
            element.Name.LocalName == "Notebook" && Attribute(element, "ID") == notebook.Id)
            ?? hierarchy.Descendants().FirstOrDefault(element => element.Name.LocalName == "Notebook")
            ?? throw new InvalidOperationException("OneNote не вернул структуру выбранной записной книжки.");

        var result = new OneNoteImportResult
        {
            Root = Folder(NameOf(sourceRoot, notebook.Name))
        };
        var pages = sourceRoot.Descendants().Count(element => element.Name.LocalName == "Page");
        var current = 0;
        foreach (var child in sourceRoot.Elements())
            ImportHierarchyNode(application, child, result.Root, result, pages, ref current, progress, cancellationToken);
        result.Cancelled = cancellationToken.IsCancellationRequested;
        return result;
    }

    private void ImportHierarchyNode(
        IApplication application,
        XElement source,
        DocumentItem targetParent,
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
            var folder = Folder(NameOf(source, kind == "Section" ? "Раздел" : "Группа разделов"));
            targetParent.Children.Add(folder);
            result.Folders++;
            foreach (var child in source.Elements())
                ImportHierarchyNode(application, child, folder, result, totalPages, ref currentPage, progress, cancellationToken);
            return;
        }

        if (kind != "Page") return;
        currentPage++;
        var pageName = NameOf(source, "Страница");
        progress?.Report(new OneNoteImportProgress
        {
            Current = currentPage,
            Total = totalPages,
            Message = pageName
        });
        try
        {
            application.GetPageContent(Attribute(source, "ID"), out string pageXml,
                PageInfo.piAll, XMLSchema.xs2013);
            var page = XDocument.Parse(pageXml, LoadOptions.PreserveWhitespace);
            var html = ConvertPage(page, pageName, result, cancellationToken);
            targetParent.Children.Add(new DocumentItem
            {
                Name = pageName,
                NodeType = NodeType.Item,
                Type = BookmarkType.Empty,
                ContentFormat = ContentFormat.Html,
                Content = html
            });
            result.Pages++;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            result.FailedPages++;
            result.Errors.Add(pageName + ": " + exception.Message);
            DocSetsLog.Current.Error("OneNote", "Не удалось импортировать страницу '" + pageName + "'.", exception);
        }
    }

    private string ConvertPage(XDocument page, string pageName, OneNoteImportResult result,
        CancellationToken cancellationToken)
    {
        var body = new StringBuilder();
        var pageElement = page.Descendants().FirstOrDefault(x => x.Name.LocalName == "Page");
        var title = pageElement?.Elements().FirstOrDefault(x => x.Name.LocalName == "Title");
        if (title != null)
        {
            var titleText = string.Concat(title.Descendants().Where(x => x.Name.LocalName == "T").Select(x => x.Value));
            if (!string.IsNullOrWhiteSpace(titleText)) body.Append("<h1>").Append(titleText).Append("</h1>");
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
            body.Append(ConvertChildren(children, result, cancellationToken, 0));
            if (margin > 0) body.Append("</div>");
        }
        return body.ToString();
    }

    private string ConvertChildren(XElement children, OneNoteImportResult result,
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
            var content = ConvertOutlineElement(element, result, cancellationToken, depth);
            var style = BuildParagraphStyle(element, listKind == null && depth > 0 ? 24 : 0);
            output.Append(listKind == null ? "<div" + style + ">" : "<li" + style + ">").Append(content)
                .Append(listKind == null ? "</div>" : "</li>");
        }
        if (openList != null) output.Append("</").Append(openList).Append('>');
        return output.ToString();
    }

    private string ConvertOutlineElement(XElement element, OneNoteImportResult result,
        CancellationToken cancellationToken, int depth)
    {
        var output = new StringBuilder();
        foreach (var child in element.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "T":
                    output.Append(child.Value); // OneNote stores formatted text as an HTML fragment.
                    break;
                case "Table":
                    output.Append(ConvertTable(child, result, cancellationToken, depth));
                    break;
                case "Image":
                    output.Append(ConvertImage(child, result, cancellationToken));
                    break;
                case "OEChildren":
                    output.Append(ConvertChildren(child, result, cancellationToken, depth + 1));
                    break;
            }
        }
        return output.Length == 0 ? "<br>" : output.ToString();
    }

    private string ConvertTable(XElement table, OneNoteImportResult result,
        CancellationToken cancellationToken, int depth)
    {
        var output = new StringBuilder("<table><tbody>");
        foreach (var row in table.Elements().Where(x => x.Name.LocalName == "Row"))
        {
            output.Append("<tr>");
            foreach (var cell in row.Elements().Where(x => x.Name.LocalName == "Cell"))
            {
                var children = cell.Elements().FirstOrDefault(x => x.Name.LocalName == "OEChildren");
                output.Append("<td>").Append(children == null ? "" : ConvertChildren(children, result, cancellationToken, depth)).Append("</td>");
            }
            output.Append("</tr>");
        }
        return output.Append("</tbody></table>").ToString();
    }

    private string ConvertImage(XElement image, OneNoteImportResult result,
        CancellationToken cancellationToken)
    {
        var data = image.Elements().FirstOrDefault(x => x.Name.LocalName == "Data")?.Value;
        if (string.IsNullOrWhiteSpace(data)) return "";
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = Convert.FromBase64String(data);
        var mime = Attribute(image, "format");
        if (string.IsNullOrWhiteSpace(mime)) mime = "image/png";
        if (!mime.Contains('/')) mime = "image/" + mime.ToLowerInvariant().TrimStart('.');
        var extension = mime.Split('/').LastOrDefault() ?? "png";
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
        }
        var reference = _saveImage(bytes, mime, "onenote-image." + extension).GetAwaiter().GetResult();
        result.Images++;
        var alt = image.Elements().FirstOrDefault(x => x.Name.LocalName == "OCRText")?.Value ?? "";
        return "<img src=\"" + WebUtility.HtmlEncode(reference) + "\" alt=\"" + WebUtility.HtmlEncode(alt) + "\">";
    }

    private static DocumentItem Folder(string name) => new()
    {
        Name = name,
        NodeType = NodeType.Folder,
        Type = BookmarkType.Empty,
        ContentFormat = ContentFormat.Html,
        IsExpanded = true
    };

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
