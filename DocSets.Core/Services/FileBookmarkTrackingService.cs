using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DocSets
{
    /// <summary>
    /// Runtime service for BookmarkType.File.
    ///
    /// Important: File bookmarks are not fixed tracking points now.
    /// They store the last active caret position in their file.
    /// If the user opens the file through the bookmark and then moves the caret,
    /// Line/Column are updated to that caret position.
    /// </summary>
    internal sealed class FileBookmarkTrackingService
    {
        private readonly AsyncPackage package;
        private readonly Func<string, string> toFullPath;
        private readonly Dictionary<DocumentItem, ViewEntry> entries = new Dictionary<DocumentItem, ViewEntry>();
        private readonly Dictionary<IWpfTextView, EventHandler<CaretPositionChangedEventArgs>> caretHandlers = new Dictionary<IWpfTextView, EventHandler<CaretPositionChangedEventArgs>>();
        private readonly Dictionary<IWpfTextView, EventHandler> closedHandlers = new Dictionary<IWpfTextView, EventHandler>();

        public FileBookmarkTrackingService(AsyncPackage package, Func<string, string> toFullPath)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            this.toFullPath = toFullPath ?? throw new ArgumentNullException(nameof(toFullPath));
        }

        public async Task TrackFromActiveDocumentAsync(DocumentItem item)
        {
            if (!CanTrack(item))
            {
                return;
            }

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var view = await GetActiveTextViewAsync();
            if (!IsViewForItem(view, item))
            {
                return;
            }

            Track(item, view);
            UpdateItemFromCaret(item, view);
        }

        public async Task TrackAfterOpenAsync(DocumentItem item)
        {
            await TrackFromActiveDocumentAsync(item);
        }

        public async Task UpdateTrackedPositionsAsync(IEnumerable<DocumentItem> items)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var alive = new HashSet<DocumentItem>(items?.Where(CanTrack) ?? Enumerable.Empty<DocumentItem>());

            // If the active document is one of tracked file-bookmark files,
            // bind its bookmarks to the active view and read the current caret.
            var activeView = await GetActiveTextViewAsync();
            if (activeView != null)
            {
                foreach (var item in alive.Where(x => IsViewForItem(activeView, x)))
                {
                    Track(item, activeView);
                    UpdateItemFromCaret(item, activeView);
                }
            }

            // Keep already attached file bookmarks up-to-date from their views.
            foreach (var item in alive)
            {
                if (entries.TryGetValue(item, out var entry))
                {
                    UpdateItemFromCaret(item, entry.View);
                }
            }

            foreach (var key in entries.Keys.Where(x => !alive.Contains(x)).ToList())
            {
                entries.Remove(key);
            }

            RemoveUnusedViewSubscriptions();
        }

        private void Track(DocumentItem item, IWpfTextView view)
        {
            if (item == null || view == null)
            {
                return;
            }

            entries[item] = new ViewEntry(view);
            EnsureSubscribed(view);
        }

        private void EnsureSubscribed(IWpfTextView view)
        {
            if (view == null || caretHandlers.ContainsKey(view))
            {
                return;
            }

            EventHandler<CaretPositionChangedEventArgs> caretHandler = (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                UpdateItemsForView(view);
            };

            EventHandler closedHandler = (sender, args) =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                RemoveView(view);
            };

            view.Caret.PositionChanged += caretHandler;
            view.Closed += closedHandler;
            caretHandlers[view] = caretHandler;
            closedHandlers[view] = closedHandler;
        }

        private void UpdateItemsForView(IWpfTextView view)
        {
            if (view == null)
            {
                return;
            }

            foreach (var pair in entries.ToList())
            {
                if (ReferenceEquals(pair.Value.View, view))
                {
                    UpdateItemFromCaret(pair.Key, view);
                }
            }
        }

        private void UpdateItemFromCaret(DocumentItem item, IWpfTextView view)
        {
            if (!CanTrack(item) || view == null || view.IsClosed)
            {
                return;
            }

            try
            {
                var point = view.Caret.Position.BufferPosition;
                var line = point.GetContainingLine();

                item.Line = line.LineNumber + 1;
                item.Column = Math.Max(1, point.Position - line.Start.Position + 1);
            }
            catch
            {
                // The view may be closing/disconnected. Keep the last known position.
            }
        }

        private void RemoveView(IWpfTextView view)
        {
            if (view == null)
            {
                return;
            }

            if (caretHandlers.TryGetValue(view, out var caretHandler))
            {
                view.Caret.PositionChanged -= caretHandler;
                caretHandlers.Remove(view);
            }

            if (closedHandlers.TryGetValue(view, out var closedHandler))
            {
                view.Closed -= closedHandler;
                closedHandlers.Remove(view);
            }

            foreach (var key in entries.Where(x => ReferenceEquals(x.Value.View, view)).Select(x => x.Key).ToList())
            {
                entries.Remove(key);
            }
        }

        private void RemoveUnusedViewSubscriptions()
        {
            var usedViews = new HashSet<IWpfTextView>(entries.Values.Select(x => x.View));
            foreach (var view in caretHandlers.Keys.Where(x => !usedViews.Contains(x) || x.IsClosed).ToList())
            {
                RemoveView(view);
            }
        }

        private bool IsViewForItem(IWpfTextView view, DocumentItem item)
        {
            if (view == null || !CanTrack(item))
            {
                return false;
            }

            var filePath = GetTextBufferPath(view.TextBuffer);
            var itemFullPath = toFullPath(item.Path);
            return PathsEqual(filePath, itemFullPath);
        }

        private static bool CanTrack(DocumentItem item)
        {
            return item != null && !item.IsFolder && item.Type == BookmarkType.File;
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

        private static string GetTextBufferPath(ITextBuffer buffer)
        {
            if (buffer == null)
            {
                return string.Empty;
            }

            if (buffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument document))
            {
                return document.FilePath ?? string.Empty;
            }

            return string.Empty;
        }

        private static bool PathsEqual(string left, string right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            try
            {
                left = Path.GetFullPath(left);
                right = Path.GetFullPath(right);
            }
            catch
            {
                // compare original values below
            }

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class ViewEntry
        {
            public ViewEntry(IWpfTextView view)
            {
                View = view;
            }

            public IWpfTextView View { get; }
        }
    }
}
