using System.Diagnostics;

namespace DocSets.Desktop;

internal sealed class DesktopUserDialogService : IUserDialogService
{
    public string Prompt(string caption, string label, string initialValue = "")
        => PromptDialog.Ask(Form.ActiveForm, caption, label, initialValue);

    public bool Confirm(string message, string caption)
        => MessageBox.Show(Form.ActiveForm, message, caption,
            MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

    public void ShowInformation(string message, string caption = "DocSets")
        => MessageBox.Show(Form.ActiveForm, message ?? string.Empty, caption,
            MessageBoxButtons.OK, MessageBoxIcon.Information);

    public void ShowError(string message, string caption = "DocSets")
        => MessageBox.Show(Form.ActiveForm, message ?? string.Empty, caption,
            MessageBoxButtons.OK, MessageBoxIcon.Error);
}

internal sealed class DesktopClipboardService : IClipboardService
{
    public bool TryGetText(out string text)
    {
        text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        return text != null;
    }

    public void SetText(string text)
    {
        if (!string.IsNullOrEmpty(text)) Clipboard.SetText(text);
    }
}

internal sealed class DesktopNavigationService : INavigationService
{
    private readonly Func<DocumentItem, string> _pathResolver;

    public DesktopNavigationService(Func<DocumentItem, string> pathResolver)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    public Task OpenBookmarkAsync(DocumentItem item)
    {
        var path = _pathResolver(item);
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task<bool> OpenSymbolAsync(string symbol, string project)
        => Task.FromResult(false);

    public Task OpenUrlAsync(string url)
    {
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return Task.CompletedTask;
    }
}

internal sealed class DesktopActiveDocumentService : IActiveDocumentService
{
    public Task<DocumentItem> CreateBookmarkAsync() => Task.FromResult<DocumentItem>(null);
    public Task<DocumentItem> CreateClassBookmarkAsync() => Task.FromResult<DocumentItem>(null);
    public Task<ActiveDocumentContext> GetContextAsync() => Task.FromResult<ActiveDocumentContext>(null);
    public Task<ActiveSymbolReference> GetSymbolReferenceAsync(string selectedText)
        => Task.FromResult<ActiveSymbolReference>(null);
}

internal sealed class DesktopPreviewService : IPreviewService
{
    private readonly Func<DocumentItem, string> _pathResolver;

    public DesktopPreviewService(Func<DocumentItem, string> pathResolver)
    {
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
    }

    public async Task<string> GetPreviewAsync(
        DocumentItem item, CancellationToken cancellationToken)
    {
        var path = _pathResolver(item);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return string.Empty;
        var lines = await File.ReadAllLinesAsync(path, cancellationToken);
        var start = Math.Max(0, Math.Min(lines.Length, item?.Line - 1 ?? 0));
        return string.Join(Environment.NewLine, lines.Skip(start).Take(20));
    }
}

internal sealed class DesktopEditorTrackingService : IEditorTrackingService
{
    public Task TrackFromActiveDocumentAsync(DocumentItem item) => Task.CompletedTask;
    public Task TrackAfterOpenAsync(DocumentItem item) => Task.CompletedTask;
    public Task UpdateTrackedPositionsAsync(IEnumerable<DocumentItem> items) => Task.CompletedTask;
}
