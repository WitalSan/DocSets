using System.Collections.Generic;
using System;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Diagnostics;

namespace DocSets.Desktop.OneNote;

internal sealed class OneNoteNotebook
{
    public OneNoteNotebook(string id, string name)
    {
        Id = id ?? "";
        Name = name ?? "";
    }

    public string Id { get; }
    public string Name { get; }
}

internal sealed class OneNoteImportProgress
{
    public int Current { get; set; }
    public int Total { get; set; }
    public string Message { get; set; } = "";
    public OneNoteImportReport ReportSnapshot { get; set; }
}

internal sealed class OneNoteImportResult
{
    public DocumentItem Root { get; set; }
    public int Folders { get; set; }
    public int Pages { get; set; }
    public int Images { get; set; }
    public int Attachments { get; set; }
    public int InternalLinks { get; set; }
    public int UnresolvedInternalLinks { get; set; }
    public int FailedPages { get; set; }
    public int NoteTags { get; set; }
    public bool Cancelled { get; set; }
    public List<string> Errors { get; } = new();
    public OneNoteImportReport Report { get; set; } = new();
    public List<NoteTagStyle> NoteTagStyles { get; } = new();
}

[JsonConverter(typeof(StringEnumConverter))]
internal enum OneNoteImportStatus
{
    Imported,
    ImportedWithWarnings,
    ConvertedWithLoss,
    NotImported
}

internal sealed class OneNoteImportReportEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ObjectType { get; set; } = "";
    public string Name { get; set; } = "";
    public OneNoteImportStatus Status { get; set; }
    public string Reason { get; set; } = "";
    public string OneNotePageId { get; set; } = "";
    public string OneNoteObjectId { get; set; } = "";
    public string OneNoteLink { get; set; } = "";
    public string DocSetsNodeId { get; set; } = "";
    public string DocSetsAnchorId { get; set; } = "";
    public bool IsAggregate { get; set; }
    public List<string> RelatedProblemIds { get; set; } = new();
}

internal sealed class OneNoteImportReport
{
    public int Version { get; set; } = 2;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string NotebookName { get; set; } = "";
    public string NotebookId { get; set; } = "";
    public string ImportedRootNodeId { get; set; } = "";
    public List<OneNoteImportReportEntry> Entries { get; set; } = new();
    public OneNoteImportProfile Profile { get; set; } = new();

    public int Count(OneNoteImportStatus status) => Entries.Count(entry => entry.Status == status);
    public int Problems => Entries.Count(entry => entry.Status != OneNoteImportStatus.Imported);
    public int PrimaryProblems => Entries.Count(entry =>
        entry.Status != OneNoteImportStatus.Imported && !entry.IsAggregate);
}

internal sealed class OneNoteImportTiming
{
    public string Path { get; set; } = "";
    public long Calls { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public double AverageMilliseconds => Calls == 0 ? 0 : ElapsedMilliseconds / Calls;
}

internal sealed class OneNotePageTiming
{
    public string PageName { get; set; } = "";
    public string OneNotePageId { get; set; } = "";
    public long XmlBytes { get; set; }
    public int Images { get; set; }
    public int Attachments { get; set; }
    public double TotalMilliseconds { get; set; }
    public Dictionary<string, double> Stages { get; set; } = new(StringComparer.Ordinal);
}

internal sealed class OneNoteImportProfile
{
    private readonly object _sync = new();
    private Stopwatch _overallStopwatch;
    public List<OneNoteImportTiming> Timings { get; set; } = new();
    public List<OneNotePageTiming> Pages { get; set; } = new();
    public int DocSetSaveCalls { get; set; }
    public int TreeUpdateCalls { get; set; }
    public bool DocSetSavedAfterEachPage { get; set; }
    public bool TreeUpdatedAfterEachPage { get; set; }

    public IDisposable Measure(string path)
        => new TimingScope(this, path);

