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
    internal enum ActiveDocumentFolderKind
    {
        Solution,
        Project,
        File,
        Class
    }

    internal sealed class DocSetsViewModel : NotifyObject
    {
        private readonly AsyncPackage package;
        private readonly DocSetsStore store;
        private readonly FileBookmarkTrackingService fileTracking;
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
            fileTracking = new FileBookmarkTrackingService(package, store.ToFullPath);

            AddSetCommand = new RelayCommand(AddSet, () => IsLoaded);
            RenameSetCommand = new RelayCommand(RenameSet, () => IsLoaded && SelectedSet != null);
            DeleteSetCommand = new RelayCommand(DeleteSet, () => IsLoaded && SelectedSet != null);
            MoveSetUpCommand = new RelayCommand(() => MoveSet(-1), () => IsLoaded && CanMove(Sets, SelectedSet, -1));
            MoveSetDownCommand = new RelayCommand(() => MoveSet(1), () => IsLoaded && CanMove(Sets, SelectedSet, 1));

            AddFolderCommand = new RelayCommand(p => AddFolder(p as DocumentItem ?? SelectedNode), p => IsLoaded && SelectedSet != null);
            AddRootFolderCommand = AddFolderCommand;
            AddChildFolderCommand = AddFolderCommand;
            AddBookmarkCommand = new RelayCommand(async p => await AddBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && SelectedSet != null);
            OpenBookmarkCommand = new RelayCommand(async p => await OpenBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && IsBookmark(p as DocumentItem ?? SelectedNode));
            UpdateBookmarkCommand = new RelayCommand(async p => await UpdateBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && IsBookmark(p as DocumentItem ?? SelectedNode));
            RenameNodeCommand = new RelayCommand(p => RenameNode(p as DocumentItem ?? SelectedNode), p => IsLoaded && (p as DocumentItem ?? SelectedNode) != null);
            DeleteNodeCommand = new RelayCommand(p => DeleteNodes(p as DocumentItem ?? SelectedNode), p => IsLoaded && ((p as DocumentItem ?? SelectedNode) != null || SelectedNodes.Count > 0));
            MoveNodeUpCommand = new RelayCommand(() => MoveNode(-1), () => IsLoaded && CanMoveNode(SelectedNode, -1));
            MoveNodeDownCommand = new RelayCommand(() => MoveNode(1), () => IsLoaded && CanMoveNode(SelectedNode, 1));

            CopySelectedNodesCommand = new RelayCommand(_ => CopySelectedNodes(), _ => IsLoaded && SelectedNodes.Count > 0);
            PasteNodesCommand = new RelayCommand(p => PasteNodes(p as DocumentItem ?? SelectedNode), _ => IsLoaded && SelectedSet != null && ClipboardContainsText());
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
        public ICommand AddFolderCommand { get; }
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
                    : store.IsSharedWorkspace
                        ? $"DocSets workspace: {store.StateFilePath}"
                        : $"DocSets solution: {store.StateFilePath}";
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
        private void AddFolder(DocumentItem parent)
        {
            var set = SelectedSet;
            if (set == null) return;

            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", "Название папки:", "Новая папка");
            if (string.IsNullOrWhiteSpace(name)) return;

            var folder = new DocumentItem { Name = name.Trim(), NodeType = NodeType.Folder, Type = BookmarkType.Empty, IsExpanded = true };
            if (parent != null && parent.NodeType == NodeType.Folder)
            {
                parent.Children.Add(folder);
                parent.IsExpanded = true;
            }
            else
            {
                set.Files.Add(folder);
            }

            SelectedNode = folder;
            _ = SaveAsync();
            InvalidateCommands();
        }

        public async Task<DocumentItem> CreateBookmarkFromActiveDocumentAsync(bool showErrors)
        {
            if (!IsLoaded || SelectedSet == null)
            {
                if (showErrors)
                {
                    Show("Откройте solution (.sln), чтобы создать закладку DocSets.");
                }

                return null;
            }

            var bookmark = await store.CreateBookmarkFromActiveDocumentAsync();
            if (bookmark == null)
            {
                if (showErrors)
                {
                    Show("Не найден активный документ редактора.");
                }

                return null;
            }

            bookmark.NodeType = NodeType.Item;
            bookmark.Children.Clear();
            if (bookmark.Type == BookmarkType.File)
            {
                await fileTracking.TrackFromActiveDocumentAsync(bookmark);
            }
            return bookmark;
        }

        public Task AddPreparedBookmarkAsync(DocumentItem bookmark, DocumentItem target)
        {
            return AddPreparedBookmarkAsync(bookmark, SelectedSet, target);
        }

        public DocumentSet GetSetContainingNode(DocumentItem item)
        {
            if (item == null)
            {
                return null;
            }

            return state.Sets.FirstOrDefault(set => ContainsNode(set.Files, item));
        }

        public DocumentItem GetParentFolder(DocumentItem item)
        {
            if (item == null)
            {
                return null;
            }

            foreach (var set in state.Sets)
            {
                var parent = FindParentFolder(set.Files, item);
                if (parent != null)
                {
                    return parent;
                }
            }

            return null;
        }

        public async Task MoveExistingNodeAsync(DocumentItem item, DocumentSet destinationSet, DocumentItem destinationParent)
        {
            if (item == null)
            {
                return;
            }

            var currentSet = GetSetContainingNode(item);
            var targetSet = destinationSet ?? currentSet ?? SelectedSet;
            if (targetSet == null)
            {
                return;
            }

            if (destinationParent != null)
            {
                if (destinationParent.NodeType != NodeType.Folder || !ContainsNode(targetSet.Files, destinationParent))
                {
                    destinationParent = null;
                }

                if (ReferenceEquals(destinationParent, item) || IsDescendantOf(destinationParent, item))
                {
                    destinationParent = null;
                }
            }

            var targetCollection = destinationParent?.NodeType == NodeType.Folder
                ? destinationParent.Children
                : targetSet.Files;

            // При редактировании свойств тот же экземпляр item не должен быть добавлен
            // в дерево повторно. Если место назначения изменилось, удаляем все
            // вхождения этой ссылки из всех групп и добавляем ровно один раз.
            var currentOwner = currentSet == null ? null : FindOwnerCollection(currentSet.Files, item);
            var destinationChanged = !ReferenceEquals(currentOwner, targetCollection);
            var alreadyInTarget = ContainsReference(targetCollection, item);

            if (destinationChanged || !alreadyInTarget)
            {
                RemoveNodeReferenceFromAllSets(item);
                if (!ContainsReference(targetCollection, item))
                {
                    targetCollection.Add(item);
                }
            }

            if (destinationParent?.NodeType == NodeType.Folder)
            {
                destinationParent.IsExpanded = true;
            }

            SelectedSet = targetSet;
            SelectedNode = item;
            SetSelectedNodes(new[] { item });
            await SaveAsync();
            OnPropertyChanged(nameof(CurrentNodes));
            InvalidateCommands();
        }

        public async Task AddPreparedBookmarkAsync(DocumentItem bookmark, DocumentSet destinationSet, DocumentItem target)
        {
            var set = destinationSet ?? SelectedSet;
            if (set == null || bookmark == null)
            {
                return;
            }

            bookmark.NodeType = NodeType.Item;
            bookmark.Children.Clear();

            if (target != null && !ContainsNode(set.Files, target))
            {
                target = null;
            }

            var collection = GetInsertCollection(set, target);
            collection.Add(bookmark);
            if (target?.NodeType == NodeType.Folder) target.IsExpanded = true;

            SelectedSet = set;
            SelectedNode = bookmark;
            SetSelectedNodes(new[] { bookmark });
            if (bookmark.Type == BookmarkType.File)
            {
                await fileTracking.TrackFromActiveDocumentAsync(bookmark);
            }
            await SaveAsync();
            OnPropertyChanged(nameof(CurrentNodes));
            InvalidateCommands();
        }

        public async Task<DocumentItem> CreateFolderFromActiveDocumentAsync(ActiveDocumentFolderKind kind, DocumentItem target)
        {
            if (!IsLoaded || SelectedSet == null)
            {
                Show("Откройте solution (.sln), чтобы создать папку DocSets.");
                return null;
            }

            var context = await store.GetActiveDocumentContextAsync();
            if (context == null)
            {
                Show("Не найден активный документ редактора.");
                return null;
            }

            var name = GetContextFolderName(context, kind);
            if (string.IsNullOrWhiteSpace(name))
            {
                Show("Не удалось определить имя для папки.");
                return null;
            }

            var set = SelectedSet;
            DocumentItem folder;

            if (kind == ActiveDocumentFolderKind.File)
            {
                folder = await store.CreateBookmarkFromActiveDocumentAsync();
                if (folder == null)
                {
                    Show("Не удалось создать ссылку на текущий файл.");
                    return null;
                }

                folder.Name = name.Trim();
                folder.NodeType = NodeType.Folder;
                folder.Type = BookmarkType.File;
                folder.Symbol = string.Empty;
                folder.Project = string.Empty;
                folder.Children.Clear();
                folder.IsExpanded = true;
                await fileTracking.TrackFromActiveDocumentAsync(folder);
            }
            else if (kind == ActiveDocumentFolderKind.Class)
            {
                folder = await store.CreateClassBookmarkFromActiveDocumentAsync();
                if (folder == null || string.IsNullOrWhiteSpace(folder.Symbol))
                {
                    Show("Не удалось определить класс в текущей позиции редактора.");
                    return null;
                }

                folder.NodeType = NodeType.Folder;
                folder.Type = BookmarkType.Symbol;
                folder.Children.Clear();
                folder.IsExpanded = true;
            }
            else
            {
                folder = new DocumentItem
                {
                    Name = name.Trim(),
                    NodeType = NodeType.Folder,
                    Type = BookmarkType.Empty,
                    IsExpanded = true
                };
            }

            if (target != null && !ContainsNode(set.Files, target))
            {
                target = null;
            }

            var collection = GetInsertCollection(set, target);
            collection.Add(folder);
            if (target?.NodeType == NodeType.Folder) target.IsExpanded = true;

            SelectedNode = folder;
            SetSelectedNodes(new[] { folder });
            await SaveAsync();
            OnPropertyChanged(nameof(CurrentNodes));
            InvalidateCommands();
            return folder;
        }

        public async Task<ActiveDocumentContext> GetActiveDocumentContextAsync()
        {
            if (!IsLoaded || SelectedSet == null)
            {
                return null;
            }

            return await store.GetActiveDocumentContextAsync();
        }

        private static string GetContextFolderName(ActiveDocumentContext context, ActiveDocumentFolderKind kind)
        {
            if (context == null) return "";

            switch (kind)
            {
                case ActiveDocumentFolderKind.Solution:
                    return context.SolutionName;
                case ActiveDocumentFolderKind.Project:
                    return context.ProjectName;
                case ActiveDocumentFolderKind.File:
                    return context.FileName;
                case ActiveDocumentFolderKind.Class:
                    return string.IsNullOrWhiteSpace(context.ClassName) ? "Class" : context.ClassName;
                default:
                    return "";
            }
        }

        private async Task AddBookmarkAsync(DocumentItem target)
        {
            var bookmark = await CreateBookmarkFromActiveDocumentAsync(showErrors: true);
            if (bookmark == null)
            {
                return;
            }

            await AddPreparedBookmarkAsync(bookmark, target);
        }

        private async Task OpenBookmarkAsync(DocumentItem item)
        {
            if (!IsBookmark(item)) return;
            await store.OpenBookmarkAsync(item);
            if (item.Type == BookmarkType.File)
            {
                await fileTracking.TrackAfterOpenAsync(item);
            }
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

            if (item.Type == BookmarkType.File)
            {
                item.Symbol = string.Empty;
                item.Project = string.Empty;
                item.Path = updated.Path;
                item.Line = updated.Line;
                item.Column = updated.Column;
            }
            else
            {
                item.Name = updated.Name;
                item.Symbol = updated.Symbol;
                item.Project = updated.Project;
                item.Path = updated.Path;
                item.Line = updated.Line;
                item.Column = updated.Column;
            }

            SelectedNode = item;
            if (item.Type == BookmarkType.File)
            {
                await fileTracking.TrackFromActiveDocumentAsync(item);
            }
            await SaveAsync();
        }

        private void RenameNode(DocumentItem item)
        {
            if (item == null) return;
            var caption = item.NodeType == NodeType.Folder ? "Новое название папки:" : "Новое название закладки:";
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
                ? (nodes[0].NodeType == NodeType.Folder ? $"Удалить папку '{nodes[0].Name}' и все вложенные элементы?" : $"Удалить закладку '{nodes[0].Name}'?")
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
            data.SetText(json);
            Clipboard.SetDataObject(data, true);
        }

        private void PasteNodes(DocumentItem target)
        {
            if (SelectedSet == null || !ClipboardContainsText()) return;

            try
            {
                var text = Clipboard.GetText();
                var nodes = TryParseClipboardJson(text, out var jsonNodes)
                    ? jsonNodes
                    : ParseClipboardText(text);

                PasteNodes(target, nodes);
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
            PasteNodes(target, nodes);
        }

        private void PasteNodes(DocumentItem target, IList<DocumentItem> nodes)
        {
            if (nodes == null || nodes.Count == 0) return;

            NormalizeNodes(nodes);
            var collection = GetInsertCollection(target);

            DocumentItem last = null;
            foreach (var node in nodes.Select(x => x.Clone()))
            {
                collection.Add(node);
                last = node;
            }

            if (target?.NodeType == NodeType.Folder) target.IsExpanded = true;
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

            if (target?.NodeType == NodeType.Folder && position == DropPosition.Inside)
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

            if (position == DropPosition.Inside && target.NodeType == NodeType.Folder)
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
            return GetInsertCollection(SelectedSet, target);
        }

        private static ObservableCollection<DocumentItem> GetInsertCollection(DocumentSet set, DocumentItem target)
        {
            if (target?.NodeType == NodeType.Folder)
            {
                return target.Children;
            }

            return set?.Files ?? new ObservableCollection<DocumentItem>();
        }

        private void RemoveNodeReferenceFromAllSets(DocumentItem item)
        {
            if (item == null || state?.Sets == null)
            {
                return;
            }

            foreach (var set in state.Sets)
            {
                RemoveNodeReference(set.Files, item);
            }
        }

        private static bool RemoveNodeReference(ObservableCollection<DocumentItem> nodes, DocumentItem item)
        {
            if (nodes == null || item == null)
            {
                return false;
            }

            var removed = false;
            for (var i = nodes.Count - 1; i >= 0; i--)
            {
                var node = nodes[i];
                if (ReferenceEquals(node, item))
                {
                    nodes.RemoveAt(i);
                    removed = true;
                    continue;
                }

                if (RemoveNodeReference(node.Children, item))
                {
                    removed = true;
                }
            }

            return removed;
        }

        private static bool ContainsReference(IEnumerable<DocumentItem> nodes, DocumentItem item)
        {
            return nodes != null && item != null && nodes.Any(x => ReferenceEquals(x, item));
        }

        private static bool ContainsNode(IEnumerable<DocumentItem> nodes, DocumentItem item)
        {
            if (nodes == null || item == null) return false;

            foreach (var node in nodes)
            {
                if (ReferenceEquals(node, item) || ContainsNode(node.Children, item))
                {
                    return true;
                }
            }

            return false;
        }

        private static DocumentItem FindParentFolder(IEnumerable<DocumentItem> nodes, DocumentItem item)
        {
            if (nodes == null || item == null) return null;

            foreach (var node in nodes)
            {
                if (node.Children != null && node.Children.Contains(item))
                {
                    return node;
                }

                var nested = FindParentFolder(node.Children, item);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
        }

        private ObservableCollection<DocumentItem> FindOwnerCollection(DocumentItem item)
        {
            if (SelectedSet == null || item == null) return null;
            if (SelectedSet.Files.Contains(item)) return SelectedSet.Files;
            return FindOwnerCollection(SelectedSet.Files, item);
        }

        private static ObservableCollection<DocumentItem> FindOwnerCollection(IEnumerable<DocumentItem> nodes, DocumentItem item)
        {
            var collection = nodes as ObservableCollection<DocumentItem>;
            if (collection != null && collection.Contains(item))
            {
                return collection;
            }

            foreach (var node in nodes)
            {
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

        private static bool IsBookmark(DocumentItem item) => item != null && item.Type != BookmarkType.Empty;

        private static bool CanHaveChildren(DocumentItem item) => item != null && item.NodeType == NodeType.Folder;

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
            if (node.NodeType == NodeType.Folder)
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
            var clipboardNodes = new List<ClipboardNode>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes ?? Enumerable.Empty<DocumentItem>())
            {
                AppendClipboardNode(node, string.Empty, clipboardNodes, usedIds);
            }

            return JsonConvert.SerializeObject(clipboardNodes, Formatting.Indented);
        }

        private static List<DocumentItem> DeserializeClipboardNodes(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<DocumentItem>();
            }

            var clipboardNodes = JsonConvert.DeserializeObject<List<ClipboardNode>>(json);
            return BuildClipboardTree(clipboardNodes);
        }

        private static void AppendClipboardNode(DocumentItem item, string parentId, ICollection<ClipboardNode> result, ISet<string> usedIds)
        {
            if (item == null)
            {
                return;
            }

            var id = MakeUniqueClipboardId(item.Name, usedIds);
            result.Add(new ClipboardNode
            {
                Id = id,
                ParentId = parentId ?? string.Empty,
                Name = item.Name ?? string.Empty,
                NodeType = item.NodeType,
                Type = item.Type,
                Symbol = item.Type == BookmarkType.Symbol ? item.Symbol ?? string.Empty : string.Empty,
                Project = item.Project ?? string.Empty,
                Path = item.Path ?? string.Empty,
                Line = item.Line,
                Column = item.Column,
                Comment = item.Comment ?? string.Empty
            });

            foreach (var child in item.Children ?? new ObservableCollection<DocumentItem>())
            {
                AppendClipboardNode(child, id, result, usedIds);
            }
        }

        private static List<DocumentItem> BuildClipboardTree(IEnumerable<ClipboardNode> flatNodes)
        {
            var roots = new List<DocumentItem>();
            if (flatNodes == null)
            {
                return roots;
            }

            var entries = flatNodes.Where(x => x != null).ToList();
            var map = entries.ToDictionary(x => x.Id ?? string.Empty, FromClipboardNode, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                var item = map[entry.Id ?? string.Empty];
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

        private static DocumentItem FromClipboardNode(ClipboardNode source)
        {
            return new DocumentItem
            {
                Name = source.Name ?? string.Empty,
                NodeType = source.NodeType,
                Type = source.Type,
                Symbol = source.Type == BookmarkType.Symbol ? source.Symbol ?? string.Empty : string.Empty,
                Project = source.Project ?? string.Empty,
                Path = source.Path ?? string.Empty,
                Line = source.Line < 1 ? 1 : source.Line,
                Column = source.Column < 1 ? 1 : source.Column,
                Comment = source.Comment ?? string.Empty,
                Children = new ObservableCollection<DocumentItem>()
            };
        }

        private static string MakeUniqueClipboardId(string name, ISet<string> usedIds)
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

        private static bool TryParseClipboardJson(string text, out List<DocumentItem> nodes)
        {
            nodes = new List<DocumentItem>();
            if (string.IsNullOrWhiteSpace(text)) return false;

            var trimmed = text.TrimStart();
            if (!trimmed.StartsWith("[", StringComparison.Ordinal) && !trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                nodes = DeserializeClipboardNodes(text);
                return true;
            }
            catch
            {
                nodes = new List<DocumentItem>();
                return false;
            }
        }

        private static List<DocumentItem> ParseClipboardText(string text)
        {
            var roots = new List<DocumentItem>();
            if (string.IsNullOrWhiteSpace(text)) return roots;

            var stack = new List<TextNodeFrame>();
            DocumentItem lastItem = null;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var rawLine in lines)
            {
                if (string.IsNullOrWhiteSpace(rawLine)) continue;

                var level = GetTextIndentLevel(rawLine);
                var content = rawLine.Trim();
                if (content.Length == 0) continue;

                if (content.StartsWith("#", StringComparison.Ordinal))
                {
                    if (lastItem != null)
                    {
                        var commentLine = content.Length == 1 ? string.Empty : content.Substring(1).TrimStart();
                        lastItem.Comment = string.IsNullOrEmpty(lastItem.Comment)
                            ? commentLine
                            : lastItem.Comment + Environment.NewLine + commentLine;
                    }
                    continue;
                }

                var item = ParseClipboardTextNode(content);
                if (item == null) continue;

                while (stack.Count > 0 && stack[stack.Count - 1].Level >= level)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                var parent = stack.Count == 0 ? null : stack[stack.Count - 1].Item;
                if (parent != null && parent.NodeType == NodeType.Folder)
                {
                    parent.Children.Add(item);
                }
                else
                {
                    roots.Add(item);
                }

                stack.Add(new TextNodeFrame(level, item));
                lastItem = item;
            }

            return roots;
        }

        private static int GetTextIndentLevel(string line)
        {
            var spaces = 0;
            foreach (var ch in line)
            {
                if (ch == ' ') spaces++;
                else if (ch == '\t') spaces += 2;
                else break;
            }

            return spaces / 2;
        }

        private static DocumentItem ParseClipboardTextNode(string content)
        {
            if (content.Length >= 2 && content[0] == '[' && content[content.Length - 1] == ']')
            {
                return new DocumentItem
                {
                    Name = content.Substring(1, content.Length - 2).Trim(),
                    NodeType = NodeType.Folder,
                    Line = 1,
                    Column = 1,
                    Children = new ObservableCollection<DocumentItem>()
                };
            }

            var item = new DocumentItem
            {
                Name = content,
                NodeType = NodeType.Item,
                Line = 1,
                Column = 1,
                Children = new ObservableCollection<DocumentItem>()
            };

            var separatorIndex = content.LastIndexOf(" — ", StringComparison.Ordinal);
            if (separatorIndex < 0)
            {
                return item;
            }

            item.Name = content.Substring(0, separatorIndex).Trim();
            var location = content.Substring(separatorIndex + 3).Trim();

            if (TryParseLocation(location, out var path, out var line, out var column))
            {
                item.Path = path;
                item.Line = line;
                item.Column = column;
            }

            return item;
        }

        private static bool TryParseLocation(string location, out string path, out int line, out int column)
        {
            path = string.Empty;
            line = 1;
            column = 1;

            if (string.IsNullOrWhiteSpace(location)) return false;

            var lastColon = location.LastIndexOf(':');
            if (lastColon <= 0 || lastColon == location.Length - 1) return false;

            var previousColon = location.LastIndexOf(':', lastColon - 1);
            if (previousColon <= 0) return false;

            if (!int.TryParse(location.Substring(previousColon + 1, lastColon - previousColon - 1), out line)) return false;
            if (!int.TryParse(location.Substring(lastColon + 1), out column)) return false;

            path = location.Substring(0, previousColon);
            if (line < 1) line = 1;
            if (column < 1) column = 1;
            return !string.IsNullOrWhiteSpace(path);
        }

        private sealed class TextNodeFrame
        {
            public TextNodeFrame(int level, DocumentItem item)
            {
                Level = level;
                Item = item;
            }

            public int Level { get; }
            public DocumentItem Item { get; }
        }

        private sealed class ClipboardNode
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
        }

        private static bool ClipboardContainsText()
        {
            try
            {
                return Clipboard.ContainsText() && !string.IsNullOrWhiteSpace(Clipboard.GetText());
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

            await fileTracking.UpdateTrackedPositionsAsync(EnumerateAllNodes());
            await store.SaveAsync(state);
        }

        private IEnumerable<DocumentItem> EnumerateAllNodes()
        {
            return state?.Sets == null
                ? Enumerable.Empty<DocumentItem>()
                : state.Sets.SelectMany(set => EnumerateNodes(set.Files));
        }

        private static IEnumerable<DocumentItem> EnumerateNodes(IEnumerable<DocumentItem> nodes)
        {
            if (nodes == null)
            {
                yield break;
            }

            foreach (var node in nodes)
            {
                if (node == null)
                {
                    continue;
                }

                yield return node;

                foreach (var child in EnumerateNodes(node.Children))
                {
                    yield return child;
                }
            }
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
