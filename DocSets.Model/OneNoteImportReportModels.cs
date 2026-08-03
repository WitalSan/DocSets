using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DocSets
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum OneNoteImportStatus
    {
        Imported,
        ImportedWithWarnings,
        ConvertedWithLoss,
        NotImported
    }

    public sealed class OneNoteImportReportEntry
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
        public List<string> RelatedProblemIds { get; set; } = new List<string>();
    }

    public sealed class OneNoteImportReport
    {
        public int Version { get; set; } = 2;
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string NotebookName { get; set; } = "";
        public string NotebookId { get; set; } = "";
        public string ImportedRootNodeId { get; set; } = "";
        public List<OneNoteImportReportEntry> Entries { get; set; } = new List<OneNoteImportReportEntry>();
        public OneNoteImportProfile Profile { get; set; } = new OneNoteImportProfile();
        public int Count(OneNoteImportStatus status) => Entries.Count(x => x.Status == status);
        public int Problems => Entries.Count(x => x.Status != OneNoteImportStatus.Imported);
        public int PrimaryProblems => Entries.Count(x => x.Status != OneNoteImportStatus.Imported && !x.IsAggregate);
    }

    public sealed class OneNoteImportTiming
    {
        public string Path { get; set; } = "";
        public long Calls { get; set; }
        public double ElapsedMilliseconds { get; set; }
        public double AverageMilliseconds => Calls == 0 ? 0 : ElapsedMilliseconds / Calls;
    }

    public sealed class OneNotePageTiming
    {
        public string PageName { get; set; } = "";
        public string OneNotePageId { get; set; } = "";
        public long XmlBytes { get; set; }
        public int Images { get; set; }
        public int Attachments { get; set; }
        public double TotalMilliseconds { get; set; }
        public Dictionary<string, double> Stages { get; set; } = new Dictionary<string, double>(StringComparer.Ordinal);
    }

    public sealed class OneNoteImportProfile
    {
        public const string RootPath = "Весь импорт";
        private readonly object sync = new object();
        private Stopwatch overallStopwatch;
        public List<OneNoteImportTiming> Timings { get; set; } = new List<OneNoteImportTiming>();
        public List<OneNotePageTiming> Pages { get; set; } = new List<OneNotePageTiming>();
        public int DocSetSaveCalls { get; set; }
        public int TreeUpdateCalls { get; set; }
        public bool DocSetSavedAfterEachPage { get; set; }
        public bool TreeUpdatedAfterEachPage { get; set; }

        public IDisposable Measure(string path) => new TimingScope(this, path);
        public IDisposable Measure(string path, OneNotePageTiming page, string stage)
            => new TimingScope(this, path, page, stage);
        public void StartOverall() { lock (sync) overallStopwatch = Stopwatch.StartNew(); }
        public void StopOverall(string path)
        {
            Stopwatch stopwatch;
            lock (sync) { stopwatch = overallStopwatch; overallStopwatch = null; }
            if (stopwatch == null) return;
            stopwatch.Stop(); Record(path, stopwatch.Elapsed);
        }
        public void AddPage(OneNotePageTiming page) { if (page != null) lock (sync) Pages.Add(page); }
        public void Record(string path, TimeSpan elapsed, long calls = 1)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            lock (sync)
            {
                var timing = Timings.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.Ordinal));
                if (timing == null) { timing = new OneNoteImportTiming { Path = path }; Timings.Add(timing); }
                timing.Calls += Math.Max(0, calls);
                timing.ElapsedMilliseconds += Math.Max(0, elapsed.TotalMilliseconds);
            }
        }
        public void RecordPageStage(OneNotePageTiming page, string stage, TimeSpan elapsed)
        {
            if (page == null || string.IsNullOrWhiteSpace(stage)) return;
            lock (sync) { page.Stages.TryGetValue(stage, out var value); page.Stages[stage] = value + Math.Max(0, elapsed.TotalMilliseconds); }
        }
        public double ElapsedMilliseconds(string path)
        { lock (sync) return Timings.FirstOrDefault(x => string.Equals(x.Path, path, StringComparison.Ordinal))?.ElapsedMilliseconds ?? 0; }
        public OneNoteImportProfile Snapshot()
        {
            lock (sync)
            {
                var result = new OneNoteImportProfile
                {
                    Timings = Timings.Select(x => new OneNoteImportTiming { Path = x.Path, Calls = x.Calls, ElapsedMilliseconds = x.ElapsedMilliseconds }).ToList(),
                    Pages = Pages.Select(x => new OneNotePageTiming { PageName = x.PageName, OneNotePageId = x.OneNotePageId, XmlBytes = x.XmlBytes, Images = x.Images, Attachments = x.Attachments, TotalMilliseconds = x.TotalMilliseconds, Stages = new Dictionary<string, double>(x.Stages, StringComparer.Ordinal) }).ToList(),
                    DocSetSaveCalls = DocSetSaveCalls, TreeUpdateCalls = TreeUpdateCalls,
                    DocSetSavedAfterEachPage = DocSetSavedAfterEachPage, TreeUpdatedAfterEachPage = TreeUpdatedAfterEachPage
                };
                if (overallStopwatch != null)
                {
                    result.Timings.RemoveAll(x => x.Path == RootPath);
                    result.Timings.Add(new OneNoteImportTiming { Path = RootPath, Calls = 1, ElapsedMilliseconds = overallStopwatch.Elapsed.TotalMilliseconds });
                }
                return result;
            }
        }

        private sealed class TimingScope : IDisposable
        {
            private readonly OneNoteImportProfile owner; private readonly string path;
            private readonly OneNotePageTiming page; private readonly string stage;
            private readonly Stopwatch stopwatch = Stopwatch.StartNew();
            private bool disposed;
            public TimingScope(OneNoteImportProfile owner, string path, OneNotePageTiming page = null, string stage = null)
            { this.owner = owner; this.path = path; this.page = page; this.stage = stage; }
            public void Dispose() { if (disposed) return; disposed = true; stopwatch.Stop(); owner.Record(path, stopwatch.Elapsed); owner.RecordPageStage(page, stage, stopwatch.Elapsed); }
        }
    }
}
