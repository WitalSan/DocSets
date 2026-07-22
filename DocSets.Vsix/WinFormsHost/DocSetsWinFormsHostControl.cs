using Microsoft.VisualStudio.Shell;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Forms.Integration;
using System.Windows.Threading;

namespace DocSets
{
    internal sealed class DocSetsWinFormsHostControl : WindowsFormsHost
    {
        private readonly DocSetsViewModel viewModel;
        private readonly DocSetsWinFormsControl winFormsControl;
        private readonly DispatcherTimer solutionLoadRetryTimer;
        private readonly DispatcherTimer workspaceSyncTimer;
        private readonly DispatcherTimer navigationHistoryTimer;
        private readonly EnvDTE.SolutionEvents solutionEvents;
        private bool workspaceSyncInProgress;
        private int solutionLoadRetryCount;

        public DocSetsWinFormsHostControl(AsyncPackage package)
        {
            viewModel = new DocSetsViewModel(package, () => Window.GetWindow(this));
            winFormsControl = new DocSetsWinFormsControl(viewModel);
            winFormsControl.CommentEditorFocusRequested += OnCommentEditorFocusRequested;
            winFormsControl.OpenCommentWindowRequested += OnOpenCommentWindowRequested;
            winFormsControl.OpenMilkdownWindowRequested += OnOpenMilkdownWindowRequested;
            winFormsControl.CommentSearchMatchRequested += OnCommentSearchMatchRequested;
            Focusable = true;
            Child = winFormsControl;

            solutionLoadRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            solutionLoadRetryTimer.Tick += (_, __) => RetryLoadAfterSolutionOpened();

            workspaceSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            workspaceSyncTimer.Tick += async (_, __) => await CheckWorkspaceChangesAsync();

            navigationHistoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            navigationHistoryTimer.Tick += async (_, __) => await viewModel.TrackNavigationHistoryAsync();

            var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
            solutionEvents = dte?.Events?.SolutionEvents;
            if (solutionEvents != null)
                solutionEvents.BeforeClosing += OnSolutionBeforeClosing;

            Loaded += (_, __) =>
            {
                ThreadHelper.JoinableTaskFactory.Run(viewModel.LoadAsync);
                winFormsControl.RefreshAll();

                if (!viewModel.IsLoaded)
                {
                    solutionLoadRetryCount = 0;
                    solutionLoadRetryTimer.Start();
                }

                workspaceSyncTimer.Start();
                navigationHistoryTimer.Start();
            };

            Unloaded += (_, __) =>
            {
                solutionLoadRetryTimer.Stop();
                workspaceSyncTimer.Stop();
                navigationHistoryTimer.Stop();
                winFormsControl.SaveLocalSettings();
            };
        }

        private bool OnCommentSearchMatchRequested(DocumentItem item, int start, int length, int occurrenceIndex)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (DocSetsMilkdownCommentToolWindow.TryShowSearchResult(
                    DocSetsPackage.Instance, item, start, length, occurrenceIndex)) return true;
            return DocSetsCommentToolWindow.TryShowSearchResult(DocSetsPackage.Instance, item, start, length, occurrenceIndex);
        }
        private void OnOpenCommentWindowRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DocSetsCommentToolWindow.Show(DocSetsPackage.Instance, viewModel, winFormsControl);
        }

        private void OnOpenMilkdownWindowRequested(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DocSetsMilkdownCommentToolWindow.Show(DocSetsPackage.Instance, viewModel, winFormsControl);
        }

        private void OnCommentEditorFocusRequested(object sender, EventArgs e)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                if (!IsVisible || winFormsControl.IsDisposed) return;
                Focus();
                Keyboard.Focus(this);
                winFormsControl.Select();
                winFormsControl.FocusCommentEditor();
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (!IsVisible || winFormsControl.IsDisposed) return;
                    Focus();
                    Keyboard.Focus(this);
                    winFormsControl.FocusCommentEditor();
                }));
            }));
        }
        private void OnSolutionBeforeClosing()
        {
            winFormsControl.SaveLocalSettings();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                winFormsControl.SaveLocalSettings();
                winFormsControl.CommentEditorFocusRequested -= OnCommentEditorFocusRequested;
                winFormsControl.OpenCommentWindowRequested -= OnOpenCommentWindowRequested;
                winFormsControl.OpenMilkdownWindowRequested -= OnOpenMilkdownWindowRequested;
                winFormsControl.CommentSearchMatchRequested -= OnCommentSearchMatchRequested;
                if (solutionEvents != null)
                    solutionEvents.BeforeClosing -= OnSolutionBeforeClosing;
            }
            base.Dispose(disposing);
        }

        internal async System.Threading.Tasks.Task AddBookmarkFromEditorAsync()
        {
            await EnsureLoadedAsync();
            await winFormsControl.AddBookmarkFromEditorAsync();
        }

        internal async System.Threading.Tasks.Task FindBookmarksFromEditorAsync()
        {
            await EnsureLoadedAsync();
            await winFormsControl.FindBookmarksFromEditorAsync();
        }

        private async System.Threading.Tasks.Task EnsureLoadedAsync()
        {
            if (!viewModel.IsLoaded)
            {
                await viewModel.LoadAsync();
                winFormsControl.RefreshAll();
            }
        }

        private async System.Threading.Tasks.Task CheckWorkspaceChangesAsync()
        {
            if (workspaceSyncInProgress || !viewModel.IsLoaded)
            {
                return;
            }

            workspaceSyncInProgress = true;
            try
            {
                if (await viewModel.ReloadIfWorkspaceChangedAsync())
                {
                    winFormsControl.RefreshAll();
                }
            }
            finally
            {
                workspaceSyncInProgress = false;
            }
        }

        private void RetryLoadAfterSolutionOpened()
        {
            ThreadHelper.JoinableTaskFactory.Run(viewModel.LoadAsync);
            winFormsControl.RefreshAll();

            solutionLoadRetryCount++;
            if (viewModel.IsLoaded || solutionLoadRetryCount >= 30)
            {
                solutionLoadRetryTimer.Stop();
            }
        }
    }
}
