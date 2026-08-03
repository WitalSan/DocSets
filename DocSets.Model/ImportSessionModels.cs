using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DocSets
{
    public sealed class ImportSessionRemovedEventArgs : EventArgs
    {
        public ImportSessionRemovedEventArgs(string sessionId) => SessionId = sessionId ?? "";
        public string SessionId { get; }
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ImportSessionStatus
    {
        Created,
        Running,
        Pausing,
        Paused,
        Completed,
        Failed,
        Interrupted
    }

    [JsonConverter(typeof(StringEnumConverter))]
    public enum ImportPageStatus
    {
        Pending,
        Importing,
        Imported,
        ImportedWithWarnings,
        Failed
    }

    public sealed class ImportPageState
    {
        [JsonProperty("oneNotePageId")]
        public string OneNotePageId { get; set; } = "";
        [JsonProperty("docSetsNodeId")]
        public string DocSetsNodeId { get; set; } = "";
        [JsonProperty("status")]
        public ImportPageStatus Status { get; set; }
        [JsonProperty("importedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? ImportedAtUtc { get; set; }
        [JsonProperty("oneNoteModifiedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? OneNoteModifiedAtUtc { get; set; }
        [JsonProperty("contentChecksum", NullValueHandling = NullValueHandling.Ignore)]
        public string ContentChecksum { get; set; } = "";
    }

    public sealed class ImportObjectLinkCacheEntry
    {
        [JsonProperty("pageId")]
        public string PageId { get; set; } = "";
        [JsonProperty("sourceObjectId")]
        public string SourceObjectId { get; set; } = "";
        [JsonProperty("hyperlinkObjectId")]
        public string HyperlinkObjectId { get; set; } = "";
        [JsonProperty("succeeded")]
        public bool Succeeded { get; set; }
    }

    public sealed class ImportSessionStatistics
    {
        public int Sections { get; set; }
        public int Pages { get; set; }
        public int Images { get; set; }
        public int Attachments { get; set; }
        public int Tables { get; set; }
        public int Tags { get; set; }
        public int InternalLinks { get; set; }
        public int ExternalLinks { get; set; }
        public int ObjectLinks { get; set; }
        public int FailedPages { get; set; }
    }

    public sealed class ImportSessionState
    {
        public const int CurrentVersion = 1;
        [JsonProperty("version")]
        public int Version { get; set; } = CurrentVersion;
        [JsonProperty("id")]
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        [JsonProperty("name")]
        public string Name { get; set; } = "";
        [JsonProperty("sourceType")]
        public string SourceType { get; set; } = "OneNote";
        [JsonProperty("sourceId")]
        public string SourceId { get; set; } = "";
        [JsonProperty("sourceName")]
        public string SourceName { get; set; } = "";
        [JsonProperty("targetNodeId")]
        public string TargetNodeId { get; set; } = "";
        [JsonProperty("status")]
        public ImportSessionStatus Status { get; set; } = ImportSessionStatus.Created;
        [JsonProperty("stage")]
        public string Stage { get; set; } = "";
        [JsonProperty("createdAtUtc")]
        public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
        [JsonProperty("startedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? StartedAtUtc { get; set; }
        [JsonProperty("completedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? CompletedAtUtc { get; set; }
        [JsonProperty("progressCurrent")]
        public int ProgressCurrent { get; set; }
        [JsonProperty("progressTotal")]
        public int ProgressTotal { get; set; }
        [JsonProperty("overallProgressPercent")]
        public int OverallProgressPercent { get; set; }
        [JsonProperty("linkResolutionCompleted")]
        public bool LinkResolutionCompleted { get; set; }
        [JsonProperty("pages")]
        public List<ImportPageState> Pages { get; set; } = new List<ImportPageState>();
        [JsonProperty("objectLinkCache")]
        public List<ImportObjectLinkCacheEntry> ObjectLinkCache { get; set; } = new List<ImportObjectLinkCacheEntry>();
        [JsonProperty("statistics")]
        public ImportSessionStatistics Statistics { get; set; } = new ImportSessionStatistics();
        [JsonProperty("reportJson")]
        public string ReportJson { get; set; } = "";
        [JsonProperty("profileJson")]
        public string ProfileJson { get; set; } = "";
        [JsonProperty("warnings")]
        public List<string> Warnings { get; set; } = new List<string>();
        [JsonProperty("errors")]
        public List<string> Errors { get; set; } = new List<string>();
    }
}
