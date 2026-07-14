using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Newtonsoft.Json;

namespace DocSets
{

    public sealed class WorkspaceInfo
    {
        public string Name { get; set; } = "";
        public string RelativePath { get; set; } = "";
        public string FullPath { get; set; } = "";

        public override string ToString() => Name;
    }


    public sealed class SolutionLocalState
    {
        [JsonProperty("workspace")]
        public string Workspace { get; set; } = "";

        [JsonProperty("activeViewId")]
        public string ActiveViewId { get; set; } = "full-tree";

        [JsonProperty("views")]
        public Dictionary<string, TreeViewLocalState> Views { get; set; } = new Dictionary<string, TreeViewLocalState>(StringComparer.OrdinalIgnoreCase);

        [JsonProperty("ui")]
        public DocumentSetsUiSettings Ui { get; set; } = new DocumentSetsUiSettings();

        [JsonProperty("activationMode")]
        public string ActivationMode { get; set; } = "ClassicDoubleClickOpen";

        [JsonProperty("filterText")]
        public string FilterText { get; set; } = "";

        [JsonProperty("filterColors")]
        public List<BookmarkColor> FilterColors { get; set; } = new List<BookmarkColor>();

        [JsonProperty("filterTagIds")]
        public List<string> FilterTagIds { get; set; } = new List<string>();

        [JsonProperty("recentCurrentSolutionOnly")]
        public bool RecentCurrentSolutionOnly { get; set; }

        [JsonProperty("propertiesVisible")]
        public bool PropertiesVisible { get; set; } = true;

        [JsonProperty("propertiesSectionOrder")]
        public List<string> PropertiesSectionOrder { get; set; } = new List<string>();

        [JsonProperty("expandedPropertiesSections")]
        public List<string> ExpandedPropertiesSections { get; set; } = new List<string>();

        [JsonProperty("history")]
        public List<NavigationHistoryLocalItem> History { get; set; } = new List<NavigationHistoryLocalItem>();

        [JsonProperty("pins")]
        public List<PinLocalItem> Pins { get; set; } = new List<PinLocalItem>();
    }

