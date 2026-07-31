namespace DocSets.Desktop;

internal sealed class MainForm : Form
{
    private readonly DocSetsViewModel _viewModel;
    private readonly DocSetsWinFormsControl _docSetsControl;
    private readonly System.Windows.Forms.Timer _workspaceTimer;
    private readonly System.Windows.Forms.Timer _historyTimer;
    private Form _commentWindow;
    private DocSetsJoditCommentWindowControl _commentControl;
    private bool _workspaceCheckInProgress;
    private bool _closeCommitInProgress;
    private bool _closeCommitCompleted;

    public MainForm()
    {
        Text = "DocSets";
        StartPosition = FormStartPosition.CenterScreen;
        WindowState = FormWindowState.Maximized;
        AutoScaleMode = AutoScaleMode.Dpi;

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
        _docSetsControl = new DocSetsWinFormsControl(_viewModel) { Dock = DockStyle.Fill };
        _docSetsControl.OpenJoditWindowRequested += OpenCommentWindow;
        Controls.Add(_docSetsControl);

        _workspaceTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _workspaceTimer.Tick += async (_, __) => await ReloadWorkspaceAsync();
        _historyTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _historyTimer.Tick += async (_, __) => await _viewModel.TrackNavigationHistoryAsync();

        Shown += async (_, __) =>
        {
            await _viewModel.LoadAsync();
            _docSetsControl.RefreshAll();
            _workspaceTimer.Start();
            _historyTimer.Start();
        };
        FormClosing += OnFormClosing;
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
        var item = _viewModel.ResolvePin(_docSetsControl.CurrentCommentItem)
            ?? _docSetsControl.CurrentCommentItem;
        if (item == null) return;
        await _commentControl.AttachAsync(_viewModel, _docSetsControl, item);
        _commentWindow.Show(this);
        _commentWindow.Activate();
    }

    private async Task ReloadWorkspaceAsync()
    {
        if (_workspaceCheckInProgress || !_viewModel.IsLoaded) return;
        _workspaceCheckInProgress = true;
        try
        {
            if (await _viewModel.ReloadIfWorkspaceChangedAsync())
                _docSetsControl.RefreshAll();
        }
        finally
        {
            _workspaceCheckInProgress = false;
        }
    }

    private async void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (_closeCommitCompleted)
            return;

        e.Cancel = true;
        if (_closeCommitInProgress)
            return;

        _closeCommitInProgress = true;
        _workspaceTimer.Stop();
        _historyTimer.Stop();
        try
        {
            await _docSetsControl.CommitPendingCommentAsync();
            if (_commentControl != null && !_commentControl.IsDisposed)
                await _commentControl.CommitPendingEditBeforeCloseAsync();
            _docSetsControl.SaveLocalSettings();
            _closeCommitCompleted = true;
            BeginInvoke(new Action(Close));
        }
        finally
        {
            _closeCommitInProgress = false;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _docSetsControl.OpenJoditWindowRequested -= OpenCommentWindow;
            _workspaceTimer.Dispose();
            _historyTimer.Dispose();
            _commentWindow?.Dispose();
        }
        base.Dispose(disposing);
    }
}
