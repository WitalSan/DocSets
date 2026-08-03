namespace DocSets.Desktop.OneNote;

using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Office.Interop.OneNote;

internal sealed class OneNoteObjectIdDiagnosticService
{
    private static readonly Regex HrefPattern = new(
        "\\bhref\\s*=\\s*(?<q>[\"'])(?<href>.*?)(\\k<q>)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex GuidPattern = new(
        "\\{[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\\}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex BracedPartPattern = new(
        "\\{(?<part>[^{}]+)\\}", RegexOptions.Compiled);

    public Task<OneNoteObjectIdDiagnosticReport> RunAsync(OneNoteNotebook notebook,
        IProgress<OneNoteDiagnosticProgress> progress, CancellationToken cancellationToken)
        => RunStaAsync(application => Run(application, notebook, progress, cancellationToken),
            cancellationToken);

    private static OneNoteObjectIdDiagnosticReport Run(IApplication application,
        OneNoteNotebook notebook, IProgress<OneNoteDiagnosticProgress> progress,
        CancellationToken cancellationToken)
    {
        application.GetHierarchy(notebook.Id, HierarchyScope.hsPages, out string hierarchyXml,
            XMLSchema.xs2013);
        var hierarchy = XDocument.Parse(hierarchyXml, LoadOptions.PreserveWhitespace);
        var pageElements = hierarchy.Descendants().Where(x => x.Name.LocalName == "Page")
            .GroupBy(x => Attribute(x, "ID"), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).ToList();
        var report = new OneNoteObjectIdDiagnosticReport
        {
            NotebookId = notebook.Id,
            NotebookName = notebook.Name
        };
        var pages = new Dictionary<string, PageData>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < pageElements.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pageId = Attribute(pageElements[index], "ID");
            var pageName = Attribute(pageElements[index], "name");
            progress?.Report(new OneNoteDiagnosticProgress
            {
                Current = index + 1, Total = pageElements.Count,
                Message = "Чтение XML: " + pageName
            });
            application.GetPageContent(pageId, out string xml, PageInfo.piAll, XMLSchema.xs2013);
            var data = new PageData(pageId, pageName, XDocument.Parse(xml,
                LoadOptions.PreserveWhitespace));
            pages[pageId] = data;
            pages[Canonical(pageId)] = data;
            try
            {
                application.GetHyperlinkToObject(pageId, null, out string pageHyperlink);
                var hyperlinkPageId = Parameter(WebUtility.HtmlDecode(pageHyperlink), "page-id");
                if (!string.IsNullOrWhiteSpace(hyperlinkPageId))
                {
                    pages[hyperlinkPageId] = data;
                    pages[Canonical(hyperlinkPageId)] = data;
                }
            }
            catch (COMException)
            {
                // The hierarchy ID remains usable when OneNote cannot create a page hyperlink.
            }
            report.Pages.Add(data.Summary);
        }

