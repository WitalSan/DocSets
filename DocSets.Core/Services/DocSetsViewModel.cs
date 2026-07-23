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
        private readonly DocumentTreeService treeService;
        private readonly NavigationHistoryService historyService;
        private readonly RecentBookmarksService recentBookmarksService;
        private readonly PinService pinService;
        private readonly UndoRedoService undoRedoService;
        private readonly TagService tagService;
        private readonly SourceReferenceService sourceReferenceService = new SourceReferenceService();
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
        private bool historyProbeInProgress;
        private bool isRestoringUndoState;
        private bool suppressUndoRedoNotification;
        private bool suppressUndoRedoPersistence;
        private long undoRedoSnapshotMilliseconds;
        private long undoRedoDeserializeMilliseconds;
        private long undoRedoApplyMilliseconds;
        private bool refreshingRecent;
        private int mutationDepth;
        private bool mutationChanged;
        private bool mutationSaveRequested;
        private readonly SemaphoreSlim mutationGate = new SemaphoreSlim(1, 1);
        private readonly AsyncLocal<int> mutationNesting = new AsyncLocal<int>();

        public DocSetsViewModel(AsyncPackage package, Func<Window> ownerAccessor)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            this.ownerAccessor = ownerAccessor ?? (() => null);
            store = new DocSetsStore(package);
            fileTracking = new FileBookmarkTrackingService(package, store.ToFullPath);
            treeService = new DocumentTreeService();
            historyService = new NavigationHistoryService();
            recentBookmarksService = new RecentBookmarksService();
            pinService = new PinService();
            undoRedoService = new UndoRedoService();
            tagService = new TagService();

            AddSetCommand = new RelayCommand(AddSet, () => IsLoaded);
            RenameSetCommand = new RelayCommand(RenameSet, () => IsLoaded && SelectedSet != null);
            DeleteSetCommand = new RelayCommand(DeleteSet, () => IsLoaded && SelectedSet != null);
            MoveSetUpCommand = new RelayCommand(() => MoveSet(-1), () => IsLoaded && treeService.CanMove(Sets, SelectedSet, -1));
            MoveSetDownCommand = new RelayCommand(() => MoveSet(1), () => IsLoaded && treeService.CanMove(Sets, SelectedSet, 1));
            RegenerateAllIdsCommand = new RelayCommand(async () => await RegenerateAllIdsAsync(), () => IsLoaded);

            AddFolderCommand = new RelayCommand(p => AddFolder(p as DocumentItem ?? SelectedNode), p => IsLoaded && CanModifySet(SelectedSet));
            AddRootFolderCommand = AddFolderCommand;
            AddChildFolderCommand = AddFolderCommand;
            AddBookmarkCommand = new RelayCommand(async p => await AddBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && CanModifySet(SelectedSet));
            OpenBookmarkCommand = new RelayCommand(async p => await OpenBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && IsBookmark(p as DocumentItem ?? SelectedNode));
            UpdateBookmarkCommand = new RelayCommand(async p => await UpdateBookmarkAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && IsBookmark(p as DocumentItem ?? SelectedNode));
            SyncWithCurrentPositionCommand = new RelayCommand(async p => await SyncWithCurrentPositionAsync(p as DocumentItem ?? SelectedNode), p => IsLoaded && (p as DocumentItem ?? SelectedNode) != null);
            RenameNodeCommand = new RelayCommand(p => RenameNode(p as DocumentItem ?? SelectedNode), p => IsLoaded && (p as DocumentItem ?? SelectedNode) != null);
            DeleteNodeCommand = new RelayCommand(p => DeleteNodes(p as DocumentItem ?? SelectedNode), p => IsLoaded && ((p as DocumentItem ?? SelectedNode) != null || SelectedNodes.Count > 0));
            MoveNodeUpCommand = new RelayCommand(() => MoveNode(-1), () => IsLoaded && CanMoveNode(SelectedNode, -1));
            MoveNodeDownCommand = new RelayCommand(() => MoveNode(1), () => IsLoaded && CanMoveNode(SelectedNode, 1));

            CopySelectedNodesCommand = new RelayCommand(_ => CopySelectedNodes(), _ => IsLoaded && SelectedNodes.Count > 0);
            PasteNodesCommand = new RelayCommand(p => PasteNodes(p as DocumentItem ?? SelectedNode), _ => IsLoaded && CanModifySet(SelectedSet) && ClipboardContainsText());
            CopySelectedNodesAsJsonCommand = new RelayCommand(_ => CopySelectedNodesAsJson(), _ => IsLoaded && SelectedNodes.Count > 0);
            TogglePinCommand = new RelayCommand(p => TogglePin(p as DocumentItem ?? SelectedNode), p => IsLoaded && CanTogglePin(p as DocumentItem ?? SelectedNode));
            UndoCommand = new RelayCommand(async () => await UndoAsync(), () => IsLoaded && undoRedoService.CanUndo);
            RedoCommand = new RelayCommand(async () => await RedoAsync(), () => IsLoaded && undoRedoService.CanRedo);
            PasteNodesFromJsonCommand = new RelayCommand(p => PasteNodesFromJson(p as DocumentItem ?? SelectedNode), _ => IsLoaded && CanModifySet(SelectedSet) && ClipboardContainsJsonText());
            state.Root.TreeChanged += Root_TreeChanged;
        }

        public event EventHandler<DocumentTreeChangedEventArgs> TreeChanged;

        public DocumentItem Root => state.Root;
        public ObservableCollection<DocumentItem> Sets => state.Root.Children;
        public DocumentItem HistoryRoot => historyService.Root;
        public DocumentItem RecentRoot => recentBookmarksService.Root;
        public DocumentItem PinRoot => pinService.Root;
        public string CurrentSolutionName => store.CurrentSolutionName;
        public IReadOnlyList<TagDefinition> Tags => state.Tags;

        public IReadOnlyList<WorkspaceInfo> Workspaces => workspaces;
        public string SolutionDirectory => store.SolutionDirectory;
        public string AssetDirectory => store.AssetDirectory;

        public Task<string> SaveImageAssetAsync(byte[] content, string mimeType, string originalName)
            => store.SaveImageAssetAsync(content, mimeType, originalName);

        public Task<string> NormalizeCommentAssetsAsync(string markdown,
            CancellationToken cancellationToken = default)
            => store.NormalizeCommentAssetsAsync(markdown, cancellationToken);

        public WorkspaceInfo SelectedWorkspace
        {
            get => selectedWorkspace;
            private set => SetProperty(ref selectedWorkspace, value);
        }

        public DocumentSetsUiSettings Ui => solutionState.Ui ?? (solutionState.Ui = new DocumentSetsUiSettings());

        public SolutionLocalState SolutionState => solutionState;

        public ContentFormat NewNoteContentFormat
        {
            get => solutionState.NewNoteContentFormat == ContentFormat.Html
                ? ContentFormat.Html
                : ContentFormat.Markdown;
            set
            {
                var normalized = value == ContentFormat.Html ? ContentFormat.Html : ContentFormat.Markdown;
                if (solutionState.NewNoteContentFormat == normalized) return;
                solutionState.NewNoteContentFormat = normalized;
                SaveSolutionState();
                OnPropertyChanged();
            }
        }

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

        public async Task<bool> OpenDocSetAsync(string directoryPath)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return false;
            if (IsLoaded) await SaveAsync();
            if (!await store.OpenDocSetAsync(directoryPath)) return false;
            ApplyLoadedState(await store.LoadAsync(), preferredSetName: null, selectedNodePath: null);
            ClearUndoHistory();
            await RefreshWorkspacesAsync();
            return true;
        }

        public async Task<bool> CreateDocSetAsync(string directoryPath, string name)
        {
            if (string.IsNullOrWhiteSpace(directoryPath)) return false;
            if (IsLoaded) await SaveAsync();
            if (!await store.CreateDocSetAsync(directoryPath, name)) return false;
            ApplyLoadedState(await store.LoadAsync(), preferredSetName: null, selectedNodePath: null);
            ClearUndoHistory();
            await RefreshWorkspacesAsync();
            return true;
        }

        private async Task RefreshWorkspacesAsync()
        {
            workspaces = await store.GetWorkspacesAsync();
            SelectedWorkspace = workspaces.FirstOrDefault(x => string.Equals(x.RelativePath, store.CurrentWorkspaceRelativePath, StringComparison.OrdinalIgnoreCase));
            if (SelectedWorkspace == null && !string.IsNullOrWhiteSpace(store.CurrentWorkspaceRelativePath))
            {
                var path = store.CurrentWorkspaceRelativePath;
                var fileName = System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                var name = fileName.EndsWith(DirectoryDocSetStore.DirectorySuffix, StringComparison.OrdinalIgnoreCase)
                    ? fileName.Substring(0, fileName.Length - DirectoryDocSetStore.DirectorySuffix.Length)
                    : fileName;
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
                var loadedState = await store.LoadAsync(forceReload: true);
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
            if (refreshingRecent) return;
            ApplyIdMigration(state.EnsureReadableIds());
            if (ShouldRefreshRecent(e))
            {
                refreshingRecent = true;
                try
                {
                    recentBookmarksService.Refresh();
                }
                finally
                {
                    refreshingRecent = false;
                }
            }
            TreeChanged?.Invoke(this, e);
            if (IsLoaded && !isApplyingState && e != null
                && e.PropertyName != nameof(DocumentItem.IsExpanded)
                && e.PropertyName != nameof(DocumentItem.IsMultiSelected))
            {
                if (e.Item?.IsLocalOnly == true)
                {
                    return;
                }

                // Обновление отслеживаемых позиций выполняется как часть текущего
                // сохранения. Его события уже попадут в записываемый снимок и не
                // должны рекурсивно запускать следующую запись.
                if (Volatile.Read(ref activeSaveCount) > 0)
                {
                    return;
                }

                if (mutationDepth > 0)
                {
                    mutationChanged = true;
                    return;
                }

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
                tagService.EnsureStandardTags(state);
                state.Root.TreeChanged += Root_TreeChanged;
                selectedSet = null;
                selectedNode = null;
                SetSelectedNodes(Enumerable.Empty<DocumentItem>());
                OnPropertyChanged(nameof(Sets));
                OnPropertyChanged(nameof(Ui));
                OnPropertyChanged(nameof(SelectedSet));
                OnPropertyChanged(nameof(SelectedNode));
                OnPropertyChanged(nameof(CurrentNodes));
                StorageText = string.IsNullOrWhiteSpace(store.SolutionFilePath)
                    ? "DocSets: solution ещё не открыт"
                    : "DocSets: откройте или создайте DocSet";
                InvalidateCommands();
                return;
            }

            state.Root.TreeChanged -= Root_TreeChanged;
            state = loadedState;
            tagService.EnsureStandardTags(state);
            if (state.Ui == null) state.Ui = new DocumentSetsUiSettings();
            if (solutionState.Ui == null || (solutionState.Ui.Columns.Count == 0 && state.Ui.Columns.Count > 0))
            {
                solutionState.Ui = state.Ui;
            }

            // Legacy NodeType.Set is normalized while DTO objects are created.
            // Root may contain both folders and leaf items; only folders are represented as tabs.
            treeService.NormalizeNodes(state.Sets);
            ApplyIdMigration(state.EnsureReadableIds());
            historyService.Attach(state, solutionState.History);
            recentBookmarksService.Attach(state, historyService.Root);
            pinService.Attach(state, solutionState.Pins, recentBookmarksService.Root);

            if (!state.Sets.Any(x => x != null && !x.IsLocalOnly))
            {
                state.Sets.Add(new DocumentItem { Name = "Default", NodeType = NodeType.Folder, Type = BookmarkType.Empty });
                state.ActiveSet = "Default";
            }

            // Loading and Undo restore rebuild several derived/local branches. Subscribing
            // before that work makes every intermediate change run the full TreeChanged
            // pipeline (ID validation, recent-bookmark refresh and persistence checks).
            state.Root.TreeChanged += Root_TreeChanged;

            isApplyingState = true;
            try
            {
                IsLoaded = true;
                OnPropertyChanged(nameof(Sets));
                OnPropertyChanged(nameof(Ui));

                var activeViewId = solutionState.ActiveViewId;
                SelectedSet = state.Sets.FirstOrDefault(x => x.NodeType == NodeType.Folder &&
                                  string.Equals(x.Id, activeViewId, StringComparison.OrdinalIgnoreCase))
                              ?? state.Sets.FirstOrDefault(x => x.NodeType == NodeType.Folder && !x.IsHistoryRoot && !x.IsRecentRoot && !x.IsPinRoot &&
                                  string.Equals(x.Name, preferredSetName ?? state.ActiveSet, StringComparison.OrdinalIgnoreCase))
                              ?? state.Sets.FirstOrDefault(x => x.NodeType == NodeType.Folder && !x.IsHistoryRoot && !x.IsRecentRoot && !x.IsPinRoot);

                var restoredNode = FindNodeByIndexPath(SelectedSet?.Children, selectedNodePath);
                SetSelectedNodes(restoredNode == null
                    ? Enumerable.Empty<DocumentItem>()
                    : new[] { restoredNode });

                StorageText = string.IsNullOrWhiteSpace(store.StateFilePath)
                    ? "DocSets: откройте solution (.sln)"
                    : store.IsSharedWorkspace
                        ? $"DocSet (внешний): {store.StateFilePath}"
                        : $"DocSet: {store.StateFilePath}";
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
            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", "Название группы:");
            if (string.IsNullOrWhiteSpace(name)) return;
            name = name.Trim();
            if (state.Sets.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                Show("Группа с таким именем уже есть.");
                return;
            }

            _ = ExecuteMutationAsync(nameof(AddSet), () =>
            {
                var set = new DocumentItem { Name = name, NodeType = NodeType.Folder, Type = BookmarkType.Empty };
                state.Sets.Add(set);
                SelectedSet = set;
                InvalidateCommands();
            });
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
            if (set == null || set.IsLocalOnly) return false;
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

            _ = ExecuteMutationAsync(nameof(TryRenameSet), () =>
            {
                set.Name = name;
                state.ActiveSet = name;
                OnPropertyChanged(nameof(Sets));
                OnPropertyChanged(nameof(SelectedSet));
                InvalidateCommands();
            });
            return true;
        }

        private void DeleteSet()
        {
            var set = SelectedSet;
            if (set == null) return;
            if (set.IsRecentRoot)
            {
                Show("Недавние закладки формируются автоматически.");
                return;
            }
            if (set.IsHistoryRoot || set.IsPinRoot)
            {
                if (MessageBox.Show(ownerAccessor(), "Очистить историю переходов?", "DocSets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                set.Children.Clear();
                SaveSolutionState();
                InvalidateCommands();
                return;
            }
            if (MessageBox.Show(ownerAccessor(), $"Удалить группу '{set.Name}'?", "DocSets", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            _ = ExecuteMutationAsync(nameof(DeleteSet), () =>
            {
            state.Sets.Remove(set);
            if (!state.Sets.Any(x => x != null && x.NodeType == NodeType.Folder && !x.IsLocalOnly))
            {
                state.Sets.Add(new DocumentItem { Name = "Default", NodeType = NodeType.Folder, Type = BookmarkType.Empty });
            }

            SelectedSet = state.Sets.FirstOrDefault(x => x.NodeType == NodeType.Folder && !x.IsHistoryRoot && !x.IsRecentRoot && !x.IsPinRoot);
            state.ActiveSet = SelectedSet?.Name ?? "";
            });
            InvalidateCommands();
        }
        private void AddFolder(DocumentItem parent)
        {
            var set = SelectedSet;
            if (set == null) return;

            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", "Название папки:", "Новая папка");
            if (string.IsNullOrWhiteSpace(name)) return;

            _ = ExecuteMutationAsync(nameof(AddFolder), () =>
            {
            var folder = new DocumentItem
            {
                Name = name.Trim(),
                NodeType = NodeType.Folder,
                Type = BookmarkType.Empty,
                ContentFormat = NewNoteContentFormat,
                IsExpanded = true
            };
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
            });
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
            bookmark.ContentFormat = NewNoteContentFormat;
            return bookmark;
        }

        public Task AddPreparedBookmarkAsync(DocumentItem bookmark, DocumentItem target)
        {
            return AddPreparedBookmarkAsync(bookmark, SelectedSet, target);
        }

        public DocumentItem GetSetContainingNode(DocumentItem item)
        {
            return treeService.GetSetContainingNode(state, item);
        }

        public DocumentItem GetParentFolder(DocumentItem item)
        {
            return treeService.GetParentFolder(state, item);
        }

        public async Task MoveExistingNodeAsync(DocumentItem item, DocumentItem destinationSet, DocumentItem destinationParent)
        {
            if (item == null)
            {
                return;
            }

            var currentSet = GetSetContainingNode(item);
            var targetSet = destinationSet ?? currentSet ?? SelectedSet;
            if (!CanModifySet(targetSet))
            {
                return;
            }

            if (destinationParent != null)
            {
                if (destinationParent.NodeType != NodeType.Folder || !treeService.ContainsNode(targetSet.Children, destinationParent))
                {
                    destinationParent = null;
                }

                if (ReferenceEquals(destinationParent, item) || treeService.IsDescendantOf(destinationParent, item))
                {
                    destinationParent = null;
                }
            }

            var targetCollection = destinationParent?.NodeType == NodeType.Folder
                ? destinationParent.Children
                : targetSet.Children;

            await ExecuteMutationAsync(nameof(MoveExistingNodeAsync), () =>
            {
            // При редактировании свойств тот же экземпляр item не должен быть добавлен
            // в дерево повторно. Если место назначения изменилось, удаляем все
            // вхождения этой ссылки из всех групп и добавляем ровно один раз.
            var currentOwner = currentSet == null ? null : treeService.FindOwnerCollection(currentSet.Children, item);
            var destinationChanged = !ReferenceEquals(currentOwner, targetCollection);
            var alreadyInTarget = treeService.ContainsReference(targetCollection, item);

            if (destinationChanged || !alreadyInTarget)
            {
                treeService.RemoveNodeReferenceFromAllSets(state, item);
                if (!treeService.ContainsReference(targetCollection, item))
                {
                    targetCollection.Add(item);
                }
                if (IsStoredBookmark(item)) StampModified(item, ensureCreated: true);
            }

            if (destinationParent?.NodeType == NodeType.Folder)
            {
                destinationParent.IsExpanded = true;
            }

            SelectedSet = targetSet;
            SelectedNode = item;
            SetSelectedNodes(new[] { item });
            });
            OnPropertyChanged(nameof(CurrentNodes));
            InvalidateCommands();
        }

        public async Task AddPreparedBookmarkAsync(DocumentItem bookmark, DocumentItem destinationSet, DocumentItem target)
        {
            var set = destinationSet ?? SelectedSet;
            if (!CanModifySet(set) || bookmark == null)
            {
                return;
            }

            bookmark.NodeType = NodeType.Item;
            bookmark.Children.Clear();
            StampCreated(bookmark);

            if (target != null && !treeService.ContainsNode(set.Children, target))
            {
                target = null;
            }

            await ExecuteMutationAsync(nameof(AddPreparedBookmarkAsync), async () =>
            {
            var collection = treeService.GetInsertCollection(set, target);
            collection.Add(bookmark);
            if (target?.NodeType == NodeType.Folder) target.IsExpanded = true;

            SelectedSet = set;
            SelectedNode = bookmark;
            SetSelectedNodes(new[] { bookmark });
            if (bookmark.Type == BookmarkType.File)
            {
                await fileTracking.TrackFromActiveDocumentAsync(bookmark);
            }
            });
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

            folder.ContentFormat = NewNoteContentFormat;

            if (target != null && !treeService.ContainsNode(set.Children, target))
            {
                target = null;
            }

            await ExecuteMutationAsync(nameof(CreateFolderFromActiveDocumentAsync), () =>
            {
            var collection = treeService.GetInsertCollection(set, target);
            collection.Add(folder);
            if (target?.NodeType == NodeType.Folder) target.IsExpanded = true;

            SelectedNode = folder;
            SetSelectedNodes(new[] { folder });
            });
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
            item = ResolvePin(item);
            if (!IsBookmark(item)) return;
            if (item.IsHistoryItem)
                historyService.SuppressNext(item);
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

            await ExecuteMutationAsync(nameof(SyncWithCurrentPositionAsync), async () =>
            {
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

            StampModified(item, ensureCreated: true);

            SelectedNode = item;
            SetSelectedNodes(new[] { item });
            });
            OnPropertyChanged(nameof(CurrentNodes));
            InvalidateCommands();
        }

        private async Task UpdateBookmarkAsync(DocumentItem item)
        {
            item = ResolvePin(item);
            if (!IsBookmark(item)) return;
            var updated = await store.CreateBookmarkFromActiveDocumentAsync();
            if (updated == null)
            {
                Show("Не найден активный документ редактора.");
                return;
            }

            await ExecuteMutationAsync(nameof(UpdateBookmarkAsync), async () =>
            {
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
            StampModified(item, ensureCreated: true);
            });
        }

        private void RenameNode(DocumentItem item)
        {
            item = ResolvePin(item);
            if (item == null) return;
            var caption = item.NodeType == NodeType.Folder ? "Новое название папки:" : "Новое название закладки:";
            var name = PromptDialog.Ask(ownerAccessor(), "DocSets", caption, item.Name);
            if (string.IsNullOrWhiteSpace(name)) return;

            _ = ExecuteMutationAsync(nameof(RenameNode), () =>
            {
                item.Name = name.Trim();
                if (IsStoredBookmark(item)) StampModified(item, ensureCreated: true);
                SelectedNode = item;
            });
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

            _ = ExecuteMutationAsync(nameof(DeleteNodes), () =>
            {
            var historyChanged = false;
            var pinChanged = false;
            foreach (var node in treeService.FilterOutDescendants(nodes))
            {
                if (node.IsRecentRoot || node.IsRecentItem)
                {
                    continue;
                }
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
                    treeService.FindOwnerCollection(node)?.Remove(node);
                    continue;
                }

                var deletedIds = new HashSet<string>((new[] { node }).Concat(EnumerateNodes(node.Children)).Select(x => x.Id).Where(x => !string.IsNullOrWhiteSpace(x)), StringComparer.OrdinalIgnoreCase);
                var pinCount = PinRoot?.Children.Count ?? 0;
                pinService.RemoveTargets(deletedIds);
                pinChanged |= (PinRoot?.Children.Count ?? 0) != pinCount;

                historyChanged |= node.IsHistoryItem || ReferenceEquals(node.Parent, HistoryRoot);
                treeService.FindOwnerCollection(node)?.Remove(node);
            }

            SetSelectedNodes(Enumerable.Empty<DocumentItem>());
            if (historyChanged || pinChanged) SaveSolutionState();
            });
            InvalidateCommands();
        }

        private void MoveSet(int delta)
        {
            _ = ExecuteMutationAsync(nameof(MoveSet), () =>
            {
            treeService.Move(state.Sets, SelectedSet, delta);
            state.ActiveSet = SelectedSet?.Name ?? "";
            });
        }

        public void MoveSetRelative(DocumentItem source, DocumentItem target, bool after)
        {
            if (source == null || target == null || ReferenceEquals(source, target))
                return;

            _ = ExecuteMutationAsync(nameof(MoveSetRelative), () =>
            {
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
            });
            InvalidateCommands();
        }

        private void MoveNode(int delta)
        {
            _ = ExecuteMutationAsync(nameof(MoveNode), () =>
            {
            var collection = treeService.FindOwnerCollection(SelectedNode);
            treeService.Move(collection, SelectedNode, delta);
            if (IsStoredBookmark(SelectedNode)) StampModified(SelectedNode, ensureCreated: true);
            });
        }

        private void CopySelectedNodes()
        {
            var selected = treeService.FilterOutDescendants(SelectedNodes).ToList();
            if (selected.Count == 0) return;

            var nodes = selected.Select(CloneResolved).Where(x => x != null).ToList();
            var json = SerializeClipboardNodes(nodes);

            var data = new DataObject();
            data.SetText(BuildText(nodes));
            Clipboard.SetDataObject(data, true);
        }

        private void CopySelectedNodesAsJson()
        {
            var selected = treeService.FilterOutDescendants(SelectedNodes).ToList();
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
            if (!CanModifySet(SelectedSet) || nodes == null || nodes.Count == 0) return;

            _ = ExecuteMutationAsync(nameof(PasteNodes), () =>
            {
            treeService.NormalizeNodes(nodes);
            var collection = treeService.GetInsertCollection(SelectedSet, target);

            DocumentItem last = null;
            foreach (var node in nodes.Select(x => x.Clone()))
            {
                StampCreatedTree(node);
                collection.Add(node);
                last = node;
            }

            if (target?.NodeType == NodeType.Folder) target.IsExpanded = true;
            if (last != null) SelectedNode = last;
            });
            InvalidateCommands();
        }


        public bool CanCopySelectedNodesTo(DocumentItem target, DropPosition position, bool fullTree = false)
        {
            var targetSet = GetSetContainingNode(target) ?? (target?.IsRootChild == true ? target : SelectedSet);
            if (!CanModifySet(targetSet)) return false;
            return treeService.CreateCopyPlan(state, SelectedSet, SelectedNodes, target, position, fullTree) != null;
        }

        public async Task CopySelectedNodesToAsync(DocumentItem target, DropPosition position, bool fullTree = false)
        {
            var plan = treeService.CreateCopyPlan(state, SelectedSet, SelectedNodes, target, position, fullTree);
            if (plan == null) return;

            var sourceNodes = treeService.FilterOutDescendants(SelectedNodes).ToList();
            if (sourceNodes.Count == 0) return;

            await ExecuteMutationAsync(nameof(CopySelectedNodesToAsync), () =>
            {
            var copies = sourceNodes.Select(CloneResolved).Where(x => x != null).ToList();
            foreach (var copy in copies) StampCreatedTree(copy);
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
            });
            InvalidateCommands();
        }

        public bool CanMoveSelectedNodesTo(DocumentItem target, DropPosition position, bool fullTree = false)
        {
            var targetSet = GetSetContainingNode(target) ?? (target?.IsRootChild == true ? target : SelectedSet);
            if (!CanModifySet(targetSet)) return false;
            return treeService.CreateMovePlan(state, SelectedSet, SelectedNodes, target, position, fullTree) != null;
        }

        public async Task MoveSelectedNodesToAsync(DocumentItem target, DropPosition position, bool fullTree = false)
        {
            var plan = treeService.CreateMovePlan(state, SelectedSet, SelectedNodes, target, position, fullTree);
            if (plan == null) return;

            var nodes = treeService.FilterOutDescendants(SelectedNodes).ToList();
            if (nodes.Count == 0) return;

            await ExecuteMutationAsync(nameof(MoveSelectedNodesToAsync), () =>
            {
            foreach (var node in nodes)
            {
                treeService.FindOwnerCollection(node)?.Remove(node);
            }

            var insertIndex = Math.Max(0, Math.Min(plan.Index, plan.Collection.Count));
            foreach (var node in nodes)
            {
                plan.Collection.Insert(insertIndex++, node);
                if (IsStoredBookmark(node)) StampModified(node, ensureCreated: true);
            }

            if (target?.NodeType == NodeType.Folder && position == DropPosition.Inside)
            {
                target.IsExpanded = true;
            }

            SetSelectedNodes(nodes);
            });
            InvalidateCommands();
        }

        private IEnumerable<DocumentItem> GetEffectiveNodes(DocumentItem item)
        {
            if (SelectedNodes.Count > 0 && (item == null || SelectedNodes.Contains(item)))
            {
                return SelectedNodes;
            }

            return item == null ? Enumerable.Empty<DocumentItem>() : new[] { item };
        }

        private IEnumerable<DocumentItem> GetAllNodes()
        {
            return SelectedSet?.Children == null
                ? Enumerable.Empty<DocumentItem>()
                : treeService.Flatten(SelectedSet.Children, includeCollapsed: true);
        }

        private IEnumerable<DocumentItem> GetVisibleNodes()
        {
            return SelectedSet?.Children == null
                ? Enumerable.Empty<DocumentItem>()
                : treeService.Flatten(SelectedSet.Children, includeCollapsed: false);
        }

        private bool CanMoveNode(DocumentItem item, int delta)
        {
            var collection = treeService.FindOwnerCollection(item);
            return treeService.CanMove(collection, item, delta);
        }

        private bool IsBookmark(DocumentItem item)
        {
            item = ResolvePin(item);
            return item != null && item.Type != BookmarkType.Empty && item.Type != BookmarkType.Pin;
        }

        private static bool CanHaveChildren(DocumentItem item) => item != null && item.NodeType == NodeType.Folder;

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

            if (!string.IsNullOrWhiteSpace(node.Content))
            {
                var commentLines = node.Content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (var commentLine in commentLines)
                {
                    lines.Add($"{indent}  # {commentLine}");
                }
            }
        }

        private string SerializeClipboardNodes(IEnumerable<DocumentItem> nodes)
        {
            var clipboardNodes = new List<ClipboardNode>();
            var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in nodes ?? Enumerable.Empty<DocumentItem>())
            {
                AppendClipboardNode(node, string.Empty, clipboardNodes, usedIds);
            }

            var usedTagIds = new HashSet<string>(clipboardNodes.SelectMany(x => x.TagIds ?? new List<string>()), StringComparer.OrdinalIgnoreCase);
            var assetReferences = clipboardNodes.SelectMany(node => store.FindAssetReferences(node.Content))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var envelope = new ClipboardEnvelope
            {
                Items = clipboardNodes,
                TagDefinitions = state.Tags.Where(x => x != null && usedTagIds.Contains(x.Id)).Select(x => x.Clone()).ToList(),
                SourceContext = store.CurrentSourceContext,
                Assets = assetReferences.Select(reference => new ClipboardAsset
                {
                    Reference = reference,
                    MimeType = store.GetAssetMimeType(reference),
                    Content = store.ReadAsset(reference)
                }).ToList()
            };
            return JsonConvert.SerializeObject(envelope, Formatting.Indented);
        }

        private List<DocumentItem> DeserializeClipboardNodes(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<DocumentItem>();
            var trimmed = json.TrimStart();
            if (trimmed.StartsWith("[", StringComparison.Ordinal))
                return BuildClipboardTree(JsonConvert.DeserializeObject<List<ClipboardNode>>(json));
            var envelope = JsonConvert.DeserializeObject<ClipboardEnvelope>(json) ?? new ClipboardEnvelope();
            tagService.MergeDefinitions(state, envelope.TagDefinitions);
            foreach (var asset in envelope.Assets ?? new List<ClipboardAsset>())
            {
                if (asset?.Content == null || asset.Content.Length == 0) continue;
                store.SaveImageAssetAsync(asset.Content, asset.MimeType,
                    System.IO.Path.GetFileName(asset.Reference)).GetAwaiter().GetResult();
            }
            if (envelope.SourceContext != null)
                RebaseClipboardNodes(envelope.Items, envelope.SourceContext, store.CurrentSourceContext);
            return BuildClipboardTree(envelope.Items);
        }

        private void RebaseClipboardNodes(IEnumerable<ClipboardNode> nodes,
            SourceReferenceContext sourceContext, SourceReferenceContext targetContext)
        {
            foreach (var node in nodes ?? Enumerable.Empty<ClipboardNode>())
            {
                var sourceId = node.SourceId ?? "";
                var path = node.Path ?? "";
                sourceReferenceService.Rebase(sourceContext, targetContext, ref sourceId, ref path);
                node.SourceId = sourceId;
                node.Path = path;
                node.Content = sourceReferenceService.RebaseMarkdownFileLinks(
                    node.Content, sourceContext, targetContext);
            }
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
                SourceId = item.SourceId ?? string.Empty,
                Symbol = item.Type == BookmarkType.Symbol ? item.Symbol ?? string.Empty : string.Empty,
                Project = item.Project ?? string.Empty,
                Path = item.Path ?? string.Empty,
                Line = item.Line,
                Column = item.Column,
                Content = item.Content ?? string.Empty,
                ContentFormat = item.ContentFormat,
                Color = item.Color,
                TagIds = item.TagIds?.ToList() ?? new List<string>(),
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
                SourceId = source.SourceId ?? string.Empty,
                Symbol = source.Type == BookmarkType.Symbol ? source.Symbol ?? string.Empty : string.Empty,
                Project = source.Project ?? string.Empty,
                Path = source.Path ?? string.Empty,
                Line = source.Line < 1 ? 1 : source.Line,
                Column = source.Column < 1 ? 1 : source.Column,
                Content = source.Content ?? string.Empty,
                ContentFormat = source.ContentFormat,
                Color = source.Color,
                TagIds = source.TagIds?.ToList() ?? new List<string>(),
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

        private bool TryParseClipboardJson(string text, out List<DocumentItem> nodes)
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
                        lastItem.Content = string.IsNullOrEmpty(lastItem.Content)
                            ? commentLine
                            : lastItem.Content + Environment.NewLine + commentLine;
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

        private sealed class ClipboardEnvelope
        {
            [JsonProperty("items")]
            public List<ClipboardNode> Items { get; set; } = new List<ClipboardNode>();
            [JsonProperty("tagDefinitions", NullValueHandling = NullValueHandling.Ignore)]
            public List<TagDefinition> TagDefinitions { get; set; } = new List<TagDefinition>();
            [JsonProperty("sourceContext", NullValueHandling = NullValueHandling.Ignore)]
            public SourceReferenceContext SourceContext { get; set; }
            [JsonProperty("assets", NullValueHandling = NullValueHandling.Ignore)]
            public List<ClipboardAsset> Assets { get; set; } = new List<ClipboardAsset>();
        }

        private sealed class ClipboardAsset
        {
            [JsonProperty("reference")]
            public string Reference { get; set; }
            [JsonProperty("mimeType")]
            public string MimeType { get; set; }
            [JsonProperty("content")]
            public byte[] Content { get; set; }
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

            [JsonProperty("sourceId", NullValueHandling = NullValueHandling.Ignore)]
            public string SourceId { get; set; }

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

            [JsonProperty("content")]
            public string Content { get; set; }

            [JsonProperty("contentFormat")]
            [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
            public ContentFormat ContentFormat { get; set; } = ContentFormat.Markdown;

            [JsonProperty("color", DefaultValueHandling = DefaultValueHandling.Ignore)]
            [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
            public BookmarkColor Color { get; set; }

            [JsonProperty("tagIds", NullValueHandling = NullValueHandling.Ignore)]
            public List<string> TagIds { get; set; } = new List<string>();

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

        private async Task RegenerateAllIdsAsync()
        {
            if (!IsLoaded) return;

            await ExecuteMutationAsync(nameof(RegenerateAllIdsAsync), () =>
            {
            var idMap = state.RegenerateAllReadableIds();
            ApplyIdMigration(idMap);

            _ = SaveAsync();
            SaveSolutionState();
            });
            InvalidateCommands();
        }

        public void SaveSolutionState()
        {
            ApplyIdMigration(state?.EnsureReadableIds());
            solutionState.History = historyService.Export();
            solutionState.Pins = pinService.Export();
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
                    historyService.ResetCurrentLocation();
                    return;
                }

                if (historyService.Record(item, pinService.IsPinned, DateTime.Now))
                {
                    SaveSolutionState();
                }
            }
            finally
            {
                historyProbeInProgress = false;
            }
        }

        public DocumentItem ResolvePin(DocumentItem item)
        {
            return recentBookmarksService.Resolve(pinService.Resolve(item));
        }

        public void MarkBookmarkModified(DocumentItem item)
        {
            item = ResolvePin(item);
            if (!IsStoredBookmark(item)) return;
            StampModified(item, ensureCreated: true);
        }

        /// <summary>
        /// Изменяет формат только пустой заметки. Операция проходит через общий механизм
        /// мутаций, поэтому создаёт один Undo и выполняет одно сохранение документа.
        /// </summary>
        public Task ChangeEmptyContentFormatAsync(DocumentItem item, ContentFormat format)
        {
            item = ResolvePin(item);
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (format != ContentFormat.Markdown && format != ContentFormat.Html)
                throw new ArgumentOutOfRangeException(nameof(format));
            if (item.ContentFormat == format) return Task.CompletedTask;
            if (!string.IsNullOrWhiteSpace(item.Content))
                throw new InvalidOperationException("Формат можно изменить только у пустой заметки.");

            return ExecuteMutationAsync("Изменение формата пустой заметки", () =>
            {
                item.ContentFormat = format;
                MarkBookmarkModified(item);
            });
        }

        private bool IsStoredBookmark(DocumentItem item)
        {
            return item != null && !item.IsLocalOnly && item.NodeType == NodeType.Item &&
                   (item.Type == BookmarkType.Symbol || item.Type == BookmarkType.File);
        }

        private static bool CanModifySet(DocumentItem set)
        {
            return set != null && !set.IsLocalOnly;
        }

        private void StampCreated(DocumentItem item)
        {
            if (item == null || item.NodeType != NodeType.Item ||
                (item.Type != BookmarkType.Symbol && item.Type != BookmarkType.File)) return;

            var now = DateTimeOffset.UtcNow;
            item.CreatedAtUtc = now;
            item.ModifiedAtUtc = now;
            item.ModifiedInSolution = store.CurrentSolutionName;
        }

        private void StampCreatedTree(DocumentItem item)
        {
            if (item == null) return;
            StampCreated(item);
            foreach (var child in item.Children ?? new ObservableCollection<DocumentItem>()) StampCreatedTree(child);
        }

        private void StampModified(DocumentItem item, bool ensureCreated)
        {
            if (item == null) return;
            var now = DateTimeOffset.UtcNow;
            if (ensureCreated && !item.CreatedAtUtc.HasValue) item.CreatedAtUtc = now;
            item.ModifiedAtUtc = now;
            item.ModifiedInSolution = store.CurrentSolutionName;
        }

        private static bool ShouldRefreshRecent(DocumentTreeChangedEventArgs e)
        {
            if (e == null || e.Item?.IsLocalOnly == true) return false;
            if (e.Kind == DocumentTreeChangeKind.Added || e.Kind == DocumentTreeChangeKind.Removed || e.Kind == DocumentTreeChangeKind.Reset)
                return true;
            if (e.Kind != DocumentTreeChangeKind.PropertyChanged) return false;

            return e.PropertyName == nameof(DocumentItem.Name) ||
                   e.PropertyName == nameof(DocumentItem.NodeType) ||
                   e.PropertyName == nameof(DocumentItem.Type) ||
                   e.PropertyName == nameof(DocumentItem.CreatedAtUtc) ||
                   e.PropertyName == nameof(DocumentItem.ModifiedAtUtc) ||
                   e.PropertyName == nameof(DocumentItem.ModifiedInSolution);
        }

        private DocumentItem CloneResolved(DocumentItem item)
        {
            return ResolvePin(item)?.Clone();
        }

        public bool IsPinned(DocumentItem item)
        {
            return pinService.IsPinned(item);
        }

        public bool CanSetPinned(DocumentItem item)
        {
            return pinService.CanToggle(ResolvePin(item));
        }

        public Task<string> GetLivePreviewAsync(DocumentItem item, CancellationToken cancellationToken)
        {
            return store.GetLivePreviewAsync(ResolvePin(item), cancellationToken);
        }

        public Task<bool> OpenSymbolAsync(DocumentItem item, string symbol)
        {
            item = ResolvePin(item);
            return store.OpenSymbolAsync(symbol, item?.Project);
        }

        public Task<ActiveSymbolReference> GetActiveSymbolReferenceAsync(string draggedText)
        {
            return store.GetActiveSymbolReferenceAsync(draggedText);
        }

        public Task<bool> OpenSymbolAsync(DocumentItem item, string symbol, string project)
        {
            item = ResolvePin(item);
            return store.OpenSymbolAsync(symbol, string.IsNullOrWhiteSpace(project) ? item?.Project : project);
        }

        public async Task<bool> OpenBookmarkByIdAsync(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            var target = treeService.Flatten(state.Root.Children, includeCollapsed: true)
                .FirstOrDefault(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase));
            if (target == null) return false;
            await OpenBookmarkAsync(target);
            return true;
        }

        public async Task<bool> OpenFileLinkAsync(string path, string sourceId = "")
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            await store.OpenBookmarkAsync(new DocumentItem
            {
                Type = BookmarkType.File, NodeType = NodeType.Item, SourceId = sourceId ?? "",
                Path = path, Line = 1, Column = 1
            });
            return true;
        }

        public async Task SetColorAsync(IEnumerable<DocumentItem> items, BookmarkColor color)
        {
            var targets = (items ?? Enumerable.Empty<DocumentItem>())
                .Select(ResolvePin)
                .Where(item => item != null)
                .Distinct()
                .Where(item => item.Color != color)
                .ToList();
            if (targets.Count == 0) return;

            await ExecuteMutationAsync(nameof(SetColorAsync), () =>
            {
                foreach (var target in targets)
                {
                    target.Color = color;
                    if (IsStoredBookmark(target)) StampModified(target, ensureCreated: true);
                }
            });
            InvalidateCommands();
        }

        public async Task SetPinnedAsync(IEnumerable<DocumentItem> items, bool isPinned)
        {
            var targets = (items ?? Enumerable.Empty<DocumentItem>())
                .Select(ResolvePin)
                .Where(item => item != null && pinService.CanToggle(item))
                .Distinct()
                .Where(item => pinService.IsPinned(item) != isPinned)
                .ToList();
            if (targets.Count == 0) return;

            await ExecuteMutationAsync(nameof(SetPinnedAsync), () =>
            {
                foreach (var target in targets)
                {
                    pinService.Toggle(target);
                }

                SaveSolutionState();
                OnPropertyChanged(nameof(CurrentNodes));
            });
            InvalidateCommands();
        }

        private bool CanTogglePin(DocumentItem item)
        {
            return pinService.CanToggle(item);
        }

        private void TogglePin(DocumentItem item)
        {
            _ = ExecuteMutationAsync(nameof(TogglePin), () =>
            {
                pinService.Toggle(item);
                SaveSolutionState();
                OnPropertyChanged(nameof(CurrentNodes));
            });
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
            pinService.ApplyIdMigration(idMap);

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

        public Task ToggleTagAsync(string tagId)
        {
            var targets = SelectedNodes.Select(x => ResolvePin(x) ?? x).Where(x => x != null).Distinct().ToList();
            return ExecuteMutationAsync("Tags", () => tagService.Toggle(targets, tagId));
        }

        public async Task<TagDefinition> AddTagAsync(string name, string color = "", string icon = "")
        {
            TagDefinition created = null;
            await ExecuteMutationAsync("Add tag", () =>
            {
                created = tagService.Add(state, name, color, icon);
                OnPropertyChanged(nameof(Tags));
            });
            return created;
        }

        public Task DeleteTagAsync(string tagId)
        {
            return ExecuteMutationAsync("Delete tag", () =>
            {
                tagService.Delete(state, tagId);
                OnPropertyChanged(nameof(Tags));
            });
        }
        public event EventHandler UndoRedoStateRestored;
        public string LastUndoRedoDiagnostics { get; private set; } = string.Empty;

        public IReadOnlyList<string> UndoOperations => undoRedoService.UndoOperations;
        public IReadOnlyList<string> RedoOperations => undoRedoService.RedoOperations;

        public void CaptureUndoState(string description = "Изменение", IEnumerable<DocumentItem> affectedItems = null)
        {
            if (!IsLoaded || isApplyingState || isRestoringUndoState) return;

            var snapshot = CreateUndoSnapshot();
            var ids = (affectedItems ?? SelectedNodes ?? Enumerable.Empty<DocumentItem>()).Where(x => x != null).Select(x => x.Id).ToArray();
            if (undoRedoService.Capture(description, snapshot, ids, ids, SelectedSet?.Id, SelectedSet?.Id))
            {
                InvalidateCommands();
            }
        }

        public async Task UndoManyAsync(int count)
        {
            var restored = false;
            ResetUndoRedoDiagnostics();
            var batch = count > 1;
            suppressUndoRedoNotification = batch;
            suppressUndoRedoPersistence = true;
            try
            {
                for (var i = 0; i < count && undoRedoService.CanUndo; i++)
                {
                    await UndoAsync();
                    restored = true;
                }
            }
            finally
            {
                suppressUndoRedoNotification = false;
                suppressUndoRedoPersistence = false;
            }
            if (!restored) return;
            if (batch) UndoRedoStateRestored?.Invoke(this, EventArgs.Empty);
            await Task.Yield();
            await PersistRestoredStateAsync();
        }

        public async Task RedoManyAsync(int count)
        {
            var restored = false;
            ResetUndoRedoDiagnostics();
            var batch = count > 1;
            suppressUndoRedoNotification = batch;
            suppressUndoRedoPersistence = true;
            try
            {
                for (var i = 0; i < count && undoRedoService.CanRedo; i++)
                {
                    await RedoAsync();
                    restored = true;
                }
            }
            finally
            {
                suppressUndoRedoNotification = false;
                suppressUndoRedoPersistence = false;
            }
            if (!restored) return;
            if (batch) UndoRedoStateRestored?.Invoke(this, EventArgs.Empty);
            await Task.Yield();
            await PersistRestoredStateAsync();
        }

        private async Task UndoAsync()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var currentSnapshot = CreateUndoSnapshot();
            timer.Stop();
            undoRedoSnapshotMilliseconds += timer.ElapsedMilliseconds;
            if (!undoRedoService.TryUndo(currentSnapshot, out var targetSnapshot, out var focusIds, out var focusSetId)) return;
            await RestoreUndoSnapshotAsync(targetSnapshot, focusIds, focusSetId);
        }

        private async Task RedoAsync()
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var currentSnapshot = CreateUndoSnapshot();
            timer.Stop();
            undoRedoSnapshotMilliseconds += timer.ElapsedMilliseconds;
            if (!undoRedoService.TryRedo(currentSnapshot, out var targetSnapshot, out var focusIds, out var focusSetId)) return;
            await RestoreUndoSnapshotAsync(targetSnapshot, focusIds, focusSetId);
        }

        private string CreateUndoSnapshot()
        {
            solutionState.Pins = pinService.Export();
            var envelope = new UndoSnapshot
            {
                State = JsonConvert.SerializeObject(state),
                Pins = JsonConvert.SerializeObject(solutionState.Pins ?? new List<PinLocalItem>())
            };
            return JsonConvert.SerializeObject(envelope);
        }

        private async Task RestoreUndoSnapshotAsync(string snapshot, IEnumerable<string> focusItemIds, string focusSetId)
        {
            if (string.IsNullOrWhiteSpace(snapshot)) return;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var envelope = JsonConvert.DeserializeObject<UndoSnapshot>(snapshot);
            var restoredState = JsonConvert.DeserializeObject<DocumentSetsState>(envelope?.State ?? "");
            timer.Stop();
            undoRedoDeserializeMilliseconds += timer.ElapsedMilliseconds;
            if (restoredState == null) return;

            isRestoringUndoState = true;
            timer.Restart();
            try
            {
                solutionState.Pins = JsonConvert.DeserializeObject<List<PinLocalItem>>(envelope.Pins ?? "[]") ?? new List<PinLocalItem>();
                ApplyLoadedState(restoredState, preferredSetName: null, selectedNodePath: null);
                var restoredSet = state.Sets.FirstOrDefault(x => x != null &&
                    string.Equals(x.Id, focusSetId, StringComparison.OrdinalIgnoreCase));
                if (restoredSet != null) SelectedSet = restoredSet;
                var selectedIds = new HashSet<string>(focusItemIds ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
                SetSelectedNodes(GetAllNodes().Where(x => x != null && selectedIds.Contains(x.Id)));
                timer.Stop();
                undoRedoApplyMilliseconds += timer.ElapsedMilliseconds;
                LastUndoRedoDiagnostics = $"snapshot {undoRedoSnapshotMilliseconds} ms, deserialize {undoRedoDeserializeMilliseconds} ms, apply {undoRedoApplyMilliseconds} ms";
                InvalidateCommands();
                if (!suppressUndoRedoNotification)
                {
                    UndoRedoStateRestored?.Invoke(this, EventArgs.Empty);
                    await Task.Yield();
                }
                if (!suppressUndoRedoPersistence) await PersistRestoredStateAsync();
            }
            finally
            {
                isRestoringUndoState = false;
                InvalidateCommands();
            }
        }

        private void ResetUndoRedoDiagnostics()
        {
            undoRedoSnapshotMilliseconds = 0;
            undoRedoDeserializeMilliseconds = 0;
            undoRedoApplyMilliseconds = 0;
            LastUndoRedoDiagnostics = string.Empty;
        }

        private async Task PersistRestoredStateAsync()
        {
            SaveSolutionState();
            await store.SaveAsync(state);
        }

        private void ClearUndoHistory()
        {
            undoRedoService.Clear();
            InvalidateCommands();
        }

        private sealed class UndoSnapshot
        {
            public string State { get; set; }
            public string Pins { get; set; }
        }

        private async Task ExecuteMutationAsync(string description, Func<Task> mutation)
        {
            if (mutation == null)
            {
                throw new ArgumentNullException(nameof(mutation));
            }

            var isOuterMutation = mutationNesting.Value == 0;
            string undoSnapshot = null;
            string undoFocusSetId = null;
            IReadOnlyList<string> undoFocusIds = Array.Empty<string>();
            IReadOnlyList<string> undoParentIds = Array.Empty<string>();
            if (isOuterMutation)
            {
                await mutationGate.WaitAsync();
            }

            try
            {
                if (isOuterMutation)
                {
                    mutationChanged = false;
                    mutationSaveRequested = false;
                    if (IsLoaded && !isApplyingState && !isRestoringUndoState)
                    {
                        undoSnapshot = CreateUndoSnapshot();
                        undoFocusSetId = SelectedSet?.Id;
                        undoFocusIds = SelectedNodes.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id)).Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                        undoParentIds = SelectedNodes.Where(x => x?.Parent != null && !string.IsNullOrWhiteSpace(x.Parent.Id)).Select(x => x.Parent.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                }

                mutationNesting.Value++;
                mutationDepth++;
                try
                {
                    await mutation();
                }
                finally
                {
                    mutationDepth--;
                    mutationNesting.Value--;
                }

                if (isOuterMutation)
                {
                    var currentSnapshot = undoSnapshot == null ? null : CreateUndoSnapshot();
                    var redoFocusIds = SelectedNodes.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Id)).Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (redoFocusIds.Length == 0) redoFocusIds = undoParentIds.ToArray();
                    if (undoSnapshot != null
                        && !string.Equals(undoSnapshot, currentSnapshot, StringComparison.Ordinal)
                        && undoRedoService.Capture(description, undoSnapshot, undoFocusIds, redoFocusIds, undoFocusSetId, SelectedSet?.Id ?? undoFocusSetId))
                    {
                        InvalidateCommands();
                    }

                    var shouldSave = mutationChanged || mutationSaveRequested;
                    mutationChanged = false;
                    mutationSaveRequested = false;
                    if (shouldSave)
                    {
                        await SaveCoreAsync();
                    }
                }
            }
            finally
            {
                if (isOuterMutation)
                {
                    mutationGate.Release();
                }
            }
        }

        private Task ExecuteMutationAsync(string description, Action mutation)
        {
            return ExecuteMutationAsync(description, () =>
            {
                mutation();
                return Task.CompletedTask;
            });
        }

        public Task SaveAsync()
        {
            if (mutationDepth > 0)
            {
                mutationSaveRequested = true;
                return Task.CompletedTask;
            }

            return SaveCoreAsync();
        }

        private async Task SaveCoreAsync()
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

    }
}
