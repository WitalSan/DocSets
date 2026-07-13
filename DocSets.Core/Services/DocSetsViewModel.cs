using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
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
        private DocumentItem selectedSet;
        private DocumentItem selectedNode;
        private ObservableCollection<DocumentItem> selectedNodes = new ObservableCollection<DocumentItem>();
        private string storageText = "DocSets: загрузка...";
        private bool isLoaded;
        private bool isApplyingState;
        private bool isReloadingExternalChanges;
        private int activeSaveCount;
        private IReadOnlyList<WorkspaceInfo> workspaces = Array.Empty<WorkspaceInfo>();
        private WorkspaceInfo selectedWorkspace;
        private SolutionLocalState solutionState = new SolutionLocalState();
        private DocumentItem historyRoot;
        private DocumentItem pinRoot;
        private string lastHistoryKey = "";
        private string suppressedHistoryKey = "";
        private bool historyProbeInProgress;
        private const int UndoLimit = 100;
        private readonly Stack<UndoEntry> undoStack = new Stack<UndoEntry>();
        private readonly Stack<UndoEntry> redoStack = new Stack<UndoEntry>();
        private bool isRestoringUndoState;

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
            RegenerateAllIdsCommand = new RelayCommand(async () => await RegenerateAllIdsAsync(), () => IsLoaded);

            AddFolderCommand = new RelayCommand(p => AddFolder(p as DocumentItem ?? SelectedNode), p => IsLoaded && SelectedSet != null);
            AddRootFolderCommand = AddFolderCommand;
            AddChildFolderCommand = AddFolderCommand;
            AddBookmarkCommand = new RelayCommand(async p => await AddBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && SelectedSet != null);
            OpenBookmarkCommand = new RelayCommand(async p => await OpenBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && IsBookmark(p as DocumentItem ?? SelectedNode));
            UpdateBookmarkCommand = new RelayCommand(async p => await UpdateBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && IsBookmark(p as DocumentItem ?? SelectedNode));
            SyncWithCurrentPositionCommand = new RelayCommand(async p => await SyncWithCurrentPositionAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && (p as DocumentItem ?? SelectedNode) != null);
            RenameNodeCommand = new RelayCommand(p => RenameNode(p as DocumentItem ?? SelectedNode), p => IsLoaded && (p as DocumentItem ?? SelectedNode) != null);
            DeleteNodeCommand = new RelayCommand(p => DeleteNodes(p as DocumentItem ?? SelectedNode), p => IsLoaded && ((p as DocumentItem ?? SelectedNode) != null || SelectedNodes.Count > 0));
            MoveNodeUpCommand = new RelayCommand(() => MoveNode(-1), () => IsLoaded && CanMoveNode(SelectedNode, -1));
            MoveNodeDownCommand = new RelayCommand(() => MoveNode(1), () => IsLoaded && CanMoveNode(SelectedNode, 1));

            CopySelectedNodesCommand = new RelayCommand(_ => CopySelectedNodes(), _ => IsLoaded && SelectedNodes.Count > 0);
            PasteNodesCommand = new RelayCommand(p => PasteNodes(p as DocumentItem ?? SelectedNode), _ => IsLoaded && SelectedSet != null && ClipboardContainsText());
            CopySelectedNodesAsJsonCommand = new RelayCommand(_ => CopySelectedNodesAsJson(), _ => IsLoaded && SelectedNodes.Count > 0);
            TogglePinCommand = new RelayCommand(p => TogglePin(p as DocumentItem ?? SelectedNode), p => IsLoaded && CanTogglePin(p as DocumentItem ?? SelectedNode));
            UndoCommand = new RelayCommand(async () => await UndoAsync(), () => IsLoaded && undoStack.Count > 0);
            RedoCommand = new RelayCommand(async () => await RedoAsync(), () => IsLoaded && redoStack.Count > 0);
            PasteNodesFromJsonCommand = new RelayCommand(p => PasteNodesFromJson(p as DocumentItem ?? SelectedNode), _ => IsLoaded && SelectedSet != null && ClipboardContainsJsonText());
            state.Root.TreeChanged += Root_TreeChanged;
        }

        public event EventHandler<DocumentTreeChangedEventArgs> TreeChanged;

        public DocumentItem Root => state.Root;
        public ObservableCollection<DocumentItem> Sets => state.Root.Children;
        public DocumentItem HistoryRoot => historyRoot;
        public DocumentItem PinRoot => pinRoot;

        public IReadOnlyList<WorkspaceInfo> Workspaces => workspaces;

        public WorkspaceInfo SelectedWorkspace
        {
            get => selectedWorkspace;
            private set => SetProperty(ref selectedWorkspace, value);
        }

        public DocumentSetsUiSettings Ui => solutionState.Ui ?? (solutionState.Ui = new DocumentSetsUiSettings());

        public SolutionLocalState SolutionState => solutionState;

        public bool IsLoaded
        {
            get => isLoaded;
            private set => SetProperty(ref isLoaded, value);
        }

        public DocumentItem SelectedSet
        {
            get => selectedSet;
            set
            {
                if (!SetProperty(ref selectedSet, value)) return;

                // SelectedSet is the working root for commands and selection context.
                // It is not necessarily the active UI view: Full-Tree may remain active while
                // SelectedSet follows the folder containing the selected node. ActiveViewId is
                // therefore owned and persisted by the tabs UI.
                SetSelectedNodes(Enumerable.Empty<DocumentItem>());
                OnPropertyChanged(nameof(CurrentNodes));
                InvalidateCommands();
                if (IsLoaded && !isApplyingState)
                {
                    SaveSolutionState();
                }
            }
        }

        public ObservableCollection<DocumentItem> CurrentNodes => SelectedSet?.Children;

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
        public ICommand RegenerateAllIdsCommand { get; }
        public ICommand AddFolderCommand { get; }
        public ICommand AddRootFolderCommand { get; }
        public ICommand AddChildFolderCommand { get; }
        public ICommand AddBookmarkCommand { get; }
        public ICommand OpenBookmarkCommand { get; }
        public ICommand UpdateBookmarkCommand { get; }
        public ICommand SyncWithCurrentPositionCommand { get; }
        public ICommand RenameNodeCommand { get; }
        public ICommand DeleteNodeCommand { get; }
        public ICommand MoveNodeUpCommand { get; }
        public ICommand MoveNodeDownCommand { get; }
        public ICommand CopySelectedNodesCommand { get; }
        public ICommand PasteNodesCommand { get; }
        public ICommand CopySelectedNodesAsJsonCommand { get; }
        public ICommand PasteNodesFromJsonCommand { get; }
        public ICommand TogglePinCommand { get; }
        public ICommand UndoCommand { get; }
        public ICommand RedoCommand { get; }

        // Compatibility aliases for old command names.
        public ICommand RenameBookmarkCommand => RenameNodeCommand;
        public ICommand DeleteBookmarkCommand => DeleteNodeCommand;
        public ICommand MoveBookmarkUpCommand => MoveNodeUpCommand;
        public ICommand MoveBookmarkDownCommand => MoveNodeDownCommand;
        public ICommand CopySelectedBookmarksCommand => CopySelectedNodesCommand;

        public async Task LoadAsync()
        {
            await RefreshWorkspacesAsync();
            solutionState = store.LoadSolutionState() ?? new SolutionLocalState();
            var loadedState = await store.LoadAsync();
            ApplyLoadedState(loadedState, preferredSetName: null, selectedNodePath: null);
            ClearUndoHistory();
            await RefreshWorkspacesAsync();
        }

        public async Task SelectWorkspaceAsync(WorkspaceInfo workspace)
        {
            if (workspace == null || string.Equals(workspace.RelativePath, store.CurrentWorkspaceRelativePath, StringComparison.OrdinalIgnoreCase)) return;
            await SaveAsync();
            if (!await store.SelectWorkspaceAsync(workspace.RelativePath)) return;
            var loadedState = await store.LoadAsync() ?? new DocumentSetsState();
            ApplyLoadedState(loadedState, preferredSetName: null, selectedNodePath: null);
            ClearUndoHistory();
            await RefreshWorkspacesAsync();
        }

        private async Task RefreshWorkspacesAsync()
        {
            workspaces = await store.GetWorkspacesAsync();
            SelectedWorkspace = workspaces.FirstOrDefault(x => string.Equals(x.RelativePath, store.CurrentWorkspaceRelativePath, StringComparison.OrdinalIgnoreCase));
            if (SelectedWorkspace == null && !string.IsNullOrWhiteSpace(store.CurrentWorkspaceRelativePath))
            {
                var path = store.CurrentWorkspaceRelativePath;
                var fileName = System.IO.Path.GetFileName(path);
                var name = fileName.EndsWith(".docsets.json", StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(0, fileName.Length - ".docsets.json".Length)
                    : System.IO.Path.GetFileNameWithoutExtension(fileName);
                SelectedWorkspace = new WorkspaceInfo { Name = name, RelativePath = path, FullPath = store.StateFilePath };
                workspaces = workspaces.Concat(new[] { SelectedWorkspace }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            OnPropertyChanged(nameof(Workspaces));
        }

        public async Task<bool> ReloadIfWorkspaceChangedAsync()
        {
            if (!IsLoaded || isReloadingExternalChanges || Volatile.Read(ref activeSaveCount) > 0 ||
                !await store.HasExternalChangesAsync())
            {
                return false;
            }

            isReloadingExternalChanges = true;
            try
            {
                var preferredSetName = SelectedSet?.Name;
                var selectedNodePath = BuildNodeIndexPath(SelectedSet?.Children, SelectedNode);
                var loadedState = await store.LoadAsync();
                ApplyLoadedState(loadedState, preferredSetName, selectedNodePath);
                return loadedState != null;
            }
            finally
            {
                isReloadingExternalChanges = false;
            }
        }

        private void Root_TreeChanged(object sender, DocumentTreeChangedEventArgs e)
        {
            ApplyIdMigration(state.EnsureReadableIds());
            TreeChanged?.Invoke(this, e);
            if (IsLoaded && !isApplyingState && e != null
                && e.PropertyName != nameof(DocumentItem.IsExpanded)
                && e.PropertyName != nameof(DocumentItem.IsMultiSelected))
            {
                _ = SaveAsync();
            }
        }

        private void ApplyLoadedState(
            DocumentSetsState loadedState,
            string preferredSetName,
            IReadOnlyList<int> selectedNodePath)
        {
            if (loadedState == null)
            {
                IsLoaded = false;
                state.Root.TreeChanged -= Root_TreeChanged;
                state = new DocumentSetsState();
                state.Root.TreeChanged += Root_TreeChanged;
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

            state.Root.TreeChanged -= Root_TreeChanged;
            state = loadedState;
            state.Root.TreeChanged += Root_TreeChanged;
            if (state.Ui == null) state.Ui = new DocumentSetsUiSettings();
            if (solutionState.Ui == null || (solutionState.Ui.Columns.Count == 0 && state.Ui.Columns.Count > 0))
            {
                solutionState.Ui = state.Ui;
            }

            // Legacy NodeType.Set is normalized while DTO objects are created.
            // Root may contain both folders and leaf items; only folders are represented as tabs.
            NormalizeNodes(state.Sets);
            ApplyIdMigration(state.EnsureReadableIds());
            historyRoot = null;
            EnsureHistoryRoot();
            EnsurePinRoot();

            if (!state.Sets.Any(x => x != null && !x.IsLocalOnly))
            {
                state.Sets.Add(new DocumentItem { Name = "Default", NodeType = NodeType.Folder, Type = BookmarkType.Empty });
                state.ActiveSet = "Default";
            }

            isApplyingState = true;
            try
            {
                IsLoaded = true;
                OnPropertyChanged(nameof(Sets));
                OnPropertyChanged(nameof(Ui));

                var activeViewId = solutionState.ActiveViewId;
                SelectedSet = state.Sets.FirstOrDefault(x => x.NodeType == NodeType.Folder &&
                                  string.Equals(x.Id, activeViewId, StringComparison.OrdinalIgnoreCase))
                              ?? state.Sets.FirstOrDefault(x => x.NodeType == NodeType.Folder && !x.IsHistoryRoot &&
                                  string.Equals(x.Name, preferredSetName ?? state.ActiveSet, StringComparison.OrdinalIgnoreCase))
                              ?? state.Sets.FirstOrDefault(x => x.NodeType == NodeType.Folder && !x.IsHistoryRoot);

                var restoredNode = FindNodeByIndexPath(SelectedSet?.Children, selectedNodePath);
                SetSelectedNodes(restoredNode == null
                    ? Enumerable.Empty<DocumentItem>()
                    : new[] { restoredNode });

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

        private static IReadOnlyList<int> BuildNodeIndexPath(
            ObservableCollection<DocumentItem> roots,
            DocumentItem target)
        {
            if (roots == null || target == null)
            {
                return null;
            }

            var path = new List<int>();
            return TryBuildNodeIndexPath(roots, target, path) ? path : null;
        }

        private static bool TryBuildNodeIndexPath(
            ObservableCollection<DocumentItem> nodes,
            DocumentItem target,
            IList<int> path)
        {
            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                path.Add(index);

                if (ReferenceEquals(node, target))
                {
                    return true;
                }

                if (node?.Children != null && TryBuildNodeIndexPath(node.Children, target, path))
                {
                    return true;
                }

                path.RemoveAt(path.Count - 1);
            }

            return false;
        }

        private static DocumentItem FindNodeByIndexPath(
            ObservableCollection<DocumentItem> roots,
            IReadOnlyList<int> path)
        {
            if (roots == null || path == null || path.Count == 0)
            {
                return null;
            }

            ObservableCollection<DocumentItem> current = roots;
            DocumentItem node = null;

            for (var depth = 0; depth < path.Count; depth++)
            {
                var index = path[depth];
                if (current == null || index < 0 || index >= current.Count)
                {
                    return null;
                }

                node = current[index];
                if (depth < path.Count - 1)
                {
                    current = node?.Children;
                }
            }

            return node;
        }

        private void AddSet()
        {
            CaptureUndoState();
            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", "Название группы:");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (state.Sets.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Show("Группа с таким именем уже есть.");
                return;
            }

            var set = new DocumentItem { Name = name, NodeType = NodeType.Folder, Type = BookmarkType.Empty };
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

        public bool TryRenameSet(DocumentItem set, string name, bool showErrors)
        {
            CaptureUndoState("Переименование группы");
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
            CaptureUndoState();
            var set = SelectedSet;
            if (set == null) return;
            if (set.IsHistoryRoot)
            {
                if (MessageBox.Show(ownerAccessor(), "Очистить историю переходов?", "DocSets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                set.Children.Clear();
                SaveSolutionState();
                InvalidateCommands();
                return;
            }
            if (MessageBox.Show(ownerAccessor(), $"Удалить группу '{set.Name}'?", "DocSets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            state.Sets.Remove(set);
            if (!state.Sets.Any(x => x != null && x.NodeType == NodeType.Folder && !x.IsLocalOnly))
            {
                state.Sets.Add(new DocumentItem { Name = "Default", NodeType = NodeType.Folder, Type = BookmarkType.Empty });
            }

            SelectedSet = state.Sets.FirstOrDefault(x => x.NodeType == NodeType.Folder && !x.IsHistoryRoot);
            state.ActiveSet = SelectedSet?.Name ?? "";
            _ = SaveAsync();
            InvalidateCommands();
        }
        private void AddFolder(DocumentItem parent)
        {
            CaptureUndoState();
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
                set.Children.Add(folder);
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

        public DocumentItem GetSetContainingNode(DocumentItem item)
        {
            if (item == null)
            {
                return null;
            }

            if (item.IsRootChild && item.NodeType == NodeType.Folder) return item;
            return state.Sets.FirstOrDefault(set => ContainsNode(set.Children, item));
        }

        public DocumentItem GetParentFolder(DocumentItem item)
        {
            if (item == null)
            {
                return null;
            }

            if (item.Parent != null && !ReferenceEquals(item.Parent, state.Root)) return item.Parent;

            foreach (var set in state.Sets)
            {
                var parent = FindParentFolder(set.Children, item);
                if (parent != null)
                {
                    return parent;
                }
            }

            return null;
        }

        public async Task MoveExistingNodeAsync(DocumentItem item, DocumentItem destinationSet, DocumentItem destinationParent)
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
                if (destinationParent.NodeType != NodeType.Folder || !ContainsNode(targetSet.Children, destinationParent))
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
                : targetSet.Children;

            // При редактировании свойств тот же экземпляр item не должен быть добавлен
            // в дерево повторно. Если место назначения изменилось, удаляем все
            // вхождения этой ссылки из всех групп и добавляем ровно один раз.
            var currentOwner = currentSet == null ? null : FindOwnerCollection(currentSet.Children, item);
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

        public async Task AddPreparedBookmarkAsync(DocumentItem bookmark, DocumentItem destinationSet, DocumentItem target)
        {
            var set = destinationSet ?? SelectedSet;
            if (set == null || bookmark == null)
            {
                return;
            }

            bookmark.NodeType = NodeType.Item;
            bookmark.Children.Clear();

            if (target != null && !ContainsNode(set.Children, target))
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

            if (target != null && !ContainsNode(set.Children, target))
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
            CaptureUndoState();
            var bookmark = await CreateBookmarkFromActiveDocumentAsync(showErrors: true);
            if (bookmark == null)
            {
                return;
            }

            await AddPreparedBookmarkAsync(bookmark, target);
        }

        private async Task OpenBookmarkAsync(DocumentItem item)
        {
            item = ResolvePin(item);
            if (!IsBookmark(item)) return;
            if (item.IsHistoryItem)
                suppressedHistoryKey = GetHistoryKey(item);
            await store.OpenBookmarkAsync(item);
            if (item.Type == BookmarkType.File)
            {
                await fileTracking.TrackAfterOpenAsync(item);
            }
        }

        public async Task SyncWithCurrentPositionAsync(DocumentItem item)
        {
            item = ResolvePin(item);
            if (item == null)
            {
                return;
            }

            var updated = await store.CreateBookmarkFromActiveDocumentAsync();
            if (updated == null)
            {
                Show("Не найден активный документ редактора.");
                return;
            }

            var targetType = item.Type;
            if (targetType == BookmarkType.Empty)
            {
                targetType = string.IsNullOrWhiteSpace(updated.Symbol)
                    ? BookmarkType.File
                    : BookmarkType.Symbol;
            }

            if (targetType == BookmarkType.Symbol && string.IsNullOrWhiteSpace(updated.Symbol))
            {
                Show("Не удалось определить символ в текущей позиции редактора.");
                return;
            }

            item.Type = targetType;
            item.Path = updated.Path;
            item.Line = updated.Line;
            item.Column = updated.Column;
            item.EditorState = updated.EditorState?.Clone();

            if (targetType == BookmarkType.File)
            {
                item.Symbol = string.Empty;
                item.Project = string.Empty;
                await fileTracking.TrackFromActiveDocumentAsync(item);
            }
            else
            {
                item.Symbol = updated.Symbol;
                item.Project = updated.Project;
            }

            SelectedNode = item;
            SetSelectedNodes(new[] { item });
            await SaveAsync();
            OnPropertyChanged(nameof(CurrentNodes));
            InvalidateCommands();
        }

        private async Task UpdateBookmarkAsync(DocumentItem item)
        {
            CaptureUndoState();
            item = ResolvePin(item);
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
                item.EditorState = updated.EditorState?.Clone();
            }
            else
            {
                item.Name = updated.Name;
                item.Symbol = updated.Symbol;
                item.Project = updated.Project;
                item.Path = updated.Path;
                item.Line = updated.Line;
                item.Column = updated.Column;
                item.EditorState = updated.EditorState?.Clone();
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
            CaptureUndoState("Переименование элемента");
            item = ResolvePin(item);
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
            CaptureUndoState();
            if (SelectedSet == null) return;

            var nodes = GetEffectiveNodes(item).ToList();
            if (nodes.Count == 0) return;

            var text = nodes.Count == 1
                ? (nodes[0].NodeType == NodeType.Folder ? $"Удалить папку '{nodes[0].Name}' и все вложенные элементы?" : $"Удалить закладку '{nodes[0].Name}'?")
                : $"Удалить выбранные элементы ({nodes.Count})?";

            if (MessageBox.Show(ownerAccessor(), text, "DocSets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            var historyChanged = false;
            var pinChanged = false;
            foreach (var node in FilterOutDescendants(nodes))
            {
                if (node.IsHistoryRoot || node.IsPinRoot)
                {
                    node.Children.Clear();
                    historyChanged |= node.IsHistoryRoot;
                    pinChanged |= node.IsPinRoot;
                    continue;
                }

                if (node.IsPinItem)
                {
                    pinChanged = true;
                    FindOwnerCollection(node)?.Remove(node);
                    continue;
                }

                var deletedIds = new HashSet<string>((new[] { node }).Concat(EnumerateNodes(node.Children)).Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
                foreach (var pin in pinRoot?.Children.Where(x => deletedIds.Contains(x.TargetId)).ToList() ?? new List<DocumentItem>())
                {
                    pinRoot.Children.Remove(pin);
                    pinChanged = true;
                }

                historyChanged |= node.IsHistoryItem || ReferenceEquals(node.Parent, historyRoot);
                FindOwnerCollection(node)?.Remove(node);
            }

            SetSelectedNodes(Enumerable.Empty<DocumentItem>());
            if (historyChanged || pinChanged) SaveSolutionState();
            _ = SaveAsync();
            InvalidateCommands();
        }

        private void MoveSet(int delta)
        {
            CaptureUndoState();
            Move(state.Sets, SelectedSet, delta);
            state.ActiveSet = SelectedSet?.Name ?? "";
            _ = SaveAsync();
        }

        public void MoveSetRelative(DocumentItem source, DocumentItem target, bool after)
        {
            CaptureUndoState();
            if (source == null || target == null || ReferenceEquals(source, target))
                return;

            var sourceIndex = state.Sets.IndexOf(source);
            var targetIndex = state.Sets.IndexOf(target);
            if (sourceIndex < 0 || targetIndex < 0)
                return;

            state.Sets.RemoveAt(sourceIndex);
            targetIndex = state.Sets.IndexOf(target);
            var insertIndex = after ? targetIndex + 1 : targetIndex;
            if (insertIndex < 0) insertIndex = 0;
            if (insertIndex > state.Sets.Count) insertIndex = state.Sets.Count;
            state.Sets.Insert(insertIndex, source);
            state.ActiveSet = SelectedSet?.Name ?? "";
            OnPropertyChanged(nameof(Sets));
            _ = SaveAsync();
            InvalidateCommands();
        }

        private void MoveNode(int delta)
        {
            CaptureUndoState();
            var collection = FindOwnerCollection(SelectedNode);
            Move(collection, SelectedNode, delta);
            _ = SaveAsync();
        }

        private void CopySelectedNodes()
        {
            var selected = FilterOutDescendants(SelectedNodes).ToList();
            if (selected.Count == 0) return;

            var nodes = selected.Select(CloneResolved).Where(x => x != null).ToList();
            var json = SerializeClipboardNodes(nodes);

            var data = new DataObject();
            data.SetText(BuildText(nodes));
            Clipboard.SetDataObject(data, true);
        }

        private void CopySelectedNodesAsJson()
        {
            var selected = FilterOutDescendants(SelectedNodes).ToList();
            if (selected.Count == 0) return;

            var nodes = selected.Select(CloneResolved).Where(x => x != null).ToList();
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
            CaptureUndoState();
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


        public bool CanCopySelectedNodesTo(DocumentItem target, DropPosition position, bool fullTree = false)
        {
            return GetCopyPlan(target, position, fullTree) != null;
        }

        public async Task CopySelectedNodesToAsync(DocumentItem target, DropPosition position, bool fullTree = false)
        {
            CaptureUndoState();
            var plan = GetCopyPlan(target, position, fullTree);
            if (plan == null) return;

            var sourceNodes = FilterOutDescendants(SelectedNodes).ToList();
            if (sourceNodes.Count == 0) return;

            var copies = sourceNodes.Select(CloneResolved).Where(x => x != null).ToList();
            var insertIndex = Math.Max(0, Math.Min(plan.Index, plan.Collection.Count));
            foreach (var copy in copies)
            {
                plan.Collection.Insert(insertIndex++, copy);
            }

            state.EnsureReadableIds();

            if (target?.NodeType == NodeType.Folder && position == DropPosition.Inside)
            {
                target.IsExpanded = true;
            }

            SetSelectedNodes(copies);
            await SaveAsync();
            InvalidateCommands();
        }

        public bool CanMoveSelectedNodesTo(DocumentItem target, DropPosition position, bool fullTree = false)
        {
            return GetMovePlan(target, position, fullTree) != null;
        }

        public async Task MoveSelectedNodesToAsync(DocumentItem target, DropPosition position, bool fullTree = false)
        {
            CaptureUndoState();
            var plan = GetMovePlan(target, position, fullTree);
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

        private MovePlan GetCopyPlan(DocumentItem target, DropPosition position, bool fullTree)
        {
            if ((!fullTree && SelectedSet == null) || SelectedNodes.Count == 0) return null;

            var nodes = FilterOutDescendants(SelectedNodes).ToList();
            if (nodes.Count == 0) return null;

            if (target == null)
            {
                var rootCollection = fullTree ? state.Root.Children : SelectedSet.Children;
                return new MovePlan(rootCollection, rootCollection.Count);
            }

            if (position == DropPosition.Inside && target.NodeType == NodeType.Folder)
            {
                return new MovePlan(target.Children, target.Children.Count);
            }

            var owner = FindOwnerCollection(target);
            if (owner == null) return null;

            var targetIndex = owner.IndexOf(target);
            if (targetIndex < 0) return null;

            var index = position == DropPosition.After ? targetIndex + 1 : targetIndex;
            return new MovePlan(owner, index);
        }

        private MovePlan GetMovePlan(DocumentItem target, DropPosition position, bool fullTree)
        {
            if ((!fullTree && SelectedSet == null) || SelectedNodes.Count == 0) return null;

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
                var rootCollection = fullTree ? state.Root.Children : SelectedSet.Children;
                return new MovePlan(rootCollection, rootCollection.Count);
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

        private static ObservableCollection<DocumentItem> GetInsertCollection(DocumentItem set, DocumentItem target)
        {
            if (target?.NodeType == NodeType.Folder)
            {
                return target.Children;
            }

            return set?.Children ?? new ObservableCollection<DocumentItem>();
        }

        private void RemoveNodeReferenceFromAllSets(DocumentItem item)
        {
            if (item == null || state?.Sets == null)
            {
                return;
            }

            foreach (var set in state.Sets)
            {
                RemoveNodeReference(set.Children, item);
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
            return item?.Parent?.Children;
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
            return SelectedSet?.Children == null ? Enumerable.Empty<DocumentItem>() : Flatten(SelectedSet.Children, includeCollapsed: true);
        }

        private IEnumerable<DocumentItem> GetVisibleNodes()
        {
            return SelectedSet?.Children == null ? Enumerable.Empty<DocumentItem>() : Flatten(SelectedSet.Children, includeCollapsed: false);
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

        private bool IsBookmark(DocumentItem item)
        {
            item = ResolvePin(item);
            return item != null && item.Type != BookmarkType.Empty && item.Type != BookmarkType.Pin;
        }

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
                Comment = item.Comment ?? string.Empty,
                Color = item.Color,
                EditorState = item.EditorState?.Clone()
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
                Color = source.Color,
                EditorState = source.EditorState?.Clone(),
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

            [JsonProperty("color", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
            public BookmarkColor Color { get; set; }

            [JsonProperty("editorState", NullValueHandling = NullValueHandling.Ignore)]
            public EditorState EditorState { get; set; }
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

        private async Task RegenerateAllIdsAsync()
        {
            CaptureUndoState();
            if (!IsLoaded) return;

            var idMap = state.RegenerateAllReadableIds();
            ApplyIdMigration(idMap);

            await SaveAsync();
            SaveSolutionState();
            InvalidateCommands();
        }

        public void SaveSolutionState()
        {
            ApplyIdMigration(state?.EnsureReadableIds());
            SaveHistoryToSolutionState();
            SavePinsToSolutionState();
            solutionState.Workspace = store.CurrentWorkspaceRelativePath ?? "";
            store.SaveSolutionState(solutionState);
        }

        public async Task TrackNavigationHistoryAsync()
        {
            if (!IsLoaded || historyProbeInProgress) return;
            historyProbeInProgress = true;
            try
            {
                var item = await store.CreateBookmarkFromActiveDocumentAsync();
                if (item == null || string.IsNullOrWhiteSpace(item.Path))
                {
                    lastHistoryKey = "";
                    return;
                }

                var key = GetHistoryKey(item);
                if (string.IsNullOrWhiteSpace(key) ||
                    string.Equals(lastHistoryKey, key, StringComparison.OrdinalIgnoreCase))
                    return;

                lastHistoryKey = key;

                // Переход, инициированный из History, открывает цель, но не создаёт
                // новую запись истории. Подавление действует только для этого перехода.
                if (string.Equals(suppressedHistoryKey, key, StringComparison.OrdinalIgnoreCase))
                {
                    suppressedHistoryKey = "";
                    return;
                }

                suppressedHistoryKey = "";
                AddOrRefreshHistoryItem(item);
                SaveSolutionState();
            }
            finally
            {
                historyProbeInProgress = false;
            }
        }

        private void EnsureHistoryRoot()
        {
            if (historyRoot != null && ReferenceEquals(historyRoot.Parent, state.Root)) return;
            historyRoot = state.Sets.FirstOrDefault(x => x != null && x.IsHistoryRoot);
            if (historyRoot == null)
            {
                historyRoot = new DocumentItem
                {
                    Id = "history",
                    Name = "History",
                    NodeType = NodeType.Folder,
                    Type = BookmarkType.Empty,
                    IsLocalOnly = true,
                    IsHistoryRoot = true
                };
                state.Sets.Insert(0, historyRoot);
            }

            historyRoot.Children.Clear();
            foreach (var saved in solutionState.History ?? new List<NavigationHistoryLocalItem>())
            {
                historyRoot.Children.Add(new DocumentItem
                {
                    Id = saved.Id ?? "",
                    Name = saved.Name ?? "",
                    NodeType = NodeType.Item,
                    Type = saved.Type,
                    Symbol = saved.Symbol ?? "",
                    Project = saved.Project ?? "",
                    Path = saved.Path ?? "",
                    Line = saved.Line,
                    Column = saved.Column,
                    Comment = saved.Comment ?? "",
                    EditorState = saved.EditorState?.Clone(),
                    IsLocalOnly = true,
                    IsHistoryItem = true
                });
            }
        }

        private void AddOrRefreshHistoryItem(DocumentItem source)
        {
            EnsureHistoryRoot();
            var key = GetHistoryKey(source);
            var existing = historyRoot.Children.FirstOrDefault(x => string.Equals(GetHistoryKey(x), key, StringComparison.OrdinalIgnoreCase));
            var now = DateTime.Now;
            var item = existing ?? new DocumentItem
            {
                Id = CreateHistoryReadableId(source.Name),
                NodeType = NodeType.Item,
                IsLocalOnly = true,
                IsHistoryItem = true
            };

            item.Name = source.Name;
            item.Type = string.IsNullOrWhiteSpace(source.Symbol) ? BookmarkType.File : BookmarkType.Symbol;
            item.Symbol = source.Symbol;
            item.Project = source.Project;
            item.Path = source.Path;
            item.Line = source.Line;
            item.Column = source.Column;
            item.Comment = now.ToString("yyyy-MM-dd HH:mm:ss");
            item.EditorState = source.EditorState?.Clone();

            if (existing != null) historyRoot.Children.Remove(existing);
            historyRoot.Children.Insert(0, item);
            while (historyRoot.Children.Count > 2000)
            {
                var removable = historyRoot.Children.LastOrDefault(x => !IsPinned(x));
                if (removable == null) break;
                historyRoot.Children.Remove(removable);
            }
        }

        private string CreateHistoryReadableId(string name)
        {
            var builder = new System.Text.StringBuilder();
            var separatorPending = false;
            foreach (var c in (name ?? "").Trim().ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c))
                {
                    if (separatorPending && builder.Length > 0) builder.Append('-');
                    builder.Append(c);
                    separatorPending = false;
                }
                else separatorPending = true;
            }
            var baseId = "history-" + (builder.Length == 0 ? "item" : builder.ToString().Trim('-'));
            var used = new HashSet<string>(EnumerateAllNodes().Where(x => x != null).Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
            var id = baseId;
            var index = 2;
            while (!used.Add(id)) id = baseId + "-" + index++;
            return id;
        }

        private void SaveHistoryToSolutionState()
        {
            if (solutionState == null) return;
            solutionState.History = historyRoot?.Children.Select(x => new NavigationHistoryLocalItem
            {
                Id = x.Id,
                Name = x.Name,
                Type = x.Type,
                Symbol = x.Symbol,
                Project = x.Project,
                Path = x.Path,
                Line = x.Line,
                Column = x.Column,
                Comment = x.Comment,
                VisitedAt = DateTime.TryParse(x.Comment, out var visited) ? visited : DateTime.Now,
                EditorState = x.EditorState?.Clone()
            }).ToList() ?? new List<NavigationHistoryLocalItem>();
        }

        private static string GetHistoryKey(DocumentItem item)
        {
            if (item == null) return "";
            if (!string.IsNullOrWhiteSpace(item.Symbol))
                return (item.Project ?? "") + "|" + item.Symbol;
            return item.Path ?? "";
        }

        private void EnsurePinRoot()
        {
            if (pinRoot != null && ReferenceEquals(pinRoot.Parent, state.Root)) return;
            pinRoot = state.Sets.FirstOrDefault(x => x != null && x.IsPinRoot);
            if (pinRoot == null)
            {
                pinRoot = new DocumentItem
                {
                    Id = "pin",
                    Name = "Pin",
                    NodeType = NodeType.Folder,
                    Type = BookmarkType.Empty,
                    IsLocalOnly = true,
                    IsPinRoot = true
                };
                var historyIndex = historyRoot == null ? 0 : state.Sets.IndexOf(historyRoot) + 1;
                state.Sets.Insert(Math.Max(0, historyIndex), pinRoot);
            }

            pinRoot.Children.Clear();
            foreach (var saved in solutionState.Pins ?? new List<PinLocalItem>())
            {
                pinRoot.Children.Add(new DocumentItem
                {
                    Id = saved.Id ?? "",
                    NodeType = NodeType.Item,
                    Type = BookmarkType.Pin,
                    TargetId = saved.TargetId ?? "",
                    IsLocalOnly = true,
                    IsPinItem = true
                });
            }
        }

        public DocumentItem ResolvePin(DocumentItem item)
        {
            if (item == null || !item.IsPinItem) return item;
            return EnumerateAllNodes().FirstOrDefault(x => !x.IsPinItem && string.Equals(x.Id, item.TargetId, StringComparison.OrdinalIgnoreCase));
        }

        private DocumentItem CloneResolved(DocumentItem item)
        {
            return ResolvePin(item)?.Clone();
        }

        public bool IsPinned(DocumentItem item)
        {
            return item != null && pinRoot != null && pinRoot.Children.Any(x => string.Equals(x.TargetId, item.Id, StringComparison.OrdinalIgnoreCase));
        }

        private bool CanTogglePin(DocumentItem item)
        {
            if (item == null || item.IsPinRoot || item.IsHistoryRoot) return false;
            if (item.IsPinItem) return true;
            return !string.IsNullOrWhiteSpace(item.Id);
        }

        private void TogglePin(DocumentItem item)
        {
            CaptureUndoState();
            EnsurePinRoot();
            if (item == null) return;
            if (item.IsPinItem)
            {
                pinRoot.Children.Remove(item);
            }
            else
            {
                var existing = pinRoot.Children.FirstOrDefault(x => string.Equals(x.TargetId, item.Id, StringComparison.OrdinalIgnoreCase));
                if (existing != null) pinRoot.Children.Remove(existing);
                else pinRoot.Children.Add(new DocumentItem
                {
                    Id = "pin-" + Guid.NewGuid().ToString("N"),
                    NodeType = NodeType.Item,
                    Type = BookmarkType.Pin,
                    TargetId = item.Id,
                    IsLocalOnly = true,
                    IsPinItem = true
                });
            }
            SaveSolutionState();
            OnPropertyChanged(nameof(CurrentNodes));
        }

        private void SavePinsToSolutionState()
        {
            solutionState.Pins = pinRoot?.Children.Select(x => new PinLocalItem { Id = x.Id, TargetId = x.TargetId }).ToList() ?? new List<PinLocalItem>();
        }

        private void ApplyIdMigration(IDictionary<string, string> idMap)
        {
            if (idMap == null || idMap.Count == 0 || solutionState == null) return;

            string Map(string id)
            {
                if (string.IsNullOrWhiteSpace(id)) return id;
                return idMap.TryGetValue(id, out var mapped) ? mapped : id;
            }

            solutionState.ActiveViewId = Map(solutionState.ActiveViewId);
            foreach (var pin in solutionState.Pins ?? new List<PinLocalItem>())
            {
                pin.TargetId = Map(pin.TargetId);
            }
            if (pinRoot != null)
            {
                foreach (var pin in pinRoot.Children)
                {
                    pin.TargetId = Map(pin.TargetId);
                }
            }

            var oldViews = solutionState.Views ?? new Dictionary<string, TreeViewLocalState>(StringComparer.OrdinalIgnoreCase);
            var newViews = new Dictionary<string, TreeViewLocalState>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in oldViews)
            {
                var key = Map(pair.Key);
                var view = pair.Value ?? new TreeViewLocalState();
                view.CollapsedIds = (view.CollapsedIds ?? new List<string>()).Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                view.SelectedIds = (view.SelectedIds ?? new List<string>()).Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (view.LegacyExpandedIds != null)
                    view.LegacyExpandedIds = view.LegacyExpandedIds.Select(Map).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                newViews[key] = view;
            }
            solutionState.Views = newViews;
        }

        public IReadOnlyList<string> UndoOperations => undoStack.Select(x => x.Description).ToArray();
        public IReadOnlyList<string> RedoOperations => redoStack.Select(x => x.Description).ToArray();

        public void CaptureUndoState(string description = "Изменение")
        {
            if (!IsLoaded || isApplyingState || isRestoringUndoState) return;

            var snapshot = CreateUndoSnapshot();
            if (undoStack.Count > 0 && string.Equals(undoStack.Peek().Snapshot, snapshot, StringComparison.Ordinal)) return;

            undoStack.Push(new UndoEntry(description, snapshot));
            while (undoStack.Count > UndoLimit)
            {
                var kept = undoStack.Reverse().Take(UndoLimit).Reverse().ToArray();
                undoStack.Clear();
                foreach (var item in kept) undoStack.Push(item);
            }
            redoStack.Clear();
            InvalidateCommands();
        }

        public async Task UndoManyAsync(int count)
        {
            for (var i = 0; i < count && undoStack.Count > 0; i++)
                await UndoAsync();
        }

        public async Task RedoManyAsync(int count)
        {
            for (var i = 0; i < count && redoStack.Count > 0; i++)
                await RedoAsync();
        }

        private async Task UndoAsync()
        {
            if (undoStack.Count == 0) return;
            var target = undoStack.Pop();
            redoStack.Push(new UndoEntry(target.Description, CreateUndoSnapshot()));
            await RestoreUndoSnapshotAsync(target.Snapshot);
        }

        private async Task RedoAsync()
        {
            if (redoStack.Count == 0) return;
            var target = redoStack.Pop();
            undoStack.Push(new UndoEntry(target.Description, CreateUndoSnapshot()));
            await RestoreUndoSnapshotAsync(target.Snapshot);
        }

        private string CreateUndoSnapshot()
        {
            SavePinsToSolutionState();
            var envelope = new UndoSnapshot
            {
                State = JsonConvert.SerializeObject(state),
                Pins = JsonConvert.SerializeObject(solutionState.Pins ?? new List<PinLocalItem>())
            };
            return JsonConvert.SerializeObject(envelope);
        }

        private async Task RestoreUndoSnapshotAsync(string snapshot)
        {
            if (string.IsNullOrWhiteSpace(snapshot)) return;
            var envelope = JsonConvert.DeserializeObject<UndoSnapshot>(snapshot);
            var restoredState = JsonConvert.DeserializeObject<DocumentSetsState>(envelope?.State ?? "");
            if (restoredState == null) return;

            isRestoringUndoState = true;
            try
            {
                solutionState.Pins = JsonConvert.DeserializeObject<List<PinLocalItem>>(envelope.Pins ?? "[]") ?? new List<PinLocalItem>();
                ApplyLoadedState(restoredState, preferredSetName: null, selectedNodePath: null);
                SaveSolutionState();
                await SaveAsync();
            }
            finally
            {
                isRestoringUndoState = false;
                InvalidateCommands();
            }
        }

        private void ClearUndoHistory()
        {
            undoStack.Clear();
            redoStack.Clear();
            InvalidateCommands();
        }

        private sealed class UndoEntry
        {
            public UndoEntry(string description, string snapshot)
            {
                Description = string.IsNullOrWhiteSpace(description) ? "Изменение" : description;
                Snapshot = snapshot;
            }

            public string Description { get; }
            public string Snapshot { get; }
        }

        private sealed class UndoSnapshot
        {
            public string State { get; set; }
            public string Pins { get; set; }
        }

        public async Task SaveAsync()
        {
            if (!IsLoaded)
            {
                return;
            }

            Interlocked.Increment(ref activeSaveCount);
            try
            {
                await fileTracking.UpdateTrackedPositionsAsync(EnumerateAllNodes());
                await store.SaveAsync(state);
            }
            finally
            {
                Interlocked.Decrement(ref activeSaveCount);
            }
        }

        private IEnumerable<DocumentItem> EnumerateAllNodes()
        {
            return state?.Sets == null
                ? Enumerable.Empty<DocumentItem>()
                : state.Sets.SelectMany(set => EnumerateNodes(set.Children));
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
