using DocSets.Desktop.Panels;
using DocSets.Desktop.OneNote;
using WeifenLuo.WinFormsUI.Docking;
using System.Text;
using System.Diagnostics;

namespace DocSets.Desktop;

internal sealed class MainForm : Form
{
    private readonly DesktopSettingsStore _settingsStore = new();
    private readonly DesktopSettings _settings;
    private readonly DocSetsViewModel _viewModel;
    private readonly DocSetsPanelComposition _composition;
    private readonly DockPanel _dockPanel = new()
    {
        Dock = DockStyle.Fill,
        DocumentStyle = DocumentStyle.DockingWindow,
        Theme = new VS2015BlueTheme()
    };
    private readonly DesktopDockManager _dockManager;
    private readonly MenuStrip _menu = new();
    private readonly ToolStripMenuItem _recentMenu = new("Недавние");
    private readonly ToolStripMenuItem _panelsMenu = new("Панели");
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _documentStatus = new()
    {
        Spring = true,
        TextAlign = ContentAlignment.MiddleLeft
    };
    private readonly System.Windows.Forms.Timer _workspaceTimer;
    private readonly System.Windows.Forms.Timer _historyTimer;
    private Form _commentWindow;
    private DocSetsJoditCommentWindowControl _commentControl;
    private bool _workspaceCheckInProgress;
    private bool _closeCommitInProgress;
    private bool _closeCommitCompleted;
    private readonly Dictionary<string, CancellationTokenSource> _importJobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _deletedImportSessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ImportSessionDockContent> _importSessionContents = new(StringComparer.OrdinalIgnoreCase);

    public MainForm()
    {
        Text = "DocSets";
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        _settings = _settingsStore.Load();
        RestoreBoundsFromSettings();

        var solutionContext = new DesktopSolutionContextService();
        var workspace = new DocSetWorkspaceService(solutionContext);
        _viewModel = new DocSetsViewModel(
            workspace,
            new DesktopEditorTrackingService(),
            new DesktopUserDialogService(),
            new DesktopClipboardService(),
            new DesktopNavigationService(workspace.ResolvePath),
            new DesktopActiveDocumentService(),
            new DesktopPreviewService(workspace.ResolvePath),
            solutionContext);
        _composition = DocSetsPanelComposition.Create(_viewModel);
        _dockManager = new DesktopDockManager(_dockPanel);
        _dockManager.Register(_composition.Panels);
        _composition.Owner.ExternalPanelActivationRequested += ShowPanel;
        _composition.Owner.OpenJoditWindowRequested += OpenCommentWindow;
        _composition.Owner.ImportSessionCommandRequested += OnImportSessionCommandRequested;
        _viewModel.ImportSessionRemoved += OnImportSessionRemoved;

        CreateMenu();
        _status.Items.Add(_documentStatus);
        Controls.Add(_dockPanel);
        Controls.Add(_status);
        Controls.Add(_menu);
        MainMenuStrip = _menu;

        _workspaceTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _workspaceTimer.Tick += async (_, __) => await ReloadWorkspaceAsync();
        _historyTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _historyTimer.Tick += async (_, __) => await _viewModel.TrackNavigationHistoryAsync();

        Shown += async (_, __) => await InitializeAsync();
        FormClosing += OnFormClosing;
        FormClosed += (_, __) => SaveApplicationState();
    }

