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
        private int solutionLoadRetryCount;

        public DocSetsWinFormsHostControl(AsyncPackage package)
        {
            viewModel = new DocSetsViewModel(package, () => Window.GetWindow(this));
            winFormsControl = new DocSetsWinFormsControl(viewModel);
            Child = winFormsControl;

            solutionLoadRetryTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            solutionLoadRetryTimer.Tick += (_, __) => RetryLoadAfterSolutionOpened();

            Loaded += (_, __) =>
            {
                ThreadHelper.JoinableTaskFactory.Run(viewModel.LoadAsync);
                winFormsControl.RefreshAll();

                if (!viewModel.IsLoaded)
                {
                    solutionLoadRetryCount = 0;
                    solutionLoadRetryTimer.Start();
                }
            };
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
