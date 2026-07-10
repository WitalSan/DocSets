using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

namespace DocSets
{
    public sealed class DocumentSetsState : NotifyObject
    {
        private string activeSet = "";
        private ObservableCollection<DocumentSet> sets = new ObservableCollection<DocumentSet>();
        private DocumentSetsUiSettings ui = new DocumentSetsUiSettings();

        [JsonProperty("activeSet")]
        public string ActiveSet
        {
            get => activeSet;
            set => SetProperty(ref activeSet, value ?? "");
        }

        [JsonProperty("sets")]
        public ObservableCollection<DocumentSet> Sets
        {
            get => sets;
            set => SetProperty(ref sets, value ?? new ObservableCollection<DocumentSet>());
        }

        [JsonProperty("ui")]
        public DocumentSetsUiSettings Ui
        {
            get => ui;
            set => SetProperty(ref ui, value ?? new DocumentSetsUiSettings());
        }
    }

    public sealed class DocumentSetsUiSettings : NotifyObject
    {
        private List<ColumnLayout> columns = new List<ColumnLayout>();

        [JsonProperty("columns")]
        public List<ColumnLayout> Columns
        {
            get => columns;
            set => SetProperty(ref columns, value ?? new List<ColumnLayout>());
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

    public sealed class DocumentSet : NotifyObject
    {
        private string name = "";
        private ObservableCollection<DocumentItem> files = new ObservableCollection<DocumentItem>();

        [JsonProperty("name")]
        public string Name
        {
            get => name;
            set => SetProperty(ref name, value ?? "");
        }

        // Runtime tree roots. Id/parent are storage-only DTO fields and are not kept in DocumentItem.
        [JsonIgnore]
        public ObservableCollection<DocumentItem> Files
        {
            get => files;
            set => SetProperty(ref files, value ?? new ObservableCollection<DocumentItem>());
        }

        // Storage format is flat. Order is exactly JSON order; nesting is restored from parent id.
        [JsonProperty("items", ObjectCreationHandling = ObjectCreationHandling.Replace)]
        public List<DocumentItemStorageDto> Items
        {
            get => BuildFlatItemsForStorage();
            set => Files = BuildTreeFromFlatItems(value);
        }

        public override string ToString() => Name;

        private List<DocumentItemStorageDto> BuildFlatItemsForStorage()
        {
            var result = new List<DocumentItemStorageDto>();
            var ids = new Dictionary<DocumentItem, string>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in Files ?? new ObservableCollection<DocumentItem>())
            {
                AppendFlatItem(item, parentId: "", result, ids, usedIds);
            }

            return result;
        }

        private static void AppendFlatItem(
            DocumentItem item,
            string parentId,
            ICollection<DocumentItemStorageDto> result,
            IDictionary<DocumentItem, string> ids,
            ISet<string> usedIds)
        {
            if (item == null)
            {
                return;
            }

            var id = CreateUniqueStorageId(item.Name, usedIds);
            ids[item] = id;

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
                Comment = item.Comment ?? ""
            });

            foreach (var child in item.Children ?? new ObservableCollection<DocumentItem>())
            {
                AppendFlatItem(child, id, result, ids, usedIds);
            }
        }

        private static ObservableCollection<DocumentItem> BuildTreeFromFlatItems(IEnumerable<DocumentItemStorageDto> flatItems)
        {
            var roots = new ObservableCollection<DocumentItem>();
            if (flatItems == null)
            {
                return roots;
            }

            var entries = flatItems.Where(x => x != null).ToList();
            var map = new Dictionary<string, DocumentItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id))
                {
                    continue;
                }

                if (!map.ContainsKey(entry.Id))
                {
                    map.Add(entry.Id, FromStorageDto(entry));
                }
            }

            foreach (var entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Id) || !map.TryGetValue(entry.Id, out var item))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(entry.ParentId) && map.TryGetValue(entry.ParentId, out var parent))
                {
                    parent.Children.Add(item);
                }
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
            // Old folders had no link type and were stored as the default enum value.
            if (source.NodeType == NodeType.Folder && type == BookmarkType.Symbol
                && string.IsNullOrWhiteSpace(source.Symbol)
                && string.IsNullOrWhiteSpace(source.Path))
            {
                type = BookmarkType.Empty;
            }

            return new DocumentItem
            {
                Name = source.Name ?? "",
                NodeType = source.NodeType,
                Type = type,
                Symbol = type == BookmarkType.Symbol ? source.Symbol ?? "" : "",
                Project = type == BookmarkType.Symbol ? source.Project ?? "" : "",
                Path = source.Path ?? "",
                Line = source.Line < 1 ? 1 : source.Line,
                Column = source.Column < 1 ? 1 : source.Column,
                Comment = source.Comment ?? "",
                Children = new ObservableCollection<DocumentItem>()
            };
        }

        private static string CreateUniqueStorageId(string name, ISet<string> usedIds)
        {
            var baseId = string.IsNullOrWhiteSpace(name) ? "node" : name.Trim();
            var id = baseId;
            var index = 1;

            while (usedIds.Contains(id))
            {
                id = baseId + "-" + index;
                index++;
            }

            usedIds.Add(id);
            return id;
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
    }

    public enum NodeType
    {
        Item = 0,
        Folder = 1
    }

    public enum BookmarkType
    {
        // Symbol keeps value 0 for compatibility with existing files where Default was omitted.
        Symbol = 0,
        Default = Symbol,
        File = 1,
        Empty = 2
    }

    public sealed class DocumentItem : NotifyObject
    {
        private string name = "";
        private BookmarkType type;
        private string symbol = "";
        private string project = "";
        private string path = "";
        private string comment = "";
        private int line = 1;
        private int column = 1;
        private NodeType nodeType;
        private bool isExpanded;
        private bool isMultiSelected;
        private ObservableCollection<DocumentItem> children = new ObservableCollection<DocumentItem>();

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

        [JsonIgnore]
        public ObservableCollection<DocumentItem> Children
        {
            get => children;
            set => SetProperty(ref children, value ?? new ObservableCollection<DocumentItem>());
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

        protected void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    internal sealed class ActiveDocumentContext
    {
        public string SolutionName { get; set; } = "";

        public string ProjectName { get; set; } = "";

        public string FileName { get; set; } = "";
    }

}
