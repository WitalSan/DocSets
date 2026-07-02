using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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

        // Backward-compatible json name. Now this is a tree root collection.
        [JsonProperty("files")]
        public ObservableCollection<DocumentItem> Files
        {
            get => files;
            set => SetProperty(ref files, value ?? new ObservableCollection<DocumentItem>());
        }

        public override string ToString() => Name;
    }

    public sealed class DocumentItem : NotifyObject
    {
        private string name = "";
        private string symbol = "";
        private string project = "";
        private string path = "";
        private string comment = "";
        private int line = 1;
        private int column = 1;
        private bool isFolder;
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

        [JsonProperty("isFolder", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool IsFolder
        {
            get => isFolder;
            set
            {
                if (SetProperty(ref isFolder, value))
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

        [JsonProperty("children")]
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
                if (IsFolder)
                {
                    return string.IsNullOrWhiteSpace(Name) ? "Новая папка" : Name;
                }

                return string.IsNullOrWhiteSpace(Name) ? $"{Path}:{Line}" : $"{Name}  ({Path}:{Line})";
            }
        }

        [JsonIgnore]
        public string Header => IsFolder ? $"📁 {Display}" : Display;

        public DocumentItem Clone()
        {
            var json = JsonConvert.SerializeObject(this);
            return JsonConvert.DeserializeObject<DocumentItem>(json) ?? new DocumentItem();
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
}
