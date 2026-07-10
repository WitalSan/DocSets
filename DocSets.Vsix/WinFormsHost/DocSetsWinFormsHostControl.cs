using Microsoft.VisualStudio.Shell;
using System;
using System.Windows;
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
        private bool workspaceSyncInProgress;
        private int solutionLoadRetryCount;

        public DocSetsWinFormsHostControl(AsyncPackage package)
        {
            viewModel = new DocSetsViewModel(package, () => Window.GetWindow(this));
            winFormsControl = new DocSetsWinFormsControl(viewModel);
            Child = winFormsControl;

            solutionLoadRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            solutionLoadRetryTimer.Tick += (_, __) => RetryLoadAfterSolutionOpened();

            workspaceSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            workspaceSyncTimer.Tick += async (_, __) => await CheckWorkspaceChangesAsync();

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
            };

            Unloaded += (_, __) =>
            {
                solutionLoadRetryTimer.Stop();
                workspaceSyncTimer.Stop();
            };
        }

        internal async System.Threading.Tasks.Task AddBookmarkFromEditorAsync()
        {
            await EnsureLoadedAsync();
            await winFormsControl.AddBookmarkFromEditorAsync();
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