    public void StartOverall()
    {
        lock (_sync) _overallStopwatch = Stopwatch.StartNew();
    }

    public void StopOverall(string path)
    {
        Stopwatch stopwatch;
        lock (_sync)
        {
            stopwatch = _overallStopwatch;
            _overallStopwatch = null;
        }
        if (stopwatch == null) return;
        stopwatch.Stop();
        Record(path, stopwatch.Elapsed);
    }

    public IDisposable Measure(string path, OneNotePageTiming page, string pageStage)
        => new TimingScope(this, path, page, pageStage);

    public void AddPage(OneNotePageTiming page)
    {
        if (page == null) return;
        lock (_sync) Pages.Add(page);
    }

    public OneNoteImportProfile Snapshot()
    {
        lock (_sync)
        {
            var snapshot = new OneNoteImportProfile
            {
                Timings = Timings.Select(item => new OneNoteImportTiming
                {
                    Path = item.Path, Calls = item.Calls,
                    ElapsedMilliseconds = item.ElapsedMilliseconds
                }).ToList(),
                Pages = Pages.Select(page => new OneNotePageTiming
                {
                    PageName = page.PageName, OneNotePageId = page.OneNotePageId,
                    XmlBytes = page.XmlBytes, Images = page.Images,
                    Attachments = page.Attachments, TotalMilliseconds = page.TotalMilliseconds,
                    Stages = new Dictionary<string, double>(page.Stages, StringComparer.Ordinal)
                }).ToList(),
                DocSetSaveCalls = DocSetSaveCalls,
                TreeUpdateCalls = TreeUpdateCalls,
                DocSetSavedAfterEachPage = DocSetSavedAfterEachPage,
                TreeUpdatedAfterEachPage = TreeUpdatedAfterEachPage
            };
            if (_overallStopwatch != null)
            {
                snapshot.Timings.RemoveAll(item => item.Path == OneNoteImportService.ProfileRoot);
                snapshot.Timings.Add(new OneNoteImportTiming
                {
                    Path = OneNoteImportService.ProfileRoot,
                    Calls = 1,
                    ElapsedMilliseconds = _overallStopwatch.Elapsed.TotalMilliseconds
                });
            }
            return snapshot;
        }
    }

    public void Record(string path, TimeSpan elapsed, long calls = 1)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_sync)
        {
            var timing = Timings.FirstOrDefault(item => string.Equals(
                item.Path, path, StringComparison.Ordinal));
            if (timing == null)
            {
                timing = new OneNoteImportTiming { Path = path };
                Timings.Add(timing);
            }
            timing.Calls += Math.Max(0, calls);
            timing.ElapsedMilliseconds += Math.Max(0, elapsed.TotalMilliseconds);
        }
    }

    public double ElapsedMilliseconds(string path)
    {
        lock (_sync)
            return Timings.FirstOrDefault(item => string.Equals(
                item.Path, path, StringComparison.Ordinal))?.ElapsedMilliseconds ?? 0;
    }

    public void RecordPageStage(OneNotePageTiming page, string stage, TimeSpan elapsed)
    {
        if (page == null || string.IsNullOrWhiteSpace(stage)) return;
        lock (_sync)
        {
            page.Stages.TryGetValue(stage, out var current);
            page.Stages[stage] = current + Math.Max(0, elapsed.TotalMilliseconds);
        }
    }

    private sealed class TimingScope : IDisposable
    {
        private readonly OneNoteImportProfile _owner;
        private readonly string _path;
        private readonly OneNotePageTiming _page;
        private readonly string _pageStage;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public TimingScope(OneNoteImportProfile owner, string path,
            OneNotePageTiming page = null, string pageStage = null)
        {
            _owner = owner;
            _path = path;
            _page = page;
            _pageStage = pageStage;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stopwatch.Stop();
            _owner.Record(_path, _stopwatch.Elapsed);
            _owner.RecordPageStage(_page, _pageStage, _stopwatch.Elapsed);
        }
    }
}
