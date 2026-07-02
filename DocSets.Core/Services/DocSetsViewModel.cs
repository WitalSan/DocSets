using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace DocSets
{
    internal sealed class DocSetsViewModel : NotifyObject
    {
        private const string ClipboardFormat = "DocSets.DocumentItems.Json";
        private const string ClipboardFormatV2 = "DocSets.DocumentItems.Json.V2";

        private readonly AsyncPackage package;
        private readonly DocSetsStore store;
        private readonly Func<Window> ownerAccessor;
        private DocumentSetsState state = new DocumentSetsState();
        private DocumentSet selectedSet;
        private DocumentItem selectedNode;
        private ObservableCollection<DocumentItem> selectedNodes = new ObservableCollection<DocumentItem>();
        private string storageText = "DocSets: загрузка...";
        private bool isLoaded;
        private bool isApplyingState;

        public DocSetsViewModel(AsyncPackage package, Func<Window> ownerAccessor)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            this.ownerAccessor = ownerAccessor ?? (() => null);
            store = new DocSetsStore(package);

            AddSetCommand = new RelayCommand(AddSet, () => IsLoaded);
            RenameSetCommand = new RelayCommand(RenameSet, () => IsLoaded && SelectedSet != null);
            DeleteSetCommand = new RelayCommand(DeleteSet, () => IsLoaded && SelectedSet != null);
            MoveSetUpCommand = new RelayCommand(() => MoveSet(-1), () => IsLoaded && CanMove(Sets, SelectedSet, -1));
            MoveSetDownCommand = new RelayCommand(() => MoveSet(1), () => IsLoaded && CanMove(Sets, SelectedSet, 1));

            AddRootFolderCommand = new RelayCommand(AddRootFolder, () => IsLoaded && SelectedSet != null);
            AddChildFolderCommand = new RelayCommand(p => AddChildFolder(p as DocumentItem ?? SelectedNode), p => IsLoaded && CanHaveChildren(p as DocumentItem ?? SelectedNode));
            AddBookmarkCommand = new RelayCommand(async p => await AddBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && SelectedSet != null);
            OpenBookmarkCommand = new RelayCommand(async p => await OpenBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && IsBookmark(p as DocumentItem ?? SelectedNode));
            UpdateBookmarkCommand = new RelayCommand(async p => await UpdateBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && IsBookmark(p as DocumentItem ?? SelectedNode));
            RenameNodeCommand = new RelayCommand(p => RenameNode(p as DocumentItem ?? SelectedNode), p => IsLoaded && (p as DocumentItem ?? SelectedNode) != null);
            DeleteNodeCommand = new RelayCommand(p => DeleteNodes(p as DocumentItem ?? SelectedNode), p => IsLoaded && ((p as DocumentItem ?? SelectedNode) != null || SelectedNodes.Count > 0));
            MoveNodeUpCommand = new RelayCommand(() => MoveNode(-1), () => IsLoaded && CanMoveNode(SelectedNode, -1));
            MoveNodeDownCommand = new RelayCommand(() => MoveNode(1), () => IsLoaded && CanMoveNode(SelectedNode, 1));

            CopySelectedNodesCommand = new RelayCommand(_ => CopySelectedNodes(), _ => IsLoaded && SelectedNodes.Count > 0);
            PasteNodesCommand = new RelayCommand(p => PasteNodes(p as DocumentItem ?? SelectedNode), _ => IsLoaded && SelectedSet != null && ClipboardContainsNodes());
            CopySelectedNodesAsJsonCommand = new RelayCommand(_ => CopySelectedNodesAsJson(), _ => IsLoaded && SelectedNodes.Count > 0);
            PasteNodesFromJsonCommand = new RelayCommand(p => PasteNodesFromJson(p as DocumentItem ?? SelectedNode), _ => IsLoaded && SelectedSet != null && ClipboardContainsJsonText());
        }

        public ObservableCollection<DocumentSet> Sets => state.Sets;

        public DocumentSetsUiSettings Ui => state.Ui;

        public bool IsLoaded
        {
            get => isLoaded;
            private set => SetProperty(ref isLoaded, value);
        }

        public DocumentSet SelectedSet
        {
            get => selectedSet;
            set
            {
                if (!SetProperty(ref selectedSet, value)) return;

                state.ActiveSet = selectedSet?.Name ?? "";
                SetSelectedNodes(Enumerable.Empty<DocumentItem>());
                OnPropertyChanged(nameof(CurrentNodes));
                InvalidateCommands();
                if (IsLoaded && !isApplyingState)
                {
                    _ = SaveAsync();
                }
            }
        }

        public ObservableCollection<DocumentItem> CurrentNodes => SelectedSet?.Files;

        public DocumentItem SelectedNode
        {
            get => selectedNode;
            set
            {
                if (SetProperty(ref selectedNode, value))
                {
                    if (value != null && SelectedNodes.Count == 0)
                    {
                        SetSelectedNodes(new[] { value });
                    }

                    InvalidateCommands();
                }
            }
        }

        public ObservableCollection<DocumentItem> SelectedNodes
        {
            get => selectedNodes;
            private set => SetProperty(ref selectedNodes, value ?? new ObservableCollection<DocumentItem>());
        }

        public void SetSelectedNodes(IEnumerable<DocumentItem> nodes)
        {
            foreach (var node in GetAllNodes())
            {
                node.IsMultiSelected = false;
            }

            var uniqueNodes = (nodes ?? Enumerable.Empty<DocumentItem>())
                .Where(x => x != null)
                .Distinct()
                .ToList();

            foreach (var node in uniqueNodes)
            {
                node.IsMultiSelected = true;
            }

            SelectedNodes = new ObservableCollection<DocumentItem>(uniqueNodes);
            selectedNode = uniqueNodes.LastOrDefault();
            OnPropertyChanged(nameof(SelectedNode));
            InvalidateCommands();
        }

        public void ToggleSelectedNode(DocumentItem node)
        {
            if (node == null) return;

            var nodes = SelectedNodes.ToList();
            if (nodes.Contains(node))
            {
                nodes.Remove(node);
            }
            else
            {
                nodes.Add(node);
            }

            SetSelectedNodes(nodes);
        }

        public void SelectRange(DocumentItem from, DocumentItem to)
        {
            if (from == null || to == null)
            {
                SetSelectedNodes(to == null ? Enumerable.Empty<DocumentItem>() : new[] { to });
                return;
            }

            var flat = GetVisibleNodes().ToList();
            var first = flat.IndexOf(from);
            var second = flat.IndexOf(to);
            if (first < 0 || second < 0)
            {
                SetSelectedNodes(new[] { to });
                return;
            }

            if (first > second)
            {
                var tmp = first;
                first = second;
                second = tmp;
            }

            SetSelectedNodes(flat.Skip(first).Take(second - first + 1));
        }

        // Compatibility aliases for old bindings/code.
        public ObservableCollection<DocumentItem> CurrentBookmarks => CurrentNodes;
        public DocumentItem SelectedBookmark
        {
            get => SelectedNode;
            set => SelectedNode = value;
        }

        public string StorageText
        {
            get => storageText;
            private set => SetProperty(ref storageText, value);
        }

        public ICommand AddSetCommand { get; }
        public ICommand RenameSetCommand { get; }
        public ICommand DeleteSetCommand { get; }
        public ICommand MoveSetUpCommand { get; }
        public ICommand MoveSetDownCommand { get; }
        public ICommand AddRootFolderCommand { get; }
        public ICommand AddChildFolderCommand { get; }
        public ICommand AddBookmarkCommand { get; }
        public ICommand OpenBookmarkCommand { get; }
        public ICommand UpdateBookmarkCommand { get; }
        public ICommand RenameNodeCommand { get; }
        public ICommand DeleteNodeCommand { get; }
        public ICommand MoveNodeUpCommand { get; }
        public ICommand MoveNodeDownCommand { get; }
        public ICommand CopySelectedNodesCommand { get; }
        public ICommand PasteNodesCommand { get; }
        public ICommand CopySelectedNodesAsJsonCommand { get; }
        public ICommand PasteNodesFromJsonCommand { get; }

        // Compatibility aliases for old command names.
        public ICommand RenameBookmarkCommand => RenameNodeCommand;
        public ICommand DeleteBookmarkCommand => DeleteNodeCommand;
        public ICommand MoveBookmarkUpCommand => MoveNodeUpCommand;
        public ICommand MoveBookmarkDownCommand => MoveNodeDownCommand;
        public ICommand CopySelectedBookmarksCommand => CopySelectedNodesCommand;

        public async Task LoadAsync()
        {
            var loadedState = await store.LoadAsync();

            if (loadedState == null)
            {
                IsLoaded = false;
                state = new DocumentSetsState();
                selectedSet = null;
                selectedNode = null;
                SetSelectedNodes(Enumerable.Empty<DocumentItem>());
                OnPropertyChanged(nameof(Sets));
                OnPropertyChanged(nameof(Ui));
                OnPropertyChanged(nameof(SelectedSet));
                OnPropertyChanged(nameof(SelectedNode));
                OnPropertyChanged(nameof(CurrentNodes));
                StorageText = "DocSets: solution ещё не открыт";
                InvalidateCommands();
                return;
            }

            state = loadedState;
            if (state.Ui == null) state.Ui = new DocumentSetsUiSettings();
            NormalizeNodes(state.Sets.SelectMany(x => x.Files));

            if (state.Sets.Count == 0)
            {
                state.Sets.Add(new DocumentSet { Name = "Default" });
                state.ActiveSet = "Default";
            }

            isApplyingState = true;
            try
            {
                IsLoaded = true;
                OnPropertyChanged(nameof(Sets));
                OnPropertyChanged(nameof(Ui));
                SelectedSet = state.Sets.FirstOrDefault(x => x.Name == state.ActiveSet) ?? state.Sets.FirstOrDefault();
                StorageText = string.IsNullOrWhiteSpace(store.StateFilePath)
                    ? "DocSets: откройте solution (.sln)"
                    : $"DocSets: {store.StateFilePath}";
            }
            finally
            {
                isApplyingState = false;
            }

            InvalidateCommands();
        }

        private void AddSet()
        {
            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", "Название группы:");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (state.Sets.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Show("Группа с таким именем уже есть.");
                return;
            }

            var set = new DocumentSet { Name = name };
            state.Sets.Add(set);
            SelectedSet = set;
            _ = SaveAsync();
            InvalidateCommands();
        }

        private void RenameSet()
        {
            var set = SelectedSet;
            if (set == null) return;
            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", "Новое название группы:", set.Name);
            TryRenameSet(set, name, showErrors: true);
        }

        public bool TryRenameSelectedSet(string name)
        {
            return TryRenameSet(SelectedSet, name, showErrors: true);
        }

        public bool TryRenameSet(DocumentSet set, string name, bool showErrors)
        {
            if (set == null) return false;
            if (string.IsNullOrWhiteSpace(name)) return false;

            name = name.Trim();
            if (string.Equals(set.Name, name, StringComparison.Ordinal))
            {
                return true;
            }

            if (state.Sets.Any(x => !ReferenceEquals(x, set) && string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                if (showErrors)
                {
                    Show("Группа с таким именем уже есть.");
                }

                return false;
            }

            set.Name = name;
            state.ActiveSet = name;
            OnPropertyChanged(nameof(Sets));
            OnPropertyChanged(nameof(SelectedSet));
            _ = SaveAsync();
            InvalidateCommands();
            return true;
        }

        private void DeleteSet()
        {
            var set = SelectedSet;
            if (set == null) return;
            if (MessageBox.Show(ownerAccessor(), $"Удалить группу '{set.Name}'?", "DocSets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            state.Sets.Remove(set);
            if (state.Sets.Count == 0)
            {
                state.Sets.Add(new DocumentSet { Name = "Default" });
            }

            SelectedSet = state.Sets.FirstOrDefault();
            state.ActiveSet = SelectedSet?.Name ?? "";
            _ = SaveAsync();
            InvalidateCommands();
        }

        private void AddRootFolder()
        {
            var set = SelectedSet;
            if (set == null) return;

            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", "Название папки:", "Новая папка");
            if (string.IsNullOrWhiteSpace(name)) return;

            var folder = new DocumentItem { Name = name.Trim(), IsFolder = true, IsExpanded = true };
            set.Files.Add(folder);
            SelectedNode = folder;
            _ = SaveAsync();
            InvalidateCommands();
        }

        private void AddChildFolder(DocumentItem parent)
        {
            if (!CanHaveChildren(parent)) return;

            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", "Название папки:", "Новая папка");
            if (string.IsNullOrWhiteSpace(name)) return;

            var folder = new DocumentItem { Name = name.Trim(), IsFolder = true, IsExpanded = true };
            parent.Children.Add(folder);
            parent.IsExpanded = true;
            SelectedNode = folder;
            _ = SaveAsync();
            InvalidateCommands();
        }

        private async Task AddBookmarkAsync(DocumentItem target)
        {
            var set = SelectedSet;
            if (set == null) return;
            var bookmark = await store.CreateBookmarkFromActiveDocumentAsync();
            if (bookmark == null)
            {
                Show("Не найден активный документ редактора.");
                return;
            }

            bookmark.IsFolder = false;
            bookmark.Children.Clear();

            var collection = GetInsertCollection(target);
            collection.Add(bookmark);
            if (target?.IsFolder == true) target.IsExpanded = true;
            SelectedNode = bookmark;
            await SaveAsync();
            OnPropertyChanged(nameof(CurrentNodes));
        }

        private async Task OpenBookmarkAsync(DocumentItem item)
        {
            if (!IsBookmark(item)) return;
            await store.OpenBookmarkAsync(item);
        }

        private async Task UpdateBookmarkAsync(DocumentItem item)
        {
            if (!IsBookmark(item)) return;
            var updated = await store.CreateBookmarkFromActiveDocumentAsync();
            if (updated == null)
            {
                Show("Не найден активный документ редактора.");
                return;
            }

            item.Name = updated.Name;
            item.Symbol = updated.Symbol;
            item.Project = updated.Project;
            item.Path = updated.Path;
            item.Line = updated.Line;
            item.Column = updated.Column;
            item.IsFolder = false;
            SelectedNode = item;
            await SaveAsync();
        }

        private void RenameNode(DocumentItem item)
        {
            if (item == null) return;
            var caption = item.IsFolder ? "Новое название папки:" : "Новое название закладки:";
            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", caption, item.Name);
            if (string.IsNullOrWhiteSpace(name)) return;

            item.Name = name.Trim();
            SelectedNode = item;
            _ = SaveAsync();
        }

        private void DeleteNodes(DocumentItem item)
        {
            if (SelectedSet == null) return;

            var nodes = GetEffectiveNodes(item).ToList();
            if (nodes.Count == 0) return;

            var text = nodes.Count == 1
                ? (nodes[0].IsFolder ? $"Удалить папку '{nodes[0].Name}' и все вложенные элементы?" : $"Удалить закладку '{nodes[0].Name}'?")
                : $"Удалить выбранные элементы ({nodes.Count})?";

            if (MessageBox.Show(ownerAccessor(), text, "DocSets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            foreach (var node in FilterOutDescendants(nodes))
            {
                FindOwnerCollection(node)?.Remove(node);
            }

            SetSelectedNodes(Enumerable.Empty<DocumentItem>());
            _ = SaveAsync();
            InvalidateCommands();
        }

        private void MoveSet(int delta)
        {
            Move(state.Sets, SelectedSet, delta);
            state.ActiveSet = SelectedSet?.Name ?? "";
            _ = SaveAsync();
        }

        private void MoveNode(int delta)
        {
            var collection = FindOwnerCollection(SelectedNode);
            Move(collection, SelectedNode, delta);
            _ = SaveAsync();
        }

        private void CopySelectedNodes()
        {
            var selected = FilterOutDescendants(SelectedNodes).ToList();
            if (selected.Count == 0) return;

            var nodes = selected.Select(x => x.Clone()).ToList();
            var json = SerializeClipboardNodes(nodes);

            var data = new DataObject();
            // V2 is an explicit clipboard DTO. It contains every bookmark field including Comment.
            data.SetData(ClipboardFormatV2, json);
            // Keep old format name for compatibility with already installed builds.
            data.SetData(ClipboardFormat, json);
            data.SetText(BuildText(nodes));
            Clipboard.SetDataObject(data, true);
        }

        private void CopySelectedNodesAsJson()
        {
            var selected = FilterOutDescendants(SelectedNodes).ToList();
            if (selected.Count == 0) return;

            var nodes = selected.Select(x => x.Clone()).ToList();
            var json = SerializeClipboardNodes(nodes);

            var data = new DataObject();
            data.SetData(ClipboardFormatV2, json);
            data.SetData(ClipboardFormat, json);
            data.SetText(json);
            Clipboard.SetDataObject(data, true);
        }

        private void PasteNodes(DocumentItem target)
        {
            if (SelectedSet == null || !ClipboardContainsNodes()) return;

            try
            {
                var json = Clipboard.ContainsData(ClipboardFormatV2)
                    ? Clipboard.GetData(ClipboardFormatV2) as string
                    : Clipboard.GetData(ClipboardFormat) as string;

                PasteNodesFromJsonText(target, json);
            }
            catch
            {
                Show("Не удалось вставить элементы из буфера.");
            }
        }

        private void PasteNodesFromJson(DocumentItem target)
        {
            if (SelectedSet == null || !ClipboardContainsJsonText()) return;

            try
            {
                PasteNodesFromJsonText(target, Clipboard.GetText());
            }
            catch
            {
                Show("Буфер не содержит корректный JSON DocSets.");
            }
        }

        private void PasteNodesFromJsonText(DocumentItem target, string json)
        {
            var nodes = DeserializeClipboardNodes(json);
            if (nodes.Count == 0) return;

            NormalizeNodes(nodes);
            var collection = GetInsertCollection(target);

            DocumentItem last = null;
            foreach (var node in nodes.Select(x => x.Clone()))
            {
                collection.Add(node);
                last = node;
            }

            if (target?.IsFolder == true) target.IsExpanded = true;
            if (last != null) SelectedNode = last;
            _ = SaveAsync();
            InvalidateCommands();
        }


        public bool CanMoveSelectedNodesTo(DocumentItem target, DropPosition position)
        {
            return GetMovePlan(target, position) != null;
        }

        public async Task MoveSelectedNodesToAsync(DocumentItem target, DropPosition position)
        {
            var plan = GetMovePlan(target, position);
            if (plan == null) return;

            var nodes = FilterOutDescendants(SelectedNodes).ToList();
            if (nodes.Count == 0) return;

            foreach (var node in nodes)
            {
                FindOwnerCollection(node)?.Remove(node);
            }

            var insertIndex = Math.Max(0, Math.Min(plan.Index, plan.Collection.Count));
            foreach (var node in nodes)
            {
                plan.Collection.Insert(insertIndex++, node);
            }

            if (target?.IsFolder == true && position == DropPosition.Inside)
            {
                target.IsExpanded = true;
            }

            SetSelectedNodes(nodes);
            await SaveAsync();
            InvalidateCommands();
        }

        private MovePlan GetMovePlan(DocumentItem target, DropPosition position)
        {
            if (SelectedSet == null || SelectedNodes.Count == 0) return null;

            var nodes = FilterOutDescendants(SelectedNodes).ToList();
            if (nodes.Count == 0) return null;

            foreach (var node in nodes)
            {
                if (ReferenceEquals(node, target) || IsDescendantOf(target, node))
                {
                    return null;
                }
            }

            if (target == null)
            {
                return new MovePlan(SelectedSet.Files, SelectedSet.Files.Count);
            }

            if (position == DropPosition.Inside && target.IsFolder)
            {
                return new MovePlan(target.Children, target.Children.Count);
            }

            var owner = FindOwnerCollection(target);
            if (owner == null) return null;

            var targetIndex = owner.IndexOf(target);
            if (targetIndex < 0) return null;

            var selectedBeforeTarget = nodes.Count(x => ReferenceEquals(FindOwnerCollection(x), owner) && owner.IndexOf(x) >= 0 && owner.IndexOf(x) < targetIndex);
            var index = position == DropPosition.After ? targetIndex + 1 : targetIndex;
            index -= selectedBeforeTarget;
            return new MovePlan(owner, index);
        }

        private IEnumerable<DocumentItem> GetEffectiveNodes(DocumentItem item)
        {
            if (SelectedNodes.Count > 0 && (item == null || SelectedNodes.Contains(item)))
            {
                return SelectedNodes;
            }

            return item == null ? Enumerable.Empty<DocumentItem>() : new[] { item };
        }

        private ObservableCollection<DocumentItem> GetInsertCollection(DocumentItem target)
        {
            if (target?.IsFolder == true)
            {
                return target.Children;
            }

            return SelectedSet?.Files ?? new ObservableCollection<DocumentItem>();
        }

        private ObservableCollection<DocumentItem> FindOwnerCollection(DocumentItem item)
        {
            if (SelectedSet == null || item == null) return null;
            if (SelectedSet.Files.Contains(item)) return SelectedSet.Files;
            return FindOwnerCollection(SelectedSet.Files, item);
        }

        private static ObservableCollection<DocumentItem> FindOwnerCollection(IEnumerable<DocumentItem> nodes, DocumentItem item)
        {
            foreach (var node in nodes)
            {
                if (node.Children.Contains(item)) return node.Children;
                var nested = FindOwnerCollection(node.Children, item);
                if (nested != null) return nested;
            }

            return null;
        }


        private IEnumerable<DocumentItem> GetAllNodes()
        {
            return SelectedSet?.Files == null ? Enumerable.Empty<DocumentItem>() : Flatten(SelectedSet.Files, includeCollapsed: true);
        }

        private IEnumerable<DocumentItem> GetVisibleNodes()
        {
            return SelectedSet?.Files == null ? Enumerable.Empty<DocumentItem>() : Flatten(SelectedSet.Files, includeCollapsed: false);
        }

        private static IEnumerable<DocumentItem> Flatten(IEnumerable<DocumentItem> nodes, bool includeCollapsed)
        {
            foreach (var node in nodes)
            {
                yield return node;
                if (includeCollapsed || node.IsExpanded)
                {
                    foreach (var child in Flatten(node.Children, includeCollapsed))
                    {
                        yield return child;
                    }
                }
            }
        }

        private static IEnumerable<DocumentItem> FilterOutDescendants(IEnumerable<DocumentItem> nodes)
        {
            var list = (nodes ?? Enumerable.Empty<DocumentItem>()).Where(x => x != null).Distinct().ToList();
            return list.Where(x => !list.Any(parent => !ReferenceEquals(parent, x) && IsDescendantOf(x, parent))).ToList();
        }

        private static bool IsDescendantOf(DocumentItem node, DocumentItem potentialParent)
        {
            if (node == null || potentialParent == null) return false;
            foreach (var child in potentialParent.Children)
            {
                if (ReferenceEquals(child, node) || IsDescendantOf(node, child)) return true;
            }

            return false;
        }

        private bool CanMoveNode(DocumentItem item, int delta)
        {
            var collection = FindOwnerCollection(item);
            return CanMove(collection, item, delta);
        }

        private static bool IsBookmark(DocumentItem item) => item != null && !item.IsFolder;

        private static bool CanHaveChildren(DocumentItem item) => item != null && item.IsFolder;

        private static void NormalizeNodes(IEnumerable<DocumentItem> nodes)
        {
            if (nodes == null) return;

            foreach (var node in nodes)
            {
                if (node.Children == null)
                {
                    node.Children = new ObservableCollection<DocumentItem>();
                }

                NormalizeNodes(node.Children);
            }
        }

        private static string BuildText(IEnumerable<DocumentItem> nodes)
        {
            var lines = new List<string>();
            foreach (var node in nodes)
            {
                AppendText(node, 0, lines);
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static void AppendText(DocumentItem node, int level, ICollection<string> lines)
        {
            var indent = new string(' ', level * 2);
            if (node.IsFolder)
            {
                lines.Add($"{indent}[{node.Name}]");
                foreach (var child in node.Children)
                {
                    AppendText(child, level + 1, lines);
                }
                return;
            }

            var location = string.IsNullOrWhiteSpace(node.Path) ? "" : $" — {node.Path}:{node.Line}:{node.Column}";
            lines.Add($"{indent}{node.Name}{location}");

            if (!string.IsNullOrWhiteSpace(node.Comment))
            {
                var commentLines = node.Comment.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (var commentLine in commentLines)
                {
                    lines.Add($"{indent}  # {commentLine}");
                }
            }
        }

        private static string SerializeClipboardNodes(IEnumerable<DocumentItem> nodes)
        {
            var clipboardNodes = (nodes ?? Enumerable.Empty<DocumentItem>()).Select(ToClipboardNode).ToList();
            return JsonConvert.SerializeObject(clipboardNodes, Formatting.Indented);
        }

        private static List<DocumentItem> DeserializeClipboardNodes(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<DocumentItem>();
            }

            try
            {
                var clipboardNodes = JsonConvert.DeserializeObject<List<ClipboardNode>>(json);
                if (clipboardNodes != null)
                {
                    return clipboardNodes.Select(FromClipboardNode).ToList();
                }
            }
            catch
            {
                // Try other supported JSON shapes below.
            }

            try
            {
                var clipboardNode = JsonConvert.DeserializeObject<ClipboardNode>(json);
                if (clipboardNode != null && (!string.IsNullOrEmpty(clipboardNode.Name) || clipboardNode.Children != null))
                {
                    return new List<DocumentItem> { FromClipboardNode(clipboardNode) };
                }
            }
            catch
            {
                // Fallback below supports the previous raw DocumentItem clipboard format.
            }

            try
            {
                return JsonConvert.DeserializeObject<List<DocumentItem>>(json) ?? new List<DocumentItem>();
            }
            catch
            {
                var item = JsonConvert.DeserializeObject<DocumentItem>(json);
                return item == null ? new List<DocumentItem>() : new List<DocumentItem> { item };
            }
        }

        private static ClipboardNode ToClipboardNode(DocumentItem item)
        {
            var node = new ClipboardNode
            {
                Name = item.Name ?? string.Empty,
                IsFolder = item.IsFolder,
                Symbol = item.Symbol ?? string.Empty,
                Project = item.Project ?? string.Empty,
                Path = item.Path ?? string.Empty,
                Line = item.Line,
                Column = item.Column,
                Comment = item.Comment ?? string.Empty,
                Children = new List<ClipboardNode>()
            };

            if (item.Children != null)
            {
                node.Children.AddRange(item.Children.Select(ToClipboardNode));
            }

            return node;
        }

        private static DocumentItem FromClipboardNode(ClipboardNode source)
        {
            var item = new DocumentItem
            {
                Name = source.Name ?? string.Empty,
                IsFolder = source.IsFolder,
                Symbol = source.Symbol ?? string.Empty,
                Project = source.Project ?? string.Empty,
                Path = source.Path ?? string.Empty,
                Line = source.Line < 1 ? 1 : source.Line,
                Column = source.Column < 1 ? 1 : source.Column,
                Comment = source.Comment ?? string.Empty,
                Children = new ObservableCollection<DocumentItem>()
            };

            if (source.Children != null)
            {
                foreach (var child in source.Children)
                {
                    item.Children.Add(FromClipboardNode(child));
                }
            }

            return item;
        }

        private sealed class ClipboardNode
        {
            [JsonProperty("name")]
            public string Name { get; set; }

            [JsonProperty("isFolder")]
            public bool IsFolder { get; set; }

            [JsonProperty("symbol")]
            public string Symbol { get; set; }

            [JsonProperty("project")]
            public string Project { get; set; }

            [JsonProperty("path")]
            public string Path { get; set; }

            [JsonProperty("line")]
            public int Line { get; set; }

            [JsonProperty("column")]
            public int Column { get; set; }

            [JsonProperty("comment")]
            public string Comment { get; set; }

            [JsonProperty("children")]
            public List<ClipboardNode> Children { get; set; }
        }

        private static bool ClipboardContainsNodes()
        {
            try
            {
                return Clipboard.ContainsData(ClipboardFormatV2) || Clipboard.ContainsData(ClipboardFormat);
            }
            catch
            {
                return false;
            }
        }

        private static bool ClipboardContainsJsonText()
        {
            try
            {
                if (!Clipboard.ContainsText()) return false;
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text)) return false;

                var trimmed = text.TrimStart();
                return trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private void Move<T>(ObservableCollection<T> collection, T item, int delta) where T : class
        {
            if (collection == null || item == null) return;
            var index = collection.IndexOf(item);
            var newIndex = index + delta;
            if (index < 0 || newIndex < 0 || newIndex >= collection.Count) return;
            collection.Move(index, newIndex);
            InvalidateCommands();
        }

        private static bool CanMove<T>(ObservableCollection<T> collection, T item, int delta) where T : class
        {
            if (collection == null || item == null) return false;
            var index = collection.IndexOf(item);
            var newIndex = index + delta;
            return index >= 0 && newIndex >= 0 && newIndex < collection.Count;
        }

        public async Task SaveAsync()
        {
            if (!IsLoaded)
            {
                return;
            }

            await store.SaveAsync(state);
        }

        private void InvalidateCommands() => CommandManager.InvalidateRequerySuggested();

        private void Show(string text)
        {
            VsShellUtilities.ShowMessageBox(package, text, "DocSets", OLEMSGICON.OLEMSGICON_INFO, OLEMSGBUTTON.OLEMSGBUTTON_OK, OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        private sealed class MovePlan
        {
            public MovePlan(ObservableCollection<DocumentItem> collection, int index)
            {
                Collection = collection;
                Index = index;
            }

            public ObservableCollection<DocumentItem> Collection { get; }
            public int Index { get; }
        }
    }

    internal enum DropPosition
    {
        Before,
        Inside,
        After
    }
}