    public sealed class PinLocalItem
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("targetId")]
        public string TargetId { get; set; } = "";
    }

    public sealed class NavigationHistoryLocalItem
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("type")]
        public BookmarkType Type { get; set; } = BookmarkType.Symbol;

        [JsonProperty("symbol")]
        public string Symbol { get; set; } = "";

        [JsonProperty("project")]
        public string Project { get; set; } = "";

        [JsonProperty("path")]
        public string Path { get; set; } = "";

        [JsonProperty("line")]
        public int Line { get; set; } = 1;

        [JsonProperty("column")]
        public int Column { get; set; } = 1;

        [JsonProperty("comment")]
        public string Comment { get; set; } = "";

        [JsonProperty("visitedAt")]
        public DateTime VisitedAt { get; set; }

        [JsonProperty("editorState", NullValueHandling = NullValueHandling.Ignore)]
        public EditorState EditorState { get; set; }
    }

    public sealed class TreeViewLocalState
    {
        [JsonProperty("collapsedIds")]
        public List<string> CollapsedIds { get; set; } = new List<string>();

        // Compatibility with solution-state files written before collapsedIds was introduced.
        [JsonProperty("expandedIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> LegacyExpandedIds { get; set; }

        [JsonProperty("selectedIds")]
        public List<string> SelectedIds { get; set; } = new List<string>();
    }

    public sealed class TagDefinition
    {
        [JsonProperty("id")]
        public string Id { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("color", NullValueHandling = NullValueHandling.Ignore)]
        public string Color { get; set; } = "";

        [JsonProperty("icon", NullValueHandling = NullValueHandling.Ignore)]
        public string Icon { get; set; } = "";

        public TagDefinition Clone() => (TagDefinition)MemberwiseClone();
    }

    public sealed class DocumentSetsState : NotifyObject
    {
        private string activeSet = "";
        private readonly DocumentItem root = new DocumentItem
        {
            Id = "root",
            Name = "Full-Tree",
            NodeType = NodeType.Folder,
            Type = BookmarkType.Empty
        };
        private DocumentSetsUiSettings ui = new DocumentSetsUiSettings();
        private List<TagDefinition> tags = new List<TagDefinition>();

        [JsonProperty("tags")]
        public List<TagDefinition> Tags
        {
            get => tags;
            set => tags = value ?? new List<TagDefinition>();
        }

        [JsonProperty("activeSet")]
        public string ActiveSet
        {
            get => activeSet;
            set => SetProperty(ref activeSet, value ?? "");
        }

        // Runtime representation. The persisted representation is one flat items list.
        [JsonIgnore]
        public DocumentItem Root => root;

        [JsonIgnore]
        public ObservableCollection<DocumentItem> Sets
        {
            get => root.Children;
            set => root.Children = value ?? new ObservableCollection<DocumentItem>();
        }

        [JsonProperty("items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<DocumentItemStorageDto> Items
        {
            get => BuildFlatItemsForStorage();
            set => Sets = BuildTreeFromFlatItems(value);
        }

        // Compatibility with workspaces produced by earlier versions where each Set
        // was serialized as a separate object containing its own flat items list.
        [JsonProperty("sets", NullValueHandling = NullValueHandling.Ignore)]
        private List<LegacyDocumentSetDto> LegacySets
        {
            set
            {
                if (value == null || value.Count == 0 || (Sets != null && Sets.Count > 0))
                    return;

                var migrated = new ObservableCollection<DocumentItem>();
                foreach (var legacySet in value.Where(x => x != null))
                {
                    var set = new DocumentItem
                    {
                        Name = legacySet.Name ?? "",
                        NodeType = NodeType.Folder,
                        Type = BookmarkType.Empty,
                        Children = BuildTreeFromFlatItems(legacySet.Items)
                    };
                    migrated.Add(set);
                }
                Sets = migrated;
            }
        }

        [JsonProperty("ui")]
        public DocumentSetsUiSettings Ui
        {
            get => ui;
            set => SetProperty(ref ui, value ?? new DocumentSetsUiSettings());
        }

        public Dictionary<string, string> RegenerateAllReadableIds()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "root" };

            foreach (var item in EnumerateItems(Sets).Where(x => !x.IsLocalOnly))
            {
                var oldId = item.Id ?? string.Empty;
                var newId = CreateReadableId(item.Name, usedIds);
                item.Id = newId;

                if (!string.IsNullOrWhiteSpace(oldId) &&
                    !string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
                {
                    result[oldId] = newId;
                }
            }

            return result;
        }

        public Dictionary<string, string> EnsureReadableIds()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "root" };

            foreach (var item in EnumerateItems(Sets).Where(x => !x.IsLocalOnly))
            {
                var oldId = item.Id ?? string.Empty;
                var keepExisting = !string.IsNullOrWhiteSpace(oldId) && !IsGuidId(oldId) && usedIds.Add(oldId);
                if (keepExisting) continue;

                var newId = CreateReadableId(item.Name, usedIds);
                item.Id = newId;
                if (!string.IsNullOrWhiteSpace(oldId) && !string.Equals(oldId, newId, StringComparison.OrdinalIgnoreCase))
                    result[oldId] = newId;
            }

            return result;
        }

        private static IEnumerable<DocumentItem> EnumerateItems(IEnumerable<DocumentItem> items)
        {
            if (items == null) yield break;
            foreach (var item in items)
            {
                if (item == null) continue;
                yield return item;
                foreach (var child in EnumerateItems(item.Children)) yield return child;
            }
        }

        private static bool IsGuidId(string value)
        {
            Guid parsed;
            return Guid.TryParse(value, out parsed) ||
                   (value != null && value.Length == 32 && Guid.TryParseExact(value, "N", out parsed));
        }

        private static string CreateReadableId(string name, ISet<string> usedIds)
        {
            var baseId = NormalizeId(name);
            if (string.IsNullOrWhiteSpace(baseId)) baseId = "item";

            var id = baseId;
            var index = 2;
            while (!usedIds.Add(id)) id = baseId + "-" + index++;
            return id;
        }

        private static string NormalizeId(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var builder = new StringBuilder();
            var separatorPending = false;
            foreach (var c in value.Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (separatorPending && builder.Length > 0) builder.Append('-');
                    builder.Append(c);
                    separatorPending = false;
                }
                else
                {
                    separatorPending = true;
                }
            }
            return builder.ToString().Trim('-');
        }

        private List<DocumentItemStorageDto> BuildFlatItemsForStorage()
        {
            EnsureReadableIds();
            var result = new List<DocumentItemStorageDto>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in Sets ?? new ObservableCollection<DocumentItem>())
            {
                if (item == null || item.IsLocalOnly) continue;
                AppendFlatItem(item, "", result, usedIds);
            }

            return result;
        }

        private static void AppendFlatItem(DocumentItem item, string parentId,
            ICollection<DocumentItemStorageDto> result, ISet<string> usedIds)
        {
            var id = item.Id;
            if (string.IsNullOrWhiteSpace(id) || !usedIds.Add(id))
            {
                id = CreateReadableId(item.Name, usedIds);
            }
            item.Id = id;
            result.Add(new DocumentItemStorageDto
            {
                Id = id,
                ParentId = parentId ?? "",
                Name = item.Name ?? "",
                NodeType = item.NodeType,
                Type = item.Type,
                Symbol = item.Type == BookmarkType.Symbol ? item.Symbol ?? "" : "",
                Project = item.Type == BookmarkType.Symbol ? item.Project ?? "" : "",
                Path = item.Path ?? "",
                Line = item.Line,
                Column = item.Column,
                Comment = item.Comment ?? "",
                Color = item.Color,
                CreatedAtUtc = item.CreatedAtUtc,
                ModifiedAtUtc = item.ModifiedAtUtc,
                ModifiedInSolution = item.ModifiedInSolution ?? "",
                TagIds = item.TagIds?.Count > 0 ? item.TagIds.ToList() : null,
                EditorState = item.EditorState?.Clone()
            });

            foreach (var child in item.Children ?? new ObservableCollection<DocumentItem>())
            {
                if (child == null || child.IsLocalOnly) continue;
                AppendFlatItem(child, id, result, usedIds);
            }
        }

        private static ObservableCollection<DocumentItem> BuildTreeFromFlatItems(IEnumerable<DocumentItemStorageDto> flatItems)
        {
            var roots = new ObservableCollection<DocumentItem>();
            if (flatItems == null) return roots;

            var entries = flatItems.Where(x => x != null).ToList();
            var map = new Dictionary<string, DocumentItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || map.ContainsKey(entry.Id)) continue;
                map.Add(entry.Id, FromStorageDto(entry));
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !map.TryGetValue(entry.Id, out var item)) continue;
                if (!string.IsNullOrWhiteSpace(entry.ParentId) && map.TryGetValue(entry.ParentId, out var parent))
                    parent.Children.Add(item);
                else
                {
                    roots.Add(item);
                }
            }

            return roots;
        }

        private static DocumentItem FromStorageDto(DocumentItemStorageDto source)
        {
            var type = source.Type;
            if ((source.NodeType == NodeType.Folder || source.NodeType == NodeType.Set)
                && type == BookmarkType.Symbol
                && string.IsNullOrWhiteSpace(source.Symbol)
                && string.IsNullOrWhiteSpace(source.Path))
                type = BookmarkType.Empty;

            return new DocumentItem
            {
                Id = source.Id ?? string.Empty,
                Name = source.Name ?? "",
                NodeType = source.NodeType == NodeType.Set ? NodeType.Folder : source.NodeType,
                Type = type,
                Symbol = type == BookmarkType.Symbol ? source.Symbol ?? "" : "",
                Project = type == BookmarkType.Symbol ? source.Project ?? "" : "",
                Path = source.Path ?? "",
                Line = source.Line < 1 ? 1 : source.Line,
                Column = source.Column < 1 ? 1 : source.Column,
                Comment = source.Comment ?? "",
                Color = source.Color,
                CreatedAtUtc = source.CreatedAtUtc,
                ModifiedAtUtc = source.ModifiedAtUtc,
                ModifiedInSolution = source.ModifiedInSolution ?? "",
                TagIds = source.TagIds?.ToList() ?? new List<string>(),
                EditorState = source.EditorState?.Clone(),
                Children = new ObservableCollection<DocumentItem>()
            };
        }

    }

    internal sealed class LegacyDocumentSetDto
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("items")]
        public List<DocumentItemStorageDto> Items { get; set; }
    }

    public sealed class DocumentSetsUiSettings : NotifyObject
    {
        private List<ColumnLayout> columns = new List<ColumnLayout>();
        private int propertiesPanelHeight = 150;

        [JsonProperty("columns")]
        public List<ColumnLayout> Columns
        {
            get => columns;
            set => SetProperty(ref columns, value ?? new List<ColumnLayout>());
        }

        [JsonProperty("propertiesPanelHeight")]
        public int PropertiesPanelHeight
        {
            get => propertiesPanelHeight;
            set => SetProperty(ref propertiesPanelHeight, value < 70 ? 70 : value);
        }
    }

    public sealed class ColumnLayout : NotifyObject
    {
        private string key = "";
        private int order;
        private int width = 100;
        private bool isVisible = true;

        [JsonProperty("key")]
        public string Key
        {
            get => key;
            set => SetProperty(ref key, value ?? "");
        }

        [JsonProperty("order")]
        public int Order
        {
            get => order;
            set => SetProperty(ref order, value);
        }

        [JsonProperty("width")]
        public int Width
        {
            get => width;
            set => SetProperty(ref width, value < 20 ? 20 : value);
        }

        [JsonProperty("isVisible")]
        public bool IsVisible
        {
            get => isVisible;
            set => SetProperty(ref isVisible, value);
        }
    }

    public sealed class DocumentItemStorageDto
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("parent", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public string ParentId { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("nodeType", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public NodeType NodeType { get; set; }

        // Read-only migration path for files created before NodeType was introduced.
        [JsonProperty("isFolder")]
        private bool LegacyIsFolder
        {
            set
            {
                if (value)
                {
                    NodeType = NodeType.Folder;
                }
            }
        }

        [JsonProperty("type", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public BookmarkType Type { get; set; }

        [JsonProperty("symbol", NullValueHandling = NullValueHandling.Ignore)]
        public string Symbol { get; set; }

        [JsonProperty("project", NullValueHandling = NullValueHandling.Ignore)]
        public string Project { get; set; }

        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string Path { get; set; }

        [JsonProperty("line")]
        public int Line { get; set; }

        [JsonProperty("column")]
        public int Column { get; set; }

        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment { get; set; }

        [JsonProperty("color", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public BookmarkColor Color { get; set; }

        [JsonProperty("createdAtUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? CreatedAtUtc { get; set; }

        [JsonProperty("modifiedAtUtc", NullValueHandling = NullValueHandling.Ignore)]
        public DateTimeOffset? ModifiedAtUtc { get; set; }

        [JsonProperty("modifiedInSolution", NullValueHandling = NullValueHandling.Ignore)]
        public string ModifiedInSolution { get; set; }

        [JsonProperty("tagIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> TagIds { get; set; }

        [JsonProperty("editorState", NullValueHandling = NullValueHandling.Ignore)]
        public EditorState EditorState { get; set; }
    }

    public sealed class EditorState
    {
        [JsonProperty("caretLineOffset")]
        public int CaretLineOffset { get; set; }

        [JsonProperty("caretColumn")]
        public int CaretColumn { get; set; } = 1;

        [JsonProperty("hasSelection", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool HasSelection { get; set; }

        [JsonProperty("selectionStartLineOffset", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int SelectionStartLineOffset { get; set; }

        [JsonProperty("selectionStartColumn", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int SelectionStartColumn { get; set; } = 1;

        [JsonProperty("selectionEndLineOffset", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int SelectionEndLineOffset { get; set; }

        [JsonProperty("selectionEndColumn", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int SelectionEndColumn { get; set; } = 1;

        [JsonProperty("firstVisibleLineOffset")]
        public int FirstVisibleLineOffset { get; set; }

        [JsonProperty("selectedText", NullValueHandling = NullValueHandling.Ignore)]
        public string SelectedText { get; set; }

        [JsonProperty("codePreview", NullValueHandling = NullValueHandling.Ignore)]
        public string CodePreview { get; set; }

        public EditorState Clone()
        {
            return (EditorState)MemberwiseClone();
        }
    }

    public enum NodeType
    {
        Item = 0,
        Folder = 1,
        // Compatibility value for loading old files. New model uses Folder at root level.
        Set = 2
    }

    [AttributeUsage(AttributeTargets.Field)]
    public sealed class BookmarkColorInfoAttribute : Attribute
    {
        public BookmarkColorInfoAttribute(string name, int red, int green, int blue)
        {
            Name = name; Red = red; Green = green; Blue = blue;
        }
        public string Name { get; }
        public int Red { get; }
        public int Green { get; }
        public int Blue { get; }
    }

    public enum BookmarkColor
    {
        [BookmarkColorInfo("Без цвета", 255, 255, 255)] None = 0,
        [BookmarkColorInfo("Красный", 237, 28, 54)] Red = 1,
        [BookmarkColorInfo("Зелёный", 31, 201, 37)] Green = 2,
        [BookmarkColorInfo("Жёлтый", 255, 229, 0)] Yellow = 3,
        [BookmarkColorInfo("Синий", 22, 139, 205)] Blue = 4,
        [BookmarkColorInfo("Оранжевый", 255, 140, 0)] Orange = 5,
        [BookmarkColorInfo("Бирюзовый", 0, 188, 212)] Cyan = 6,
        [BookmarkColorInfo("Фиолетовый", 156, 39, 176)] Purple = 7,
        [BookmarkColorInfo("Серый", 128, 128, 128)] Gray = 8
    }

    public enum BookmarkType
    {
        // Symbol keeps value 0 for compatibility with existing files where Default was omitted.
        Symbol = 0,
        Default = Symbol,
        File = 1,
        Empty = 2,
        Pin = 3
    }

    public enum DocumentTreeChangeKind
    {
        PropertyChanged,
        Added,
        Removed,
        Moved,
        Reset
    }

    public sealed class DocumentTreeChangedEventArgs : EventArgs
    {
        public DocumentTreeChangeKind Kind { get; internal set; }
        public DocumentItem Item { get; internal set; }
        public string PropertyName { get; internal set; }
        public DocumentItem OldParent { get; internal set; }
        public DocumentItem NewParent { get; internal set; }
        public int OldIndex { get; internal set; } = -1;
        public int NewIndex { get; internal set; } = -1;
    }

    public sealed class DocumentItem : NotifyObject
    {
        private string id = string.Empty;
        private DocumentItem parent;
        private string name = "";
        private BookmarkType type;
        private string symbol = "";
        private string project = "";
        private string path = "";
        private string comment = "";
        private BookmarkColor color;
        private DateTimeOffset? createdAtUtc;
        private DateTimeOffset? modifiedAtUtc;
        private string modifiedInSolution = "";
        private int line = 1;
        private int column = 1;
        private NodeType nodeType;
        private EditorState editorState;
        private List<string> tagIds = new List<string>();
        private bool isExpanded;
        private bool isMultiSelected;
        private bool isLocalOnly;
        private bool isHistoryRoot;
        private bool isHistoryItem;
        private bool isPinRoot;
        private bool isPinItem;
        private bool isRecentRoot;
        private bool isRecentItem;
        private string targetId = string.Empty;
        private bool isMethodSymbol;
        private ObservableCollection<DocumentItem> children = new ObservableCollection<DocumentItem>();

        [JsonIgnore]
        public string Id
        {
            get => id;
            set => id = value ?? string.Empty;
        }

        [JsonIgnore]
        public DocumentItem Parent => parent;

        [JsonIgnore]
        public DocumentItem Root
        {
            get
            {
                var node = this;
                while (node.parent != null) node = node.parent;
                return node;
            }
        }

        [JsonIgnore]
        public bool IsRootChild => parent != null && parent.parent == null;

        public event EventHandler<DocumentTreeChangedEventArgs> TreeChanged;

        [JsonProperty("name")]
        public string Name
        {
            get => name;
            set
            {
                if (SetProperty(ref name, value ?? ""))
                {
                    OnPropertyChanged(nameof(Display));
                    OnPropertyChanged(nameof(Header));
                }
            }
        }

        [JsonProperty("nodeType", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public NodeType NodeType
        {
            get => nodeType;
            set
            {
                if (SetProperty(ref nodeType, value))
                {
                    OnPropertyChanged(nameof(Display));
                    OnPropertyChanged(nameof(Header));
                }
            }
        }

        [JsonProperty("type", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public BookmarkType Type
        {
            get => type;
            set
            {
                if (SetProperty(ref type, value))
                {
                    OnPropertyChanged(nameof(Display));
                    OnPropertyChanged(nameof(Header));
                }
            }
        }

        [JsonProperty("symbol", NullValueHandling = NullValueHandling.Ignore)]
        public string Symbol
        {
            get => symbol;
            set => SetProperty(ref symbol, value ?? "");
        }

        [JsonProperty("project", NullValueHandling = NullValueHandling.Ignore)]
        public string Project
        {
            get => project;
            set => SetProperty(ref project, value ?? "");
        }

        [JsonProperty("path", NullValueHandling = NullValueHandling.Ignore)]
        public string Path
        {
            get => path;
            set
            {
                if (SetProperty(ref path, value ?? ""))
                {
                    OnPropertyChanged(nameof(Display));
                    OnPropertyChanged(nameof(Header));
                }
            }
        }

        [JsonProperty("comment", NullValueHandling = NullValueHandling.Ignore)]
        public string Comment
        {
            get => comment;
            set
            {
                if (SetProperty(ref comment, value ?? ""))
                {
                    OnPropertyChanged(nameof(CommentFirstLine));
                }
            }
        }

        [JsonProperty("color", DefaultValueHandling = DefaultValueHandling.Ignore)]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public BookmarkColor Color
        {
            get => color;
            set => SetProperty(ref color, value);
        }

        [JsonIgnore]
        public DateTimeOffset? CreatedAtUtc
        {
            get => createdAtUtc;
            set => SetProperty(ref createdAtUtc, value);
        }

        [JsonIgnore]
        public DateTimeOffset? ModifiedAtUtc
        {
            get => modifiedAtUtc;
            set => SetProperty(ref modifiedAtUtc, value);
        }

        [JsonIgnore]
        public string ModifiedInSolution
        {
            get => modifiedInSolution;
            set => SetProperty(ref modifiedInSolution, value ?? "");
        }

        [JsonIgnore]
        public string CommentFirstLine
        {
            get
            {
                if (string.IsNullOrWhiteSpace(Comment))
                {
                    return string.Empty;
                }

                var normalized = Comment.Replace("\r\n", "\n").Replace('\r', '\n');
                var index = normalized.IndexOf('\n');
                return index < 0 ? normalized.Trim() : normalized.Substring(0, index).Trim();
            }
        }

        [JsonProperty("line")]
        public int Line
        {
            get => line;
            set
            {
                if (SetProperty(ref line, value < 1 ? 1 : value))
                {
                    OnPropertyChanged(nameof(Display));
                    OnPropertyChanged(nameof(Header));
                }
            }
        }

        [JsonProperty("column")]
        public int Column
        {
            get => column;
            set => SetProperty(ref column, value < 1 ? 1 : value);
        }

        [JsonProperty("tagIds", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> TagIds
        {
            get => tagIds;
            set => SetProperty(ref tagIds, value == null ? new List<string>() : value.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
        }

        [JsonProperty("editorState", NullValueHandling = NullValueHandling.Ignore)]
        public EditorState EditorState
        {
            get => editorState;
            set => SetProperty(ref editorState, value);
        }

        [JsonIgnore]
        public ObservableCollection<DocumentItem> Children
        {
            get => children;
            set
            {
                if (ReferenceEquals(children, value)) return;
                if (children != null) children.CollectionChanged -= ChildrenCollectionChanged;
                children = value ?? new ObservableCollection<DocumentItem>();
                children.CollectionChanged += ChildrenCollectionChanged;
                foreach (var child in children.Where(x => x != null)) child.parent = this;
                RaiseTreeChanged(new DocumentTreeChangedEventArgs
                {
                    Kind = DocumentTreeChangeKind.Reset,
                    Item = this,
                    NewParent = this
                });
                OnPropertyChanged();
            }
        }


        [JsonIgnore]
        public bool IsLocalOnly
        {
            get => isLocalOnly;
            set => isLocalOnly = value;
        }

        [JsonIgnore]
        public bool IsHistoryRoot
        {
            get => isHistoryRoot;
            set => isHistoryRoot = value;
        }

        [JsonIgnore]
        public bool IsHistoryItem
        {
            get => isHistoryItem;
            set => isHistoryItem = value;
        }

        [JsonIgnore]
        public bool IsPinRoot
        {
            get => isPinRoot;
            set => isPinRoot = value;
        }

        [JsonIgnore]
        public bool IsPinItem
        {
            get => isPinItem;
            set => isPinItem = value;
        }

        [JsonIgnore]
        public bool IsRecentRoot
        {
            get => isRecentRoot;
            set => isRecentRoot = value;
        }

        [JsonIgnore]
        public bool IsRecentItem
        {
            get => isRecentItem;
            set => isRecentItem = value;
        }

        [JsonIgnore]
        public string TargetId
        {
            get => targetId;
            set => targetId = value ?? string.Empty;
        }

        [JsonIgnore]
        public bool IsMethodSymbol
        {
            get => isMethodSymbol;
            set => isMethodSymbol = value;
        }

        [JsonIgnore]
        public bool IsExpanded
        {
            get => isExpanded;
            set => SetProperty(ref isExpanded, value);
        }

        [JsonIgnore]
        public bool IsMultiSelected
        {
            get => isMultiSelected;
            set => SetProperty(ref isMultiSelected, value);
        }

        [JsonIgnore]
        public string Display
        {
            get
            {
                if (NodeType == NodeType.Folder)
                {
                    return string.IsNullOrWhiteSpace(Name) ? "Новая папка" : Name;
                }

                return string.IsNullOrWhiteSpace(Name) ? $"{Path}:{Line}" : $"{Name}  ({Path}:{Line})";
            }
        }

        [JsonIgnore]
        public string Header => NodeType == NodeType.Folder ? $"📁 {Display}" : Display;

        public DocumentItem()
        {
            children.CollectionChanged += ChildrenCollectionChanged;
        }

        private void ChildrenCollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move)
            {
                var moved = e.NewItems != null && e.NewItems.Count > 0 ? e.NewItems[0] as DocumentItem : null;
                RaiseTreeChanged(new DocumentTreeChangedEventArgs
                {
                    Kind = DocumentTreeChangeKind.Moved, Item = moved, OldParent = this, NewParent = this,
                    OldIndex = e.OldStartingIndex, NewIndex = e.NewStartingIndex
                });
                return;
            }

            if (e.OldItems != null)
            {
                foreach (DocumentItem child in e.OldItems)
                {
                    RaiseTreeChanged(new DocumentTreeChangedEventArgs
                    {
                        Kind = DocumentTreeChangeKind.Removed, Item = child, OldParent = this, OldIndex = e.OldStartingIndex
                    });
                    if (ReferenceEquals(child.parent, this)) child.parent = null;
                }
            }
            if (e.NewItems != null)
            {
                var index = e.NewStartingIndex;
                foreach (DocumentItem child in e.NewItems)
                {
                    child.parent = this;
                    RaiseTreeChanged(new DocumentTreeChangedEventArgs
                    {
                        Kind = DocumentTreeChangeKind.Added, Item = child, NewParent = this, NewIndex = index++
                    });
                }
            }
            if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset)
            {
                RaiseTreeChanged(new DocumentTreeChangedEventArgs { Kind = DocumentTreeChangeKind.Reset, Item = this });
            }
        }

        private void RaiseTreeChanged(DocumentTreeChangedEventArgs args)
        {
            Root.TreeChanged?.Invoke(Root, args);
        }

        protected override void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            base.OnPropertyChanged(propertyName);
            if (propertyName == nameof(Parent) || propertyName == nameof(Root)) return;
            RaiseTreeChanged(new DocumentTreeChangedEventArgs
            {
                Kind = DocumentTreeChangeKind.PropertyChanged,
                Item = this,
                PropertyName = propertyName
            });
        }

        public DocumentItem Clone()
        {
            var clone = new DocumentItem
            {
                Name = Name,
                NodeType = NodeType,
                Type = Type,
                Symbol = Type == BookmarkType.Symbol ? Symbol : "",
                Project = Project,
                Path = Path,
                Line = Line,
                Column = Column,
                Comment = Comment,
                Color = Color,
                CreatedAtUtc = CreatedAtUtc,
                ModifiedAtUtc = ModifiedAtUtc,
                ModifiedInSolution = ModifiedInSolution,
                TagIds = TagIds.ToList(),
                EditorState = EditorState?.Clone(),
                IsExpanded = IsExpanded,
                Children = new ObservableCollection<DocumentItem>()
            };

            if (Children != null)
            {
                foreach (var child in Children)
                {
                    clone.Children.Add(child.Clone());
                }
            }

            return clone;
        }

        public override string ToString() => Display;
    }

    public abstract class NotifyObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string propertyName = null)
        {
            if (Equals(storage, value))
            {
                return false;
            }

            storage = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    internal sealed class ActiveDocumentContext
    {
        public string SolutionName { get; set; } = "";

        public string ProjectName { get; set; } = "";

        public string FileName { get; set; } = "";

        public string ClassName { get; set; } = "";
    }

}