    private void CreateMenu()
    {
        var file = new ToolStripMenuItem("Файл");
        file.DropDownItems.Add(Item("Создать…", Keys.Control | Keys.N,
            async (_, __) => await CreateAsync()));
        file.DropDownItems.Add(Item("Открыть…", Keys.Control | Keys.O,
            async (_, __) => await OpenAsync()));
        file.DropDownItems.Add(_recentMenu);
        var import = new ToolStripMenuItem("Импорт");
        import.DropDownItems.Add(Item("OneNote...", Keys.None,
            async (_, __) => await StartOneNoteImportAsync()));
        import.DropDownItems.Add(Item("OneNote Test-1...", Keys.None,
            async (_, __) => await RunOneNoteObjectIdDiagnosticAsync()));
        file.DropDownItems.Add(import);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Сохранить", Keys.Control | Keys.S,
            async (_, __) => await SaveAsync()));
        file.DropDownItems.Add(Item("Закрыть DocSet", Keys.Control | Keys.W,
            async (_, __) => await CloseDocSetAsync()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Выход", Keys.Alt | Keys.F4, (_, __) => Close()));

        var edit = new ToolStripMenuItem("Правка");
        edit.DropDownItems.Add(Item("Отменить", Keys.Control | Keys.Z,
            (_, __) => Execute(_viewModel.UndoCommand)));
        edit.DropDownItems.Add(Item("Повторить", Keys.Control | Keys.Y,
            (_, __) => Execute(_viewModel.RedoCommand)));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Вырезать", Keys.Control | Keys.X,
            (_, __) => ExecuteActivePanelCommand("cut")));
        edit.DropDownItems.Add(Item("Копировать", Keys.Control | Keys.C,
            (_, __) => ExecuteActivePanelCommand("copy")));
        edit.DropDownItems.Add(Item("Вставить", Keys.Control | Keys.V,
            (_, __) => ExecuteActivePanelCommand("paste")));
        edit.DropDownItems.Add(Item("Выделить всё", Keys.Control | Keys.A,
            (_, __) => ExecuteActivePanelCommand("selectall")));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Переименовать", Keys.F2,
            (_, __) => Execute(_viewModel.RenameNodeCommand, _viewModel.SelectedNode)));
        edit.DropDownItems.Add(Item("Удалить", Keys.Delete,
            (_, __) => Execute(_viewModel.DeleteNodeCommand, _viewModel.SelectedNode)));
        edit.DropDownItems.Add(Item("Найти…", Keys.Control | Keys.F, (_, __) =>
        {
            ShowPanel(DocSetsPanelIds.Search);
            _composition.Owner.FocusSearch();
        }));

        RebuildPanelsMenu();
        _panelsMenu.DropDownOpening += (_, __) => RebuildPanelsMenu();
        _menu.Items.AddRange(new ToolStripItem[] { file, edit, _panelsMenu });
        RefreshRecentMenu();
    }

    private void RebuildPanelsMenu()
    {
        _dockManager.PopulateMenu(_panelsMenu.DropDownItems);
        _panelsMenu.DropDownItems.Add(new ToolStripSeparator());
        _panelsMenu.DropDownItems.Add(Item("Сбросить расположение", Keys.None,
            (_, __) => ResetLayout()));
    }

    private static ToolStripMenuItem Item(string text, Keys shortcut, EventHandler click)
    {
        var item = new ToolStripMenuItem(text, null, click);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        return item;
    }

    private async Task InitializeAsync()
    {
        RestoreLayout();
        await _viewModel.LoadAsync();
        _composition.Owner.RefreshAll();
        RememberCurrentDocument();
        UpdateTitle();
        _workspaceTimer.Start();
        _historyTimer.Start();
    }

    private async Task OpenAsync()
    {
        await _composition.Owner.OpenDocSetFromDialogAsync();
        RememberCurrentDocument();
        UpdateTitle();
    }

    private async Task CreateAsync()
    {
        await _composition.Owner.CreateDocSetFromDialogAsync();
        RememberCurrentDocument();
        UpdateTitle();
    }

    private async Task OpenRecentAsync(string path)
    {
        try
        {
            if (!await _viewModel.OpenDocSetAsync(path)) return;
            _composition.Owner.RefreshAll();
            RememberCurrentDocument();
            UpdateTitle();
        }
        catch (Exception exception)
        {
            ReportError("Не удалось открыть DocSet.", exception);
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            await _composition.Owner.SaveAsync();
            UpdateTitle();
        }
        catch (Exception exception)
        {
            ReportError("Не удалось сохранить DocSet.", exception);
        }
    }

    private async Task StartOneNoteImportAsync()
    {
        if (!_viewModel.CanSave)
        {
            MessageBox.Show(this, "Сначала откройте или создайте DocSet.",
                "Импорт из OneNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            await _composition.Owner.CommitPendingCommentAsync();
            var discovery = new OneNoteImportService(
                _viewModel.SaveImageAssetAsync, _viewModel.SaveFileAssetAsync);
            var notebooks = await discovery.GetNotebooksAsync(CancellationToken.None);
            if (notebooks.Count == 0)
            {
                MessageBox.Show(this, "OneNote не вернул доступных записных книжек.",
                    "Импорт из OneNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var notebook = OneNoteNotebookDialog.Select(this, notebooks);
            if (notebook == null) return;
            var session = new ImportSessionState
            {
                Name = "OneNote - " + notebook.Name,
                SourceType = "OneNote",
                SourceId = notebook.Id,
                SourceName = notebook.Name,
                Stage = "Подготовка"
            };
            await _viewModel.SaveImportSessionAsync(session);
            _ = RunOneNoteImportSessionAsync(session);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось запустить импорт OneNote.", exception);
        }
    }

    private async Task RunOneNoteImportSessionAsync(ImportSessionState session)
    {
        if (session == null || _importJobs.ContainsKey(session.Id)) return;
        var cancellation = new CancellationTokenSource();
        var checkpointGate = new SemaphoreSlim(1, 1);
        _importJobs[session.Id] = cancellation;
        ImportSessionStateMachine.StartOrResume(session);
        await _viewModel.SaveImportSessionAsync(session);

        var profile = DeserializeProfile(session.ProfileJson) ?? new OneNoteImportProfile();
        profile.StartOverall();
        var target = FindNode(session.TargetNodeId);
        var restoredReport = DeserializeReport(session.ReportJson);
        var service = new OneNoteImportService(_viewModel.SaveImageAssetAsync,
            _viewModel.SaveFileAssetAsync, profile, session.ObjectLinkCache,
            session.Pages.Where(x => x.Status == ImportPageStatus.Imported ||
                x.Status == ImportPageStatus.ImportedWithWarnings).Select(x => x.OneNotePageId),
            restoredReport?.Entries, session.Pages, EnumerateNodes(target));
        service.ObjectLinkCacheCheckpoint += () =>
        {
            var snapshot = service.GetObjectLinkCacheSnapshot().ToList();
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(new Action(async () =>
            {
                if (_deletedImportSessions.Contains(session.Id)) return;
                session.ObjectLinkCache = snapshot;
                await _viewModel.SaveImportSessionAsync(session);
            }));
        };
        OneNoteImportResult latest = null;
        var progress = new Progress<OneNoteImportProgress>(async value =>
        {
            await checkpointGate.WaitAsync();
            try
            {
                if (_deletedImportSessions.Contains(session.Id)) return;
                latest = value.ResultSnapshot ?? latest;
                ImportSessionStateMachine.ApplyProgress(session, value.Current, value.Total);
                session.OverallProgressPercent = Math.Max(0, Math.Min(100, value.OverallPercent));
                session.Stage = !string.IsNullOrWhiteSpace(value.Stage)
                    ? value.Stage + ": " + value.Message
                    : string.IsNullOrWhiteSpace(value.CompletedPageId)
                    ? "Импорт страницы: " + value.Message
                    : "Сохранение страницы: " + value.Message;
                session.ProfileJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                    profile.Snapshot(), Newtonsoft.Json.Formatting.Indented);
                if (value.ReportSnapshot != null)
                    session.ReportJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                        value.ReportSnapshot, Newtonsoft.Json.Formatting.Indented);
                if (latest?.Root != null)
                {
                    if (target == null)
                    {
                        target = latest.Root;
                        session.TargetNodeId = target.Id;
                        await _viewModel.AddImportedRootAsync(target,
                            "Импорт OneNote: " + session.SourceName, latest.NoteTagStyles,
                            selectRoot: false);
                    }
                    else if (!ReferenceEquals(target, latest.Root)) MergeImportTree(target, latest.Root, session);
                }
                if (!string.IsNullOrWhiteSpace(value.CompletedPageId))
                {
                    var page = session.Pages.FirstOrDefault(x => string.Equals(x.OneNotePageId,
                        value.CompletedPageId, StringComparison.OrdinalIgnoreCase));
                    if (page == null) session.Pages.Add(page = new ImportPageState
                        { OneNotePageId = value.CompletedPageId });
                    page.DocSetsNodeId = value.CompletedNodeId;
                    var pageReport = value.ReportSnapshot?.Entries.LastOrDefault(x =>
                        string.Equals(x.ObjectType, "Page", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(x.OneNotePageId, value.CompletedPageId, StringComparison.OrdinalIgnoreCase));
                    page.Status = pageReport?.Status == OneNoteImportStatus.NotImported
                        ? ImportPageStatus.Failed
                        : pageReport?.Status == OneNoteImportStatus.ImportedWithWarnings
                            ? ImportPageStatus.ImportedWithWarnings : ImportPageStatus.Imported;
                    page.ImportedAtUtc ??= DateTimeOffset.UtcNow;
                    if (!string.IsNullOrWhiteSpace(value.ContentChecksum))
                        page.ContentChecksum = value.ContentChecksum;
                    if (value.OneNoteModifiedAtUtc.HasValue)
                        page.OneNoteModifiedAtUtc = value.OneNoteModifiedAtUtc;
                    if (value.ReportSnapshot != null)
                        session.ReportJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                            value.ReportSnapshot, Newtonsoft.Json.Formatting.Indented);
                    session.ProfileJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                        profile.Snapshot(), Newtonsoft.Json.Formatting.Indented);
                    session.ObjectLinkCache = service.GetObjectLinkCacheSnapshot().ToList();
                    UpdateStatistics(session, latest, value.ReportSnapshot);
                    await _viewModel.SaveAsync();
                    await _viewModel.SaveImportSessionAsync(session);
                }
                else
                {
                    await _viewModel.SaveImportSessionAsync(session);
                }
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("OneNote", "Не удалось сохранить контрольную точку импорта.", exception);
            }
            finally { checkpointGate.Release(); }
        });

        try
        {
            var notebook = new OneNoteNotebook(session.SourceId, session.SourceName);
            latest = await service.ImportAsync(notebook, progress, cancellation.Token);
            if (!latest.LinkResolutionCompleted)
                throw new InvalidOperationException(
                    "Стадия разрешения внутренних ссылок не завершена.");
            await checkpointGate.WaitAsync();
            try
            {
                if (latest.Root != null)
                {
                    if (target == null)
                    {
                        target = latest.Root; session.TargetNodeId = target.Id;
                        await _viewModel.AddImportedRootAsync(target,
                            "Импорт OneNote: " + session.SourceName, latest.NoteTagStyles,
                            selectRoot: false);
                    }
                    else if (!ReferenceEquals(target, latest.Root)) MergeImportTree(target, latest.Root, session);
                }
                profile.StopOverall(OneNoteImportService.ProfileRoot);
                session.Status = ImportSessionStatus.Completed;
                session.LinkResolutionCompleted = true;
                session.Stage = "Завершено";
                session.CompletedAtUtc = DateTimeOffset.UtcNow;
                session.ProgressCurrent = session.ProgressTotal;
                session.OverallProgressPercent = 100;
                session.Errors = latest.Errors.ToList();
                session.Warnings = latest.Report.Entries.Where(x =>
                    x.Status == OneNoteImportStatus.ImportedWithWarnings ||
                    x.Status == OneNoteImportStatus.ConvertedWithLoss)
                    .Select(x => x.Name + ": " + x.Reason).Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct().ToList();
                session.ReportJson = Newtonsoft.Json.JsonConvert.SerializeObject(latest.Report, Newtonsoft.Json.Formatting.Indented);
                session.ProfileJson = Newtonsoft.Json.JsonConvert.SerializeObject(profile.Snapshot(), Newtonsoft.Json.Formatting.Indented);
                session.ObjectLinkCache = service.GetObjectLinkCacheSnapshot().ToList();
                UpdateStatistics(session, latest, latest.Report);
                await _viewModel.SaveAsync();
                await _viewModel.SaveImportSessionAsync(session);
            }
            finally { checkpointGate.Release(); }
        }
        catch (OperationCanceledException)
        {
            if (_deletedImportSessions.Contains(session.Id)) return;
            ImportSessionStateMachine.CompletePause(session);
            session.ObjectLinkCache = service.GetObjectLinkCacheSnapshot().ToList();
            await _viewModel.SaveAsync();
            await _viewModel.SaveImportSessionAsync(session);
        }
        catch (Exception exception)
        {
            if (_deletedImportSessions.Contains(session.Id)) return;
            session.Status = ImportSessionStatus.Failed;
            session.Stage = "Ошибка";
            session.Errors.Add(exception.GetBaseException().Message);
            session.ObjectLinkCache = service.GetObjectLinkCacheSnapshot().ToList();
            await _viewModel.SaveImportSessionAsync(session);
            DocSetsLog.Current.Error("OneNote", "Фоновый импорт завершился ошибкой.", exception);
        }
        finally
        {
            _importJobs.Remove(session.Id);
            cancellation.Dispose();
            checkpointGate.Dispose();
        }
    }

    private async void OnImportSessionCommandRequested(object sender, ImportSessionCommandEventArgs e)
    {
        var session = _viewModel.FindImportSession(e.SessionId);
        if (session == null) return;
        if (e.Command == ImportSessionCommand.Open)
        {
            ShowImportSessionContent(session);
        }
        else if (e.Command == ImportSessionCommand.Resume)
            await RunOneNoteImportSessionAsync(session);
        else if (e.Command == ImportSessionCommand.Pause)
        {
            _importJobs.TryGetValue(session.Id, out var cancellation);
            Action cancel = cancellation == null ? null : cancellation.Cancel;
            if (!ImportSessionStateMachine.RequestPause(session, cancel)) return;
            await _viewModel.SaveImportSessionAsync(session);
        }
        else if (e.Command == ImportSessionCommand.Delete)
        {
            if (MessageBox.Show(this,
                    "Удалить информацию о сессии? Импортированные документы останутся.",
                    "Импорт OneNote", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2) != DialogResult.Yes)
                return;
            await _viewModel.DeleteImportSessionAsync(session.Id);
        }
    }

    private void OnImportSessionRemoved(object sender, ImportSessionRemovedEventArgs e)
    {
        _deletedImportSessions.Add(e.SessionId);
        if (_importJobs.TryGetValue(e.SessionId, out var cancellation)) cancellation.Cancel();
        if (_importSessionContents.TryGetValue(e.SessionId, out var content)) content.Close();
    }

    private void ShowImportSessionContent(ImportSessionState session)
    {
        if (_importSessionContents.TryGetValue(session.Id, out var existing) && !existing.IsDisposed)
        {
            existing.Show();
            existing.Activate();
            return;
        }
        var content = new ImportSessionDockContent(session, OpenImportedEntryAsync);
        content.Report.CommandRequested += OnImportSessionCommandRequested;
        content.FormClosed += (_, __) => _importSessionContents.Remove(session.Id);
        _importSessionContents[session.Id] = content;
        content.Show(_dockPanel, DockState.Document);
        content.Activate();
    }

    private async Task OpenImportedEntryAsync(OneNoteImportReportEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry?.DocSetsNodeId) ||
            !await _viewModel.OpenBookmarkByIdAsync(entry.DocSetsNodeId))
        {
            MessageBox.Show(this, "Связанный объект DocSets не найден.",
                "Отчёт импорта OneNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        _composition.Owner.NavigateToSelectedItem();
        ShowPanel(DocSetsPanelIds.Note);
        var note = _composition.GetPanel(DocSetsPanelIds.Note)?.Controls
            .OfType<DocSetsHtmlCommentWindowControl>().FirstOrDefault();
        if (note != null)
            await note.NavigateToAnchorAsync(_composition.Owner.CurrentCommentItem,
                entry.DocSetsAnchorId);
    }

    private DocumentItem FindNode(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return FindNode(_viewModel.Sets, id);
    }

    private static DocumentItem FindNode(IEnumerable<DocumentItem> nodes, string id)
    {
        foreach (var node in nodes ?? Enumerable.Empty<DocumentItem>())
        {
            if (string.Equals(node?.Id, id, StringComparison.OrdinalIgnoreCase)) return node;
            var child = FindNode(node?.Children, id);
            if (child != null) return child;
        }
        return null;
    }

    private static void MergeImportTree(DocumentItem target, DocumentItem source,
        ImportSessionState session)
    {
        foreach (var sourceChild in source.Children.ToList())
        {
            var targetChild = target.Children.FirstOrDefault(x => string.Equals(
                x.Id, sourceChild.Id, StringComparison.OrdinalIgnoreCase));
            if (targetChild == null) target.Children.Add(sourceChild);
            else
            {
                var page = session?.Pages?.FirstOrDefault(x => string.Equals(
                    x.DocSetsNodeId, targetChild.Id, StringComparison.OrdinalIgnoreCase));
                if (page != null && !string.IsNullOrWhiteSpace(sourceChild.Content) &&
                    string.Equals(page.ContentChecksum, ComputeContentChecksum(targetChild.Content),
                        StringComparison.Ordinal))
                {
                    targetChild.Content = sourceChild.Content;
                    page.ContentChecksum = ComputeContentChecksum(sourceChild.Content);
                }
                MergeImportTree(targetChild, sourceChild, session);
            }
        }
    }

    private static string ComputeContentChecksum(string content)
    {
        using var hash = System.Security.Cryptography.SHA256.Create();
        return Convert.ToBase64String(hash.ComputeHash(
            System.Text.Encoding.UTF8.GetBytes(content ?? "")));
    }

    private static OneNoteImportProfile DeserializeProfile(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return Newtonsoft.Json.JsonConvert.DeserializeObject<OneNoteImportProfile>(json); }
        catch { return null; }
    }

    private static OneNoteImportReport DeserializeReport(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return Newtonsoft.Json.JsonConvert.DeserializeObject<OneNoteImportReport>(json); }
        catch { return null; }
    }

    private static IEnumerable<DocumentItem> EnumerateNodes(DocumentItem root)
    {
        if (root == null) yield break;
        yield return root;
        foreach (var child in root.Children.ToList())
            foreach (var nested in EnumerateNodes(child)) yield return nested;
    }

    private static void UpdateStatistics(ImportSessionState session, OneNoteImportResult result,
        OneNoteImportReport report)
    {
        if (session == null || result == null) return;
        session.Statistics.Sections = Math.Max(session.Statistics.Sections, result.Folders);
        session.Statistics.Pages = Math.Max(session.Statistics.Pages,
            Math.Max(result.Pages, session.Pages.Count));
        session.Statistics.Images = Math.Max(session.Statistics.Images, result.Images);
        session.Statistics.Attachments = Math.Max(session.Statistics.Attachments, result.Attachments);
        session.Statistics.Tags = Math.Max(session.Statistics.Tags, result.NoteTags);
        session.Statistics.InternalLinks = Math.Max(session.Statistics.InternalLinks, result.InternalLinks);
        session.Statistics.FailedPages = Math.Max(session.Statistics.FailedPages, result.FailedPages);
        if (report == null) return;
        var reportEntries = report.Entries?.ToArray() ?? Array.Empty<OneNoteImportReportEntry>();
        session.Statistics.ObjectLinks = reportEntries.Count(x =>
            !string.IsNullOrWhiteSpace(x.OneNoteObjectId));
        session.Statistics.ExternalLinks = reportEntries.Count(x =>
            string.Equals(x.ObjectType, "Link", StringComparison.OrdinalIgnoreCase) &&
            !x.Name.StartsWith("onenote:", StringComparison.OrdinalIgnoreCase));
        session.Statistics.Tables = reportEntries.Count(x =>
            string.Equals(x.ObjectType, "Table", StringComparison.OrdinalIgnoreCase));
    }

