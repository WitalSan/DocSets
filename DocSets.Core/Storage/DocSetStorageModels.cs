using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace DocSets
{
    public enum ContentFormat
    {
        Markdown = 0,
        Html = 1,
        Docx = 2
    }

    public enum CodeSourceType
    {
        Solution = 0,
        Project = 1,
        Directory = 2
    }

    public sealed class CodeSource
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("type")]
        [JsonConverter(typeof(StringEnumConverter))]
        public CodeSourceType Type { get; set; }

        [JsonProperty("root")]
        public string Root { get; set; } = "";

        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string Path { get; set; } = "";
    }

    public sealed class DocSetManifest
    {
        public const string CurrentFormat = "DocSets";
        public const int CurrentFormatVersion = 1;

        [JsonProperty("format")]
        public string Format { get; set; } = CurrentFormat;

        [JsonProperty("formatVersion")]
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("sources")]
        public List<CodeSource> Sources { get; set; } = new List<CodeSource>();

        [JsonProperty("tags")]
        public List<TagDefinition> Tags { get; set; } = new List<TagDefinition>();

        [JsonProperty("items")]
        public List<DocSetItemStorageDto> Items { get; set; } = new List<DocSetItemStorageDto>();
    }

    public sealed class DocSetItemStorageDto
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("parent", NullValueHandling = NullValueHandling.Ignore)]
        public string ParentId { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("nodeType", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(StringEnumConverter))]
        public NodeType NodeType { get; set; }

        [JsonProperty("type", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(StringEnumConverter))]
        public BookmarkType Type { get; set; }

        [JsonProperty("sourceId", NullValueHandling = NullValueHandling.Ignore)]
        public string SourceId { get; set; } = "";

        [JsonProperty("symbol", NullValueHandling = NullValueHandling.Ignore)]
        public string Symbol { get; set; } = "";

        [JsonProperty("project", NullValueHandling = NullValueHandling.Ignore)]
        public string Project { get; set; } = "";

        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string Path { get; set; } = "";

        [JsonProperty("line", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Line { get; set; } = 1;

        [JsonProperty("column", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Column { get; set; } = 1;

        [JsonProperty("contentFormat")]
        [JsonConverter(typeof(StringEnumConverter))]
        public ContentFormat ContentFormat { get; set; } = ContentFormat.Markdown;

        [JsonProperty("content", NullValueHandling = NullValueHandling.Ignore)]
        public string Content { get; set; } = "";

        [JsonProperty("contentPath", NullValueHandling = NullValueHandling.Ignore)]
        public string ContentPath { get; set; } = "";

        [JsonProperty("color", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(StringEnumConverter))]
        public BookmarkColor Color { get; set; }

        [JsonProperty("createdAtUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? CreatedAtUtc { get; set; }

        [JsonProperty("modifiedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? ModifiedAtUtc { get; set; }

        [JsonProperty("tagIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> TagIds { get; set; }

        [JsonProperty("editorState", NullValueHandling = NullValueHandling.Ignore)]
        public EditorState EditorState { get; set; }
    }

    public sealed class DocSetsWorkspace
    {
        public const string CurrentFormat = "DocSetsWorkspace";
        public const int CurrentFormatVersion = 1;

        [JsonProperty("format")]
        public string Format { get; set; } = CurrentFormat;

        [JsonProperty("formatVersion")]
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        [JsonProperty("openDocSets")]
        public List<string> OpenDocSets { get; set; } = new List<string>();

        [JsonProperty("activeDocSet")]
        public string ActiveDocSet { get; set; } = "";

        [JsonProperty("sourceRootOverrides")]
        public Dictionary<string, string> SourceRootOverrides { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("ui")]
        public DocSetsWorkspaceUiState Ui { get; set; } = new DocSetsWorkspaceUiState();
    }

    public sealed class DocSetsWorkspaceUiState
    {
        [JsonProperty("activeViewId")]
        public string ActiveViewId { get; set; } = "full-tree";

        [JsonProperty("propertiesDockLayout", NullValueHandling = NullValueHandling.Ignore)]
        public string PropertiesDockLayout { get; set; } = "";

        [JsonProperty("selectedItemIds")]
        public Dictionary<string, List<string>> SelectedItemIds { get; set; }
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
    }
}
