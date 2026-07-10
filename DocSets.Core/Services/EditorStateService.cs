using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Threading.Tasks;

namespace DocSets
{
    internal sealed class EditorStateService
    {
        private readonly AsyncPackage package;

        public EditorStateService(AsyncPackage package)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
        }

        public async Task<EditorState> CaptureAsync(int anchorLine)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = await GetActiveTextViewAsync();
            if (view == null)
            {
                return null;
            }

            var snapshot = view.TextSnapshot;
            var caret = view.Caret.Position.BufferPosition;
            var caretLine = caret.GetContainingLine();
            var state = new EditorState
            {
                CaretLineOffset = caretLine.LineNumber + 1 - Math.Max(1, anchorLine),
                CaretColumn = caret.Position - caretLine.Start.Position + 1
            };

            if (!view.Selection.IsEmpty)
            {
                var span = view.Selection.StreamSelectionSpan.SnapshotSpan;
                var startLine = span.Start.GetContainingLine();
                var endLine = span.End.GetContainingLine();
                state.HasSelection = true;
                state.SelectionStartLineOffset = startLine.LineNumber + 1 - Math.Max(1, anchorLine);
                state.SelectionStartColumn = span.Start.Position - startLine.Start.Position + 1;
                state.SelectionEndLineOffset = endLine.LineNumber + 1 - Math.Max(1, anchorLine);
                state.SelectionEndColumn = span.End.Position - endLine.Start.Position + 1;
            }

            var firstVisible = view.TextViewLines?.FirstVisibleLine;
            if (firstVisible != null)
            {
                state.FirstVisibleLineOffset = firstVisible.Start.GetContainingLine().LineNumber + 1 - Math.Max(1, anchorLine);
            }

            return state;
        }

        public async Task RestoreAsync(EditorState state, int anchorLine)
        {
            if (state == null)
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = await GetActiveTextViewAsync();
            if (view == null)
            {
                return;
            }

            var snapshot = view.TextSnapshot;
            var caretPoint = GetPoint(snapshot, anchorLine + state.CaretLineOffset, state.CaretColumn);
            view.Caret.MoveTo(caretPoint);

            if (state.HasSelection)
            {
                var start = GetPoint(snapshot, anchorLine + state.SelectionStartLineOffset, state.SelectionStartColumn);
                var end = GetPoint(snapshot, anchorLine + state.SelectionEndLineOffset, state.SelectionEndColumn);
                if (end.Position < start.Position)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }

                view.Selection.Select(new SnapshotSpan(start, end), false);
            }
            else
            {
                view.Selection.Clear();
            }

            var visiblePoint = GetPoint(snapshot, anchorLine + state.FirstVisibleLineOffset, 1);
            view.DisplayTextLineContainingBufferPosition(visiblePoint, 0.0, ViewRelativePosition.Top);
        }

        private static SnapshotPoint GetPoint(ITextSnapshot snapshot, int oneBasedLine, int oneBasedColumn)
        {
            if (snapshot == null || snapshot.LineCount == 0)
            {
                return new SnapshotPoint(snapshot, 0);
            }

            var lineNumber = Math.Max(0, Math.Min(snapshot.LineCount - 1, oneBasedLine - 1));
            var line = snapshot.GetLineFromLineNumber(lineNumber);
            var column = Math.Max(0, Math.Min(line.Length, oneBasedColumn - 1));
            return line.Start + column;
        }

        private async Task<IWpfTextView> GetActiveTextViewAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var textManager = await package.GetServiceAsync(typeof(SVsTextManager)) as IVsTextManager;
            if (textManager == null)
            {
                return null;
            }

            textManager.GetActiveView(1, null, out var textView);
            if (textView == null)
            {
                textManager.GetActiveView(0, null, out textView);
            }

            if (textView == null)
            {
                return null;
            }

            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            var adapterFactory = componentModel?.GetService<IVsEditorAdaptersFactoryService>();
            return adapterFactory?.GetWpfTextView(textView);
        }
    }
}