#if false // Replaced by persistent background import sessions; retained only as migration reference.
    private async Task ImportOneNoteAsync()
    {
        if (!_viewModel.CanSave)
        {
            MessageBox.Show(this, "Сначала откройте или создайте DocSet.",
                "Импорт из OneNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        try
        {
            await _composition.Owner.CommitPendingCommentAsync();
            var profiler = new OneNoteImportProfile();
            var service = new OneNoteImportService(
                _viewModel.SaveImageAssetAsync, _viewModel.SaveFileAssetAsync, profiler);
            var notebooks = await service.GetNotebooksAsync(CancellationToken.None);
            if (notebooks.Count == 0)
            {
                MessageBox.Show(this, "OneNote не вернул доступных записных книжек.",
                    "Импорт из OneNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var notebook = OneNoteNotebookDialog.Select(this, notebooks);
            if (notebook == null) return;
            profiler.StartOverall();
            var result = OneNoteProgressDialog.Run(this,
                (progress, token) => service.ImportAsync(notebook, progress, token),
                async importedResult =>
                {
                    var saveCallsBefore = _viewModel.SaveInvocationCount;
                    var saveDurationBefore = _viewModel.TotalSaveDuration;
                    var treeUpdatesBefore = _composition.Owner.RefreshAllInvocationCount;
                    var treeUpdateDurationBefore = _composition.Owner.TotalRefreshAllDuration;
                    await _viewModel.AddImportedRootAsync(importedResult.Root,
                        "Импорт OneNote: " + notebook.Name, importedResult.NoteTagStyles);
                    _composition.Owner.RefreshAll();
                    profiler.StopOverall(OneNoteImportService.ProfileRoot);
                    profiler.DocSetSaveCalls = (int)Math.Max(0,
                        _viewModel.SaveInvocationCount - saveCallsBefore);
                    profiler.TreeUpdateCalls = (int)Math.Max(0,
                        _composition.Owner.RefreshAllInvocationCount - treeUpdatesBefore);
                    profiler.Record(OneNoteImportService.ProfileSave,
                        _viewModel.TotalSaveDuration - saveDurationBefore, profiler.DocSetSaveCalls);
                    profiler.Record(OneNoteImportService.ProfileUi,
                        _composition.Owner.TotalRefreshAllDuration - treeUpdateDurationBefore,
                        profiler.TreeUpdateCalls);
                    profiler.DocSetSavedAfterEachPage = importedResult.Pages > 0 &&
                        profiler.DocSetSaveCalls >= importedResult.Pages;
                    profiler.TreeUpdatedAfterEachPage = importedResult.Pages > 0 &&
                        profiler.TreeUpdateCalls >= importedResult.Pages;
                    importedResult.Report.Profile = profiler;
                    importedResult.Report.ImportedRootNodeId = importedResult.Root.Id;
                    var reportDirectory = Path.Combine(_viewModel.ActiveDocSetDirectory, "reports");
                    Directory.CreateDirectory(reportDirectory);
                    var reportPath = Path.Combine(reportDirectory,
                        "onenote-import-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
                    File.WriteAllText(reportPath,
                        Newtonsoft.Json.JsonConvert.SerializeObject(importedResult.Report,
                            Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
                    var profilePath = Path.Combine(reportDirectory,
                        Path.GetFileNameWithoutExtension(reportPath) + "-profile.json");
                    File.WriteAllText(profilePath,
                        Newtonsoft.Json.JsonConvert.SerializeObject(profiler,
                            Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
                    return new OneNoteImportPresentation
                    {
                        Result = importedResult,
                        ReportPath = reportPath,
                        ProfilePath = profilePath,
                        OpenDocSets = async entry =>
                        {
                            if (string.IsNullOrWhiteSpace(entry?.DocSetsNodeId) ||
                                !await _viewModel.OpenBookmarkByIdAsync(entry.DocSetsNodeId))
                            {
                                MessageBox.Show(this, "Связанный объект DocSets не найден.",
                                    "Отчёт импорта OneNote", MessageBoxButtons.OK,
                                    MessageBoxIcon.Information);
                                return;
                            }
                            _composition.Owner.NavigateToSelectedItem();
                            ShowPanel(DocSetsPanelIds.Note);
                            var note = _composition.GetPanel(DocSetsPanelIds.Note)?.Controls
                                .OfType<DocSetsHtmlCommentWindowControl>().FirstOrDefault();
                            if (note != null)
                                await note.NavigateToAnchorAsync(_composition.Owner.CurrentCommentItem,
                                    entry.DocSetsAnchorId);
                        }
                    };
                });
            if (result.Cancelled || result.Root == null)
            {
                MessageBox.Show(this, "Импорт отменён. Дерево DocSets не изменено.",
                    "Импорт из OneNote", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        catch (Exception exception)
        {
            ReportError("Не удалось импортировать записную книжку OneNote.", exception);
        }
    }

#endif
    private async Task RunOneNoteObjectIdDiagnosticAsync()
    {
        if (!_viewModel.CanSave)
        {
            MessageBox.Show(this, "Сначала откройте или создайте DocSet для сохранения отчёта.",
                "OneNote Test-1", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            var importService = new OneNoteImportService(_viewModel.SaveImageAssetAsync,
                _viewModel.SaveFileAssetAsync);
            var notebooks = await importService.GetNotebooksAsync(CancellationToken.None);
            if (notebooks.Count == 0)
            {
                MessageBox.Show(this, "OneNote не вернул доступных записных книжек.",
                    "OneNote Test-1", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var notebook = OneNoteNotebookDialog.Select(this, notebooks);
            if (notebook == null) return;
            var service = new OneNoteObjectIdDiagnosticService();
            var report = OneNoteDiagnosticDialog.Run(this,
                (progress, token) => service.RunAsync(notebook, progress, token));
            if (report == null) return;

            var reportDirectory = Path.Combine(_viewModel.ActiveDocSetDirectory, "reports");
            Directory.CreateDirectory(reportDirectory);
            var baseName = "onenote-object-id-test-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
            var jsonPath = Path.Combine(reportDirectory, baseName + ".json");
            var textPath = Path.Combine(reportDirectory, baseName + ".txt");
            File.WriteAllText(jsonPath, Newtonsoft.Json.JsonConvert.SerializeObject(report,
                Newtonsoft.Json.Formatting.Indented), Encoding.UTF8);
            File.WriteAllText(textPath,
                OneNoteObjectIdDiagnosticService.BuildTextSummary(report), Encoding.UTF8);
            MessageBox.Show(this,
                $"Исследование завершено.\r\n\r\nОбъектных ссылок: {report.TotalObjectLinks}\r\n" +
                $"Разрешено COM: {report.ResolvedByCom}\r\nНе разрешено COM: {report.NotResolvedByCom}\r\n\r\n" +
                $"JSON: {jsonPath}\r\nTXT: {textPath}",
                "OneNote Test-1", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception exception)
        {
            ReportError("Не удалось выполнить OneNote Test-1.", exception);
        }
    }

    private async Task CloseDocSetAsync()
    {
        try
        {
            await _composition.Owner.CommitPendingCommentAsync();
            if (!await _viewModel.CloseActiveDocSetAsync()) return;
            _composition.Owner.RefreshAll();
            UpdateTitle();
        }
        catch (Exception exception)
        {
            ReportError("Не удалось закрыть DocSet.", exception);
        }
    }

    private async Task ReloadWorkspaceAsync()
    {
        if (_workspaceCheckInProgress || !_viewModel.IsLoaded) return;
        _workspaceCheckInProgress = true;
        try
        {
            if (await _viewModel.ReloadIfWorkspaceChangedAsync())
                _composition.Owner.RefreshAll();
        }
        finally
        {
            _workspaceCheckInProgress = false;
        }
    }

    private async void OpenCommentWindow(object sender, EventArgs e)
    {
        if (_commentWindow == null || _commentWindow.IsDisposed)
        {
            _commentControl = new DocSetsJoditCommentWindowControl { Dock = DockStyle.Fill };
            _commentWindow = new Form
            {
                Text = "DocSets Заметка — Jodit (HTML)",
                StartPosition = FormStartPosition.CenterParent,
                Width = 1100,
                Height = 800
            };
            _commentWindow.Controls.Add(_commentControl);
        }

        var item = _viewModel.ResolvePin(_composition.Owner.CurrentCommentItem)
            ?? _composition.Owner.CurrentCommentItem;
        if (item == null) return;
        await _commentControl.AttachAsync(_viewModel, _composition.Owner, item);
        _commentWindow.Show(this);
        _commentWindow.Activate();
    }

    private void RestoreLayout()
    {
        if (File.Exists(_settingsStore.LayoutPath))
        {
            try
            {
                _dockPanel.LoadFromXml(_settingsStore.LayoutPath,
                    _dockManager.Deserialize);
                return;
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Layout",
                    "Сохранённое расположение повреждено; применяется стандартное.",
                    exception);
                _settingsStore.DeleteLayout();
            }
        }
        _dockManager.ShowDefaultLayout();
    }

    private void ResetLayout()
    {
        _settingsStore.DeleteLayout();
        _dockManager.ResetLayout();
        RebuildPanelsMenu();
    }

    private void ShowPanel(string persistId)
    {
        _dockManager.Show(persistId);
    }

    private void ExecuteActivePanelCommand(string command)
    {
        var panelId = (_dockPanel.ActiveContent as DesktopDockContent)?.PersistId
            ?? DocSetsPanelIds.Note;
        _composition.Owner.ExecutePanelCommand(panelId, command);
    }

    private void RememberCurrentDocument()
    {
        var path = _viewModel.ActiveDocSetDirectory;
        if (string.IsNullOrWhiteSpace(path)) return;
        _settings.RecentDocSets.RemoveAll(value => string.Equals(
            value, path, StringComparison.OrdinalIgnoreCase));
        _settings.RecentDocSets.Insert(0, path);
        if (_settings.RecentDocSets.Count > 10)
            _settings.RecentDocSets.RemoveRange(10, _settings.RecentDocSets.Count - 10);
        _settingsStore.Save(_settings);
        RefreshRecentMenu();
    }

    private void RefreshRecentMenu()
    {
        _recentMenu.DropDownItems.Clear();
        foreach (var path in _settings.RecentDocSets.Where(Directory.Exists))
        {
            var item = new ToolStripMenuItem(path) { ToolTipText = path };
            item.Click += async (_, __) => await OpenRecentAsync(path);
            _recentMenu.DropDownItems.Add(item);
        }
        _recentMenu.Enabled = _recentMenu.DropDownItems.Count > 0;
    }

    private void UpdateTitle()
    {
        var name = _viewModel.ActiveDocSetName;
        Text = string.IsNullOrWhiteSpace(name) ? "DocSets" : name + " — DocSets";
        _documentStatus.Text = string.IsNullOrWhiteSpace(_viewModel.ActiveDocSetDirectory)
            ? "DocSet не открыт"
            : _viewModel.ActiveDocSetDirectory;
    }

    private async void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_closeCommitCompleted) return;
        e.Cancel = true;
        if (_closeCommitInProgress) return;

        _closeCommitInProgress = true;
        _workspaceTimer.Stop();
        _historyTimer.Stop();
        try
        {
            foreach (var job in _importJobs.ToList())
            {
                job.Value.Cancel();
                var session = _viewModel.FindImportSession(job.Key);
                if (session == null) continue;
                session.Status = ImportSessionStatus.Paused;
                session.Stage = "Приостановлено при завершении приложения";
                await _viewModel.SaveImportSessionAsync(session);
            }
            await _composition.Owner.CommitPendingCommentAsync();
            if (_commentControl != null && !_commentControl.IsDisposed)
                await _commentControl.CommitPendingEditBeforeCloseAsync();
            _composition.Owner.SaveLocalSettings();
            _closeCommitCompleted = true;
            BeginInvoke(new Action(Close));
        }
        finally
        {
            _closeCommitInProgress = false;
        }
    }

    private void SaveApplicationState()
    {
        if (WindowState == FormWindowState.Normal)
        {
            _settings.X = Bounds.X;
            _settings.Y = Bounds.Y;
            _settings.Width = Bounds.Width;
            _settings.Height = Bounds.Height;
        }
        _settings.WindowState = WindowState == FormWindowState.Minimized
            ? FormWindowState.Normal
            : WindowState;
        _settingsStore.Save(_settings);
        try
        {
            var directory = Path.GetDirectoryName(_settingsStore.LayoutPath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            _dockPanel.SaveAsXml(_settingsStore.LayoutPath);
        }
        catch (Exception exception)
        {
            DocSetsLog.Current.Error("Layout",
                "Не удалось сохранить расположение панелей.", exception);
        }
    }

    private void RestoreBoundsFromSettings()
    {
        StartPosition = FormStartPosition.Manual;
        var bounds = _settings.Bounds;
        Bounds = Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds))
            ? bounds
            : new Rectangle(80, 80, 1400, 900);
        WindowState = _settings.WindowState;
    }

    private static void Execute(System.Windows.Input.ICommand command, object parameter = null)
    {
        if (command?.CanExecute(parameter) == true) command.Execute(parameter);
    }

    private void ReportError(string message, Exception exception)
    {
        DocSetsLog.Current.Error("Desktop", message, exception);
        MessageBox.Show(this, message + Environment.NewLine + exception.Message,
            "DocSets", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _composition.Owner.ExternalPanelActivationRequested -= ShowPanel;
            _composition.Owner.OpenJoditWindowRequested -= OpenCommentWindow;
            _viewModel.ImportSessionRemoved -= OnImportSessionRemoved;
            _workspaceTimer.Dispose();
            _historyTimer.Dispose();
            _commentWindow?.Dispose();
            _composition.Dispose();
        }
        base.Dispose(disposing);
    }
}
