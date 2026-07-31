using System;
using System.Windows;

namespace DocSets
{
    /// <summary>
    /// Диалоги DocSets с корректным владельцем окна Visual Studio.
    /// </summary>
    internal sealed class VisualStudioUserDialogService : IUserDialogService
    {
        private readonly Func<Window> _ownerAccessor;

        public VisualStudioUserDialogService(Func<Window> ownerAccessor)
        {
            _ownerAccessor = ownerAccessor ?? (() => null);
        }

        public string Prompt(string caption, string label, string initialValue = "")
            => PromptDialog.Ask(null, caption, label, initialValue);

        public bool Confirm(string message, string caption)
            => MessageBox.Show(
                _ownerAccessor(), message, caption,
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

        public void ShowInformation(string message, string caption = "DocSets")
            => MessageBox.Show(
                _ownerAccessor(), message ?? string.Empty, caption,
                MessageBoxButton.OK, MessageBoxImage.Information);

        public void ShowError(string message, string caption = "DocSets")
            => MessageBox.Show(
                _ownerAccessor(), message ?? string.Empty, caption,
                MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
