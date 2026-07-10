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

        public Task<EditorState> CaptureAsync(int anchorLine)
        {
            return CaptureAsync(anchorLine, anchorLine, anchorLine + 5);
        }

        public async Task<EditorState> CaptureAsync(int anchorLine, int previewStartLine, int previewEndLine)
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
                state.SelectedText = span.GetText();
                state.CodePreview = state.SelectedText;
            }
            else
            {
                state.CodePreview = GetLineRangeText(snapshot, previewStartLine, previewEndLine);
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
                SnapshotPoint start;
                SnapshotPoint end;
                if (!TryFindSelectedText(snapshot, state.SelectedText, anchorLine, state.SelectionStartLineOffset, out start, out end))
                {
                    start = GetPoint(snapshot, anchorLine + state.SelectionStartLineOffset, state.SelectionStartColumn);
                    end = GetPoint(snapshot, anchorLine + state.SelectionEndLineOffset, state.SelectionEndColumn);
                }

                if (end.Position < start.Position)
                {
                    var temp = start;
                    start = end;
                    end = temp;
                }

                view.Selection.Select(new SnapshotSpan(start, end), false);
                view.Caret.MoveTo(end);
            }
            else
            {
                view.Selection.Clear();
            }

            var visiblePoint = GetPoint(snapshot, anchorLine + state.FirstVisibleLineOffset, 1);
            view.DisplayTextLineContainingBufferPosition(visiblePoint, 0.0, ViewRelativePosition.Top);
        }


        private static bool TryFindSelectedText(ITextSnapshot snapshot, string selectedText, int anchorLine, int expectedOffset, out SnapshotPoint start, out SnapshotPoint end)
        {
            start = default(SnapshotPoint);
            end = default(SnapshotPoint);
            if (snapshot == null || string.IsNullOrWhiteSpace(selectedText) || selectedText.Length < 3)
            {
                return false;
            }

            var expectedLine = Math.Max(1, anchorLine + expectedOffset);
            var firstLine = Math.Max(0, expectedLine - 1 - 300);
            var lastLine = Math.Min(snapshot.LineCount - 1, expectedLine - 1 + 300);
            var rangeStart = snapshot.GetLineFromLineNumber(firstLine).Start.Position;
            var rangeEnd = snapshot.GetLineFromLineNumber(lastLine).EndIncludingLineBreak.Position;
            var source = snapshot.GetText(rangeStart, rangeEnd - rangeStart);

            int normalizedStart;
            int normalizedLength;
            if (!TryFindIgnoringWhitespace(source, selectedText, out normalizedStart, out normalizedLength))
            {
                return false;
            }

            start = new SnapshotPoint(snapshot, rangeStart + normalizedStart);
            end = new SnapshotPoint(snapshot, Math.Min(snapshot.Length, rangeStart + normalizedStart + normalizedLength));
            return true;
        }

        private static bool TryFindIgnoringWhitespace(string source, string target, out int sourceStart, out int sourceLength)
        {
            sourceStart = 0;
            sourceLength = 0;
            var normalizedTarget = NormalizeWhitespace(target, null);
            if (normalizedTarget.Length == 0)
            {
                return false;
            }

            var map = new System.Collections.Generic.List<int>();
            var normalizedSource = NormalizeWhitespace(source, map);
            var index = normalizedSource.IndexOf(normalizedTarget, StringComparison.Ordinal);
            if (index < 0 || index >= map.Count)
            {
                return false;
            }

            var lastNormalizedIndex = Math.Min(map.Count - 1, index + normalizedTarget.Length - 1);
            sourceStart = map[index];
            sourceLength = Math.Max(1, map[lastNormalizedIndex] - sourceStart + 1);
            return true;
        }

        private static string NormalizeWhitespace(string text, System.Collections.Generic.List<int> map)
        {
            var builder = new System.Text.StringBuilder(text?.Length ?? 0);
            var inWhitespace = false;
            for (var i = 0; i < (text?.Length ?? 0); i++)
            {
                var ch = text[i];
                if (char.IsWhiteSpace(ch))
                {
                    if (!inWhitespace && builder.Length > 0)
                    {
                        builder.Append(' ');
                        map?.Add(i);
                    }
                    inWhitespace = true;
                }
                else
                {
                    builder.Append(ch);
                    map?.Add(i);
                    inWhitespace = false;
                }
            }

            return builder.ToString().Trim();
        }

        private static string GetLineRangeText(ITextSnapshot snapshot, int oneBasedStartLine, int oneBasedEndLine)
        {
            if (snapshot == null || snapshot.LineCount == 0)
            {
                return string.Empty;
            }

            var startLineNumber = Math.Max(0, Math.Min(snapshot.LineCount - 1, oneBasedStartLine - 1));
            var endLineNumber = Math.Max(startLineNumber, Math.Min(snapshot.LineCount - 1, oneBasedEndLine - 1));
            var start = snapshot.GetLineFromLineNumber(startLineNumber).Start.Position;
            var end = snapshot.GetLineFromLineNumber(endLineNumber).End.Position;
            return snapshot.GetText(start, Math.Max(0, end - start));
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