        var links = pages.Values.Distinct().SelectMany(FindLinks).ToList();
        report.TotalObjectLinks = links.Count;
        var hyperlinkCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < links.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var link = links[index];
            progress?.Report(new OneNoteDiagnosticProgress
            {
                Current = index + 1, Total = links.Count,
                Message = "Проверка ссылки: " + link.Source.PageName
            });
            var entry = AnalyzeLink(application, link, pages, hyperlinkCache, cancellationToken);
            report.Entries.Add(entry);
            if (string.IsNullOrWhiteSpace(entry.ResolvedSourceObjectId)) report.NotResolvedByCom++;
            else report.ResolvedByCom++;
        }
        Summarize(report);
        return report;
    }

    private static OneNoteObjectIdDiagnosticEntry AnalyzeLink(IApplication application,
        LinkData link, IReadOnlyDictionary<string, PageData> pages,
        IDictionary<string, string> cache, CancellationToken cancellationToken)
    {
        var decoded = WebUtility.HtmlDecode(link.Href);
        var targetPageId = Parameter(decoded, "page-id");
        var linkObjectId = Parameter(decoded, "object-id");
        var entry = new OneNoteObjectIdDiagnosticEntry
        {
            SourcePageId = link.Source.PageId,
            TargetPageId = targetPageId,
            OriginalHref = link.Href,
            DecodedHref = decoded,
            LinkObjectId = linkObjectId,
            CanonicalLinkObjectId = Canonical(linkObjectId),
            LinkPositions = LinkPositions(decoded, linkObjectId)
        };
        if (!pages.TryGetValue(targetPageId, out var target) &&
            !pages.TryGetValue(Canonical(targetPageId), out target))
        {
            SetAllNotFound(entry);
            return entry;
        }

        foreach (var source in target.Objects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = target.PageId + "|" + source.ObjectId;
            if (!cache.TryGetValue(key, out var hyperlink))
            {
                try { application.GetHyperlinkToObject(target.PageId, source.ObjectId, out hyperlink); }
                catch (COMException) { hyperlink = ""; }
                cache[key] = hyperlink ?? "";
            }
            var returnedId = Parameter(WebUtility.HtmlDecode(hyperlink), "object-id");
            if (!string.Equals(Canonical(returnedId), Canonical(linkObjectId),
                    StringComparison.OrdinalIgnoreCase)) continue;
            entry.ResolvedSourceObjectId = source.ObjectId;
            entry.CanonicalSourceObjectId = Canonical(source.ObjectId);
            entry.SourcePositions = Parts(source.ObjectId);
            entry.HtmlAnchorId = Anchor(returnedId);
            entry.ComHyperlink = hyperlink;
            entry.ComHyperlinkObjectId = returnedId;
            entry.XmlElementType = source.Element.Name.LocalName;
            entry.XmlElementName = Attribute(source.Element, "name");
            entry.ParentElements = source.Element.Ancestors().Reverse()
                .Select(x => x.Name.LocalName).ToList();
            entry.SiblingIndex = source.Element.Parent?.Elements()
                .TakeWhile(x => x != source.Element).Count() ?? 0;
            entry.PageIndex = source.PageIndex;
            entry.TextPreview = Preview(source.Element.Value);
            break;
        }

        Evaluate(entry, "DirectCanonical", target.ByCanonical.GetValueOrDefault(
            entry.CanonicalLinkObjectId));
        Evaluate(entry, "GuidPart", target.ByGuid.GetValueOrDefault(GuidPart(linkObjectId)));
        var positionCandidates = entry.LinkPositions.SelectMany(position =>
                target.ByPosition.GetValueOrDefault(position) ?? new List<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Evaluate(entry, "Position", positionCandidates);
        Evaluate(entry, "PageAndPosition", positionCandidates);
        return entry;
    }

    private static IEnumerable<LinkData> FindLinks(PageData page)
    {
        foreach (var text in page.Document.Descendants().Where(x => x.Name.LocalName == "T"))
            foreach (Match match in HrefPattern.Matches(text.Value ?? ""))
            {
                var href = match.Groups["href"].Value;
                var decoded = WebUtility.HtmlDecode(href);
                if (decoded.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase) &&
                    !string.IsNullOrWhiteSpace(Parameter(decoded, "page-id")) &&
                    !string.IsNullOrWhiteSpace(Parameter(decoded, "object-id")))
                    yield return new LinkData(page, href);
            }
    }

    private static void Evaluate(OneNoteObjectIdDiagnosticEntry entry, string method,
        List<string> candidates)
    {
        candidates ??= new List<string>();
        entry.Candidates[method] = candidates;
        var resolved = entry.ResolvedSourceObjectId;
        entry.Matches[method] = candidates.Count == 0 ? OneNoteDiagnosticMatch.NotFound :
            candidates.Count > 1 ? OneNoteDiagnosticMatch.Ambiguous :
            string.Equals(candidates[0], resolved, StringComparison.OrdinalIgnoreCase)
                ? OneNoteDiagnosticMatch.Exact : OneNoteDiagnosticMatch.Wrong;
    }

    private static void SetAllNotFound(OneNoteObjectIdDiagnosticEntry entry)
    {
        foreach (var method in new[] { "DirectCanonical", "GuidPart", "Position", "PageAndPosition" })
            Evaluate(entry, method, null);
    }

    private static void Summarize(OneNoteObjectIdDiagnosticReport report)
    {
        foreach (var method in report.Entries.SelectMany(x => x.Matches.Keys).Distinct())
        {
            var summary = new OneNoteDiagnosticMethodSummary();
            foreach (var value in report.Entries.Select(x => x.Matches.GetValueOrDefault(method)))
                switch (value)
                {
                    case OneNoteDiagnosticMatch.Exact: summary.Exact++; break;
                    case OneNoteDiagnosticMatch.NotFound: summary.NotFound++; break;
                    case OneNoteDiagnosticMatch.Ambiguous: summary.Ambiguous++; break;
                    case OneNoteDiagnosticMatch.Wrong: summary.Wrong++; break;
                }
            report.Methods[method] = summary;
        }
    }

    public static string BuildTextSummary(OneNoteObjectIdDiagnosticReport report)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine("OneNote object-id diagnostic");
        text.AppendLine("Notebook: " + report.NotebookName);
        text.AppendLine("Object links: " + report.TotalObjectLinks);
        text.AppendLine("Resolved by COM: " + report.ResolvedByCom);
        text.AppendLine("Not resolved by COM: " + report.NotResolvedByCom);
        foreach (var pair in report.Methods)
            text.AppendLine($"{pair.Key}: Exact={pair.Value.Exact}, NotFound={pair.Value.NotFound}, " +
                            $"Ambiguous={pair.Value.Ambiguous}, Wrong={pair.Value.Wrong}");
        foreach (var method in report.Methods.Keys)
        {
            text.AppendLine();
            text.AppendLine("Examples for " + method + ":");
            var examples = report.Entries.Where(entry => entry.Matches.GetValueOrDefault(method) is
                    OneNoteDiagnosticMatch.Wrong or OneNoteDiagnosticMatch.NotFound or
                    OneNoteDiagnosticMatch.Ambiguous)
                .Where(entry => entry.Matches[method] == OneNoteDiagnosticMatch.Wrong)
                .Concat(report.Entries.Where(entry => entry.Matches.GetValueOrDefault(method) !=
                    OneNoteDiagnosticMatch.Exact).Take(20)).Distinct().ToList();
            foreach (var entry in examples)
                text.AppendLine($"[{entry.Matches[method]}] link={entry.LinkObjectId}; " +
                                $"xml={entry.ResolvedSourceObjectId}; positions=" +
                                string.Join(",", entry.LinkPositions) + "; preview=" + entry.TextPreview);
        }
        return text.ToString();
    }

    private sealed class PageData
    {
        public PageData(string pageId, string pageName, XDocument document)
        {
            PageId = pageId; PageName = pageName; Document = document;
            Objects = document.Descendants().Where(x => !string.IsNullOrWhiteSpace(Attribute(x, "objectID")))
                .Select((element, index) => new ObjectData(element, Attribute(element, "objectID"), index))
                .GroupBy(x => x.ObjectId, StringComparer.OrdinalIgnoreCase).Select(x => x.First()).ToList();
            ByCanonical = Index(Objects, x => Canonical(x.ObjectId));
            ByGuid = Index(Objects, x => GuidPart(x.ObjectId));
            ByPosition = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in Objects)
                foreach (var position in Parts(item.ObjectId)) Add(ByPosition, position, item.ObjectId);
            Summary = new OneNoteObjectIdPageSummary
            {
                PageId = pageId, PageName = pageName, XmlObjectIds = Objects.Count,
                UniqueCanonicalIds = ByCanonical.Count, UniquePositions = ByPosition.Count,
                RepeatedPositions = ByPosition.Where(x => x.Value.Count > 1)
                    .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase)
            };
        }
        public string PageId { get; }
        public string PageName { get; }
        public XDocument Document { get; }
        public List<ObjectData> Objects { get; }
        public Dictionary<string, List<string>> ByCanonical { get; }
        public Dictionary<string, List<string>> ByGuid { get; }
        public Dictionary<string, List<string>> ByPosition { get; }
        public OneNoteObjectIdPageSummary Summary { get; }
    }

    private sealed record ObjectData(XElement Element, string ObjectId, int PageIndex);
    private sealed record LinkData(PageData Source, string Href);

    private static Dictionary<string, List<string>> Index(IEnumerable<ObjectData> objects,
        Func<ObjectData, string> key)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in objects)
        {
            var value = key(item);
            if (!string.IsNullOrWhiteSpace(value)) Add(result, value, item.ObjectId);
        }
        return result;
    }

    private static void Add(IDictionary<string, List<string>> index, string key, string value)
    {
        if (!index.TryGetValue(key, out var values)) index[key] = values = new List<string>();
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
    }

    private static string Parameter(string link, string name)
    {
        var match = Regex.Match(link ?? "", "(?:^|[&#?])" + Regex.Escape(name) +
            "=(?<v>[^&]*)", RegexOptions.IgnoreCase);
        return match.Success ? Uri.UnescapeDataString(match.Groups["v"].Value) : "";
    }

    private static List<string> LinkPositions(string link, string objectId)
    {
        var tokens = (link ?? "").Split('&');
        var objectToken = Array.FindIndex(tokens, token => token.StartsWith("object-id=",
            StringComparison.OrdinalIgnoreCase));
        return objectToken >= 0 ? tokens.Skip(objectToken + 1).TakeWhile(token =>
                !token.Contains('=')).Where(token => !string.IsNullOrWhiteSpace(token) &&
                !string.Equals(token, "end", StringComparison.OrdinalIgnoreCase)).ToList()
            : Parts(objectId);
    }

    private static List<string> Parts(string id) => BracedPartPattern.Matches(id ?? "")
        .Cast<Match>().Select(x => x.Groups["part"].Value)
        .Where(x => !GuidPattern.IsMatch("{" + x + "}")).Skip(0).ToList();
    private static string Canonical(string id) => GuidPattern.Match(id ?? "") is { Success: true } match
        ? match.Value.ToLowerInvariant() : id ?? "";
    private static string GuidPart(string id) => Canonical(id).Trim('{', '}');
    private static string Anchor(string id) => "onenote-object-" + Regex.Replace(
        (id ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
    private static string Preview(string text) => Regex.Replace(text ?? "", "\\s+", " ").Trim() is var value
        ? value.Substring(0, Math.Min(100, value.Length)) : "";
    private static string Attribute(XElement element, string name) => element?.Attributes()
        .FirstOrDefault(x => x.Name.LocalName == name)?.Value ?? "";

    private static Task<T> RunStaAsync<T>(Func<IApplication, T> action,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            object application = null;
            try
            {
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
        }) { IsBackground = true, Name = "OneNote object-id diagnostic" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
