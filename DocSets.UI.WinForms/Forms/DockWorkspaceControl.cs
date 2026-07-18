using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace DocSets
{
    internal sealed class DockWorkspaceControl : UserControl, IMessageFilter
    {
        internal const string DockPanelDragFormat = "DocSets.DockPanel";
        private readonly ToolStrip toolbar = new ToolStrip();
        private readonly ToolStripDropDownButton panelsButton = new ToolStripDropDownButton("Панели");
        private readonly Panel host = new Panel();
        private readonly Dictionary<string, DockPanelRegistration> registrations = new Dictionary<string, DockPanelRegistration>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> lastGroupByPanel = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Control> dragSurfaces = new List<Control>();
        private DockLayoutNode root;
        private string draggedPanelId;
        private Point dragStart;
        private DockDropIndicatorWindow dropIndicator;
        private SplitterIndicatorWindow splitterIndicator;
        private SplitContainer activeSplitter;
        private Cursor activeSplitterCursor;
        private string selectedPanelId;
        private bool rebuilding;

        public event EventHandler LayoutStateChanged;
        public event EventHandler SelectedPanelChanged;

        public DockWorkspaceControl()
        {
            Dock = DockStyle.Fill;
            toolbar.Dock = DockStyle.Top;
            toolbar.GripStyle = ToolStripGripStyle.Hidden;
            toolbar.Items.Add(panelsButton);
            host.Dock = DockStyle.Fill;
            Controls.Add(host);
            Controls.Add(toolbar);
            panelsButton.DropDownOpening += (_, __) => BuildPanelsMenu();
        }

        public string SelectedPanelId => selectedPanelId ?? string.Empty;
        public int GroupCount => EnumerateGroups(root).Count(x => x.PanelIds.Count > 0);

        public void Register(string id, string title, Control content, bool visibleByDefault = true)
        {
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Panel id is required.", nameof(id));
            if (content == null) throw new ArgumentNullException(nameof(content));
            registrations[id] = new DockPanelRegistration(id, title, content, visibleByDefault);
            if (visibleByDefault && FindPanelGroup(id) == null)
            {
                var group = EnsureDefaultGroup();
                group.PanelIds.Add(id);
                if (string.IsNullOrWhiteSpace(group.ActivePanelId)) group.ActivePanelId = id;
                if (string.IsNullOrWhiteSpace(selectedPanelId)) selectedPanelId = id;
            }
            Rebuild();
        }

        public bool ContainsPanel(string id) => registrations.ContainsKey(id);
        public bool IsPanelVisible(string id) => FindPanelGroup(id) != null;
        public bool IsPanelDisplayed(string id)
        {
            var group = FindPanelGroup(id);
            return group != null && string.Equals(group.ActivePanelId, id, StringComparison.OrdinalIgnoreCase);
        }

        public void ActivatePanel(string id)
        {
            if (!registrations.ContainsKey(id)) return;
            var group = FindPanelGroup(id);
            if (group == null)
            {
                group = FindRestoreGroup(id) ?? EnsureDefaultGroup();
                group.PanelIds.Add(id);
                Rebuild();
                OnLayoutChanged();
            }
            group.ActivePanelId = id;
            selectedPanelId = id;
            var tabs = EnumerateTabs(host).FirstOrDefault(x => string.Equals(x.Tag as string, group.Id, StringComparison.OrdinalIgnoreCase));
            var page = tabs?.TabPages.Cast<TabPage>().FirstOrDefault(x => string.Equals(x.Tag as string, id, StringComparison.OrdinalIgnoreCase));
            if (page != null) tabs.SelectedTab = page;
            SelectedPanelChanged?.Invoke(this, EventArgs.Empty);
        }

        public void HidePanel(string id)
        {
            var group = FindPanelGroup(id);
            if (group == null) return;
            lastGroupByPanel[id] = group.Id;
            group.PanelIds.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(group.ActivePanelId, id, StringComparison.OrdinalIgnoreCase)) group.ActivePanelId = group.PanelIds.FirstOrDefault();
            root = Normalize(root);
            selectedPanelId = EnumerateGroups(root).Select(x => x.ActivePanelId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
            Rebuild();
            OnLayoutChanged();
        }

        public void ResetLayout()
        {
            lastGroupByPanel.Clear();
            var group = NewGroup();
            foreach (var registration in registrations.Values.Where(x => x.VisibleByDefault)) group.PanelIds.Add(registration.Id);
            group.ActivePanelId = group.PanelIds.FirstOrDefault();
            selectedPanelId = group.ActivePanelId;
            root = group;
            Rebuild();
            OnLayoutChanged();
        }

        public string CaptureLayout()
        {
            CaptureSplitRatios(host);
            var state = new DockWorkspaceState
            {
                Version = 2,
                Root = root?.Clone(),
                Titles = registrations.Values.Where(x => !string.IsNullOrWhiteSpace(x.CustomTitle))
                    .ToDictionary(x => x.Id, x => x.CustomTitle, StringComparer.OrdinalIgnoreCase),
                LastGroups = new Dictionary<string, string>(lastGroupByPanel, StringComparer.OrdinalIgnoreCase)
            };
            return JsonConvert.SerializeObject(state);
        }

        public void RestoreLayout(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) { ResetLayout(); return; }
            try
            {
                var state = JsonConvert.DeserializeObject<DockWorkspaceState>(json);
                root = state?.Root != null ? Sanitize(state.Root) : ConvertLegacyGroups(state?.Groups);
                lastGroupByPanel.Clear();
                foreach (var pair in state?.LastGroups ?? new Dictionary<string, string>()) lastGroupByPanel[pair.Key] = pair.Value;
                foreach (var pair in state?.Titles ?? new Dictionary<string, string>())
                    if (registrations.TryGetValue(pair.Key, out var registration)) registration.CustomTitle = pair.Value;
                root = Normalize(root);
                if (root == null) ResetLayout();
                else
                {
                    selectedPanelId = EnumerateGroups(root).Select(x => x.ActivePanelId).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                        ?? EnumerateGroups(root).SelectMany(x => x.PanelIds).FirstOrDefault();
                    Rebuild();
                }
            }
            catch (JsonException) { ResetLayout(); }
        }

        private DockLayoutNode EnsureDefaultGroup()
        {
            var group = EnumerateGroups(root).FirstOrDefault();
            if (group != null) return group;
            group = NewGroup();
            root = group;
            return group;
        }

        private static DockLayoutNode NewGroup() => new DockLayoutNode { Type = "tabs", Id = Guid.NewGuid().ToString("N") };

        private DockLayoutNode FindPanelGroup(string panelId) => EnumerateGroups(root)
            .FirstOrDefault(x => x.PanelIds.Contains(panelId, StringComparer.OrdinalIgnoreCase));

        private DockLayoutNode FindRestoreGroup(string panelId)
        {
            return lastGroupByPanel.TryGetValue(panelId, out var groupId)
                ? EnumerateGroups(root).FirstOrDefault(x => string.Equals(x.Id, groupId, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        private void BuildPanelsMenu()
        {
            panelsButton.DropDownItems.Clear();
            foreach (var registration in registrations.Values)
            {
                var visible = IsPanelVisible(registration.Id);
                var item = new ToolStripMenuItem(registration.Title) { Checked = visible, Tag = registration.Id };
                item.Click += (_, __) => ActivatePanel(registration.Id);
                panelsButton.DropDownItems.Add(item);
            }
            panelsButton.DropDownItems.Add(new ToolStripSeparator());
            var reset = new ToolStripMenuItem("Сбросить расположение");
            reset.Click += (_, __) => ResetLayout();
            panelsButton.DropDownItems.Add(reset);
        }

        private void Rebuild()
        {
            if (rebuilding) return;
            rebuilding = true;
            try
            {
                ClearDropIndicator();
                foreach (var registration in registrations.Values) registration.Content.Parent?.Controls.Remove(registration.Content);
                var old = host.Controls.Cast<Control>().ToArray();
                host.Controls.Clear();
                foreach (var control in old) control.Dispose();
                if (root == null) return;
                var controlRoot = BuildNode(root);
                controlRoot.Dock = DockStyle.Fill;
                host.Controls.Add(controlRoot);
            }
            finally { rebuilding = false; }
        }

        private Control BuildNode(DockLayoutNode node)
        {
            if (node.IsTabs) return CreateTabs(node);
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = node.Orientation,
                Panel1MinSize = 0,
                Panel2MinSize = 0,
                SplitterWidth = DpiService.Scale(this, 5),
                Tag = node
            };
            split.Panel1.Controls.Add(BuildNode(node.First));
            split.Panel2.Controls.Add(BuildNode(node.Second));
            split.SizeChanged += (_, __) => ApplySplitterRatio(split, node.Ratio);
            split.SplitterMoving += Split_SplitterMoving;
            split.SplitterMoved += (_, __) =>
            {
                EndSplitterDrag();
                if (rebuilding) return;
                node.Ratio = GetSplitterRatio(split);
                OnLayoutChanged();
            };
            split.MouseCaptureChanged += (_, __) => { if (!split.Capture && ReferenceEquals(activeSplitter, split)) EndSplitterDrag(); };
            return split;
        }

        private void Split_SplitterMoving(object sender, SplitterCancelEventArgs e)
        {
            var split = (SplitContainer)sender;
            if (!ReferenceEquals(activeSplitter, split))
            {
                EndSplitterDrag();
                activeSplitter = split;
                activeSplitterCursor = split.Orientation == Orientation.Horizontal ? Cursors.HSplit : Cursors.VSplit;
                Application.AddMessageFilter(this);
            }

            ShowSplitterIndicator(split, e.SplitX, e.SplitY);
            Cursor.Current = activeSplitterCursor;
        }

        private void ShowSplitterIndicator(SplitContainer split, int splitX, int splitY)
        {
            Rectangle bounds;
            if (split.Orientation == Orientation.Horizontal)
                bounds = new Rectangle(0, Math.Max(0, splitY), split.ClientSize.Width, Math.Max(1, split.SplitterWidth));
            else
                bounds = new Rectangle(Math.Max(0, splitX), 0, Math.Max(1, split.SplitterWidth), split.ClientSize.Height);
            bounds = split.RectangleToScreen(bounds);
            if (splitterIndicator == null || splitterIndicator.IsDisposed)
                splitterIndicator = new SplitterIndicatorWindow();
            splitterIndicator.ShowAt(split.FindForm(), bounds);
        }

        private void ClearSplitterIndicator()
        {
            if (splitterIndicator == null) return;
            splitterIndicator.Close();
            splitterIndicator.Dispose();
            splitterIndicator = null;
        }

        private void EndSplitterDrag()
        {
            ClearSplitterIndicator();
            if (activeSplitter != null) Application.RemoveMessageFilter(this);
            activeSplitter = null;
            activeSplitterCursor = null;
        }

        public bool PreFilterMessage(ref Message message)
        {
            const int wmSetCursor = 0x20;
            const int wmMouseMove = 0x200;
            const int wmLButtonUp = 0x202;
            if (activeSplitter == null) return false;
            if (message.Msg == wmLButtonUp)
            {
                EndSplitterDrag();
                return false;
            }
            if (message.Msg == wmSetCursor || message.Msg == wmMouseMove)
            {
                Cursor.Current = activeSplitterCursor;
                return message.Msg == wmSetCursor;
            }
            return false;
        }
        internal static void ApplySplitterRatio(SplitContainer split, float ratio)
        {
            if (split == null || split.IsDisposed) return;
            split.Panel1MinSize = 0;
            split.Panel2MinSize = 0;
            var extent = split.Orientation == Orientation.Horizontal ? split.ClientSize.Height : split.ClientSize.Width;
            var available = extent - split.SplitterWidth;
            if (available <= 0) return;
            ratio = Math.Max(0F, Math.Min(1F, ratio));
            var distance = Math.Max(0, Math.Min(available, (int)Math.Round(available * ratio)));
            if (split.SplitterDistance != distance) split.SplitterDistance = distance;
        }

        private static float GetSplitterRatio(SplitContainer split)
        {
            var extent = split.Orientation == Orientation.Horizontal ? split.ClientSize.Height : split.ClientSize.Width;
            var available = extent - split.SplitterWidth;
            return available <= 0 ? .5F : Math.Max(0F, Math.Min(1F, split.SplitterDistance / (float)available));
        }

        private TabControl CreateTabs(DockLayoutNode group)
        {
            var tabs = new TabControl { Dock = DockStyle.Fill, AllowDrop = true, Tag = group.Id };
            foreach (var id in group.PanelIds.ToArray())
            {
                if (!registrations.TryGetValue(id, out var registration)) continue;
                var page = new TabPage(registration.DisplayTitle + "  ×") { Tag = id };
                registration.Content.Dock = DockStyle.Fill;
                page.Controls.Add(registration.Content);
                tabs.TabPages.Add(page);
            }
            var selected = tabs.TabPages.Cast<TabPage>().FirstOrDefault(x => string.Equals(x.Tag as string, group.ActivePanelId, StringComparison.OrdinalIgnoreCase));
            if (selected != null) tabs.SelectedTab = selected;
            tabs.SelectedIndexChanged += (_, __) =>
            {
                group.ActivePanelId = tabs.SelectedTab?.Tag as string;
                selectedPanelId = group.ActivePanelId;
                SelectedPanelChanged?.Invoke(this, EventArgs.Empty);
                OnLayoutChanged();
            };
            tabs.MouseDown += Tabs_MouseDown;
            tabs.MouseMove += Tabs_MouseMove;
            tabs.MouseUp += (_, e) =>
            {
                if (e.Button == MouseButtons.Middle) HideTabAt(tabs, e.Location);
                if (e.Button == MouseButtons.Left) draggedPanelId = null;
            };
            tabs.DragEnter += Tabs_DragOver;
            tabs.DragOver += Tabs_DragOver;
            tabs.DragLeave += (_, __) => ClearDropIndicator();
            tabs.DragDrop += Tabs_DragDrop;
            tabs.ContextMenuStrip = CreateTabMenu(tabs);
            return tabs;
        }

        private ContextMenuStrip CreateTabMenu(TabControl tabs)
        {
            var menu = new ContextMenuStrip();
            menu.Opening += (_, e) =>
            {
                menu.Items.Clear();
                var page = GetTabAt(tabs, tabs.PointToClient(Cursor.Position)) ?? tabs.SelectedTab;
                if (page == null) { e.Cancel = true; return; }
                var id = page.Tag as string;
                var rename = new ToolStripMenuItem("Переименовать…");
                rename.Click += (_, __) => RenamePanel(id);
                var resetName = new ToolStripMenuItem("Сбросить название") { Enabled = registrations.TryGetValue(id, out var r) && !string.IsNullOrWhiteSpace(r.CustomTitle) };
                resetName.Click += (_, __) => { if (registrations.TryGetValue(id, out var registration)) { registration.CustomTitle = null; Rebuild(); OnLayoutChanged(); } };
                var hide = new ToolStripMenuItem("Скрыть");
                hide.Click += (_, __) => HidePanel(id);
                menu.Items.Add(rename); menu.Items.Add(resetName); menu.Items.Add(new ToolStripSeparator()); menu.Items.Add(hide);
            };
            return menu;
        }

        private void RenamePanel(string id)
        {
            if (!registrations.TryGetValue(id, out var registration)) return;
            using (var dialog = new Form { Text = "Название вкладки", StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8) })
            using (var text = new TextBox { Text = registration.DisplayTitle, Width = DpiService.Scale(this, 260) })
            using (var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true })
            using (var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true })
            {
                var layout = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
                var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
                buttons.Controls.Add(ok); buttons.Controls.Add(cancel); layout.Controls.Add(text); layout.Controls.Add(buttons); dialog.Controls.Add(layout);
                dialog.AcceptButton = ok; dialog.CancelButton = cancel;
                if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
                registration.CustomTitle = string.IsNullOrWhiteSpace(text.Text) ? null : text.Text.Trim();
                Rebuild(); OnLayoutChanged();
            }
        }

        private void Tabs_MouseDown(object sender, MouseEventArgs e)
        {
            var tabs = (TabControl)sender;
            if (e.Button == MouseButtons.Left && TryGetClosePanel(tabs, e.Location, out var closePanelId))
            {
                draggedPanelId = null; HidePanel(closePanelId); return;
            }
            if (e.Button != MouseButtons.Left) { draggedPanelId = null; return; }
            dragStart = e.Location;
            draggedPanelId = GetTabAt(tabs, e.Location)?.Tag as string;
        }

        private bool TryGetClosePanel(TabControl tabs, Point point, out string panelId)
        {
            panelId = null;
            for (var index = 0; index < tabs.TabPages.Count; index++)
            {
                var bounds = tabs.GetTabRect(index);
                if (!bounds.Contains(point)) continue;
                var closeWidth = Math.Min(bounds.Width, DpiService.Scale(this, 24));
                if (!new Rectangle(bounds.Right - closeWidth, bounds.Top, closeWidth, bounds.Height).Contains(point)) return false;
                panelId = tabs.TabPages[index].Tag as string;
                return !string.IsNullOrWhiteSpace(panelId);
            }
            return false;
        }

        private void Tabs_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left || string.IsNullOrWhiteSpace(draggedPanelId)) return;
            var dragSize = SystemInformation.DragSize;
            if (new Rectangle(dragStart.X - dragSize.Width / 2, dragStart.Y - dragSize.Height / 2, dragSize.Width, dragSize.Height).Contains(e.Location)) return;
            var panelId = draggedPanelId;
            draggedPanelId = null;
            InstallDragSurfaces();
            try { ((TabControl)sender).DoDragDrop(new DataObject(DockPanelDragFormat, panelId), DragDropEffects.Move); }
            finally
            {
                ClearDropIndicator();
                RemoveDragSurfaces();
            }
        }

        private void InstallDragSurfaces()
        {
            RemoveDragSurfaces();
            foreach (var tabs in EnumerateTabs(host).ToArray())
            {
                var page = tabs.SelectedTab;
                if (page == null) continue;
                var surface = new DockDragSurface { Dock = DockStyle.Fill, AllowDrop = true, Tag = tabs };
                surface.DragEnter += DragSurface_DragOver;
                surface.DragOver += DragSurface_DragOver;
                surface.DragLeave += (_, __) => ClearDropIndicator();
                surface.DragDrop += DragSurface_DragDrop;
                page.Controls.Add(surface);
                surface.BringToFront();
                dragSurfaces.Add(surface);
            }
        }

        private void RemoveDragSurfaces()
        {
            foreach (var surface in dragSurfaces.ToArray())
            {
                if (surface == null || surface.IsDisposed) continue;
                surface.Parent?.Controls.Remove(surface);
                surface.Dispose();
            }
            dragSurfaces.Clear();
        }

        private void DragSurface_DragOver(object sender, DragEventArgs e)
        {
            var tabs = (sender as Control)?.Tag as TabControl;
            if (tabs == null || tabs.IsDisposed) { e.Effect = DragDropEffects.None; ClearDropIndicator(); return; }
            Tabs_DragOver(tabs, e);
        }

        private void DragSurface_DragDrop(object sender, DragEventArgs e)
        {
            var tabs = (sender as Control)?.Tag as TabControl;
            if (tabs == null || tabs.IsDisposed) { e.Effect = DragDropEffects.None; ClearDropIndicator(); return; }
            Tabs_DragDrop(tabs, e);
        }
        private void Tabs_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DockPanelDragFormat)) { e.Effect = DragDropEffects.None; ClearDropIndicator(); return; }
            e.Effect = DragDropEffects.Move;
            var tabs = (TabControl)sender;
            ShowDropIndicator(tabs, tabs.PointToClient(new Point(e.X, e.Y)));
        }

        private void ShowDropIndicator(TabControl tabs, Point point)
        {
            var client = tabs.ClientRectangle;
            if (client.Width <= 0 || client.Height <= 0) { ClearDropIndicator(); return; }
            var zone = GetDropZone(client, point);
            Rectangle target;
            switch (zone)
            {
                case DockDropZone.Top: target = new Rectangle(0, 0, client.Width, Math.Max(1, client.Height / 2)); break;
                case DockDropZone.Bottom: target = new Rectangle(0, client.Height / 2, client.Width, Math.Max(1, client.Height - client.Height / 2)); break;
                case DockDropZone.Left: target = new Rectangle(0, 0, Math.Max(1, client.Width / 2), client.Height); break;
                case DockDropZone.Right: target = new Rectangle(client.Width / 2, 0, Math.Max(1, client.Width - client.Width / 2), client.Height); break;
                default: target = client; break;
            }
            target = tabs.RectangleToScreen(target); target.Inflate(-2, -2);
            if (target.Width <= 0 || target.Height <= 0) { ClearDropIndicator(); return; }
            if (dropIndicator == null || dropIndicator.IsDisposed)
                dropIndicator = new DockDropIndicatorWindow();
            dropIndicator.ShowAt(tabs.FindForm(), target);
        }

        private DockDropZone GetDropZone(Rectangle client, Point point)
        {
            var verticalEdge = Math.Max(DpiService.Scale(this, 28), client.Height / 4);
            var horizontalEdge = Math.Max(DpiService.Scale(this, 28), client.Width / 4);
            var candidates = new List<Tuple<DockDropZone, int>>();
            if (point.Y < verticalEdge) candidates.Add(Tuple.Create(DockDropZone.Top, Math.Max(0, point.Y)));
            if (point.Y > client.Height - verticalEdge) candidates.Add(Tuple.Create(DockDropZone.Bottom, Math.Max(0, client.Height - point.Y)));
            if (point.X < horizontalEdge) candidates.Add(Tuple.Create(DockDropZone.Left, Math.Max(0, point.X)));
            if (point.X > client.Width - horizontalEdge) candidates.Add(Tuple.Create(DockDropZone.Right, Math.Max(0, client.Width - point.X)));
            return candidates.OrderBy(x => x.Item2).Select(x => x.Item1).FirstOrDefault();
        }

        private void ClearDropIndicator()
        {
            if (dropIndicator == null) return;
            dropIndicator.Close();
            dropIndicator.Dispose();
            dropIndicator = null;
        }

        private void Tabs_DragDrop(object sender, DragEventArgs e)
        {
            ClearDropIndicator();
            if (!e.Data.GetDataPresent(DockPanelDragFormat)) return;
            var id = e.Data.GetData(DockPanelDragFormat) as string;
            var tabs = (TabControl)sender;
            var target = EnumerateGroups(root).FirstOrDefault(x => string.Equals(x.Id, tabs.Tag as string, StringComparison.OrdinalIgnoreCase));
            var source = FindPanelGroup(id);
            if (target == null || source == null) return;
            var zone = GetDropZone(tabs.ClientRectangle, tabs.PointToClient(new Point(e.X, e.Y)));
            if (ReferenceEquals(source, target) && source.PanelIds.Count == 1 && zone != DockDropZone.Center) return;

            source.PanelIds.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            if (string.Equals(source.ActivePanelId, id, StringComparison.OrdinalIgnoreCase)) source.ActivePanelId = source.PanelIds.FirstOrDefault();
            if (zone == DockDropZone.Center)
            {
                target.PanelIds.Add(id);
                target.ActivePanelId = id;
            }
            else
            {
                var newGroup = NewGroup();
                newGroup.PanelIds.Add(id); newGroup.ActivePanelId = id;
                var split = new DockLayoutNode
                {
                    Type = "split",
                    Id = Guid.NewGuid().ToString("N"),
                    Orientation = zone == DockDropZone.Left || zone == DockDropZone.Right ? Orientation.Vertical : Orientation.Horizontal,
                    Ratio = .5F
                };
                var newFirst = zone == DockDropZone.Left || zone == DockDropZone.Top;
                split.First = newFirst ? newGroup : target;
                split.Second = newFirst ? target : newGroup;
                root = ReplaceNode(root, target, split);
            }
            root = Normalize(root);
            Rebuild(); ActivatePanel(id); OnLayoutChanged();
        }

        private static DockLayoutNode ReplaceNode(DockLayoutNode current, DockLayoutNode target, DockLayoutNode replacement)
        {
            if (ReferenceEquals(current, target)) return replacement;
            if (current == null || current.IsTabs) return current;
            current.First = ReplaceNode(current.First, target, replacement);
            current.Second = ReplaceNode(current.Second, target, replacement);
            return current;
        }

        private DockLayoutNode Sanitize(DockLayoutNode node)
        {
            if (node == null) return null;
            if (node.IsTabs)
            {
                if (string.IsNullOrWhiteSpace(node.Id)) node.Id = Guid.NewGuid().ToString("N");
                node.PanelIds = (node.PanelIds ?? new List<string>()).Where(registrations.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (!node.PanelIds.Contains(node.ActivePanelId, StringComparer.OrdinalIgnoreCase)) node.ActivePanelId = node.PanelIds.FirstOrDefault();
                return node;
            }
            node.Type = "split";
            node.Ratio = Math.Max(.05F, Math.Min(.95F, node.Ratio));
            node.First = Sanitize(node.First); node.Second = Sanitize(node.Second);
            return node;
        }

        private static DockLayoutNode Normalize(DockLayoutNode node)
        {
            if (node == null) return null;
            if (node.IsTabs) return node.PanelIds == null || node.PanelIds.Count == 0 ? null : node;
            node.First = Normalize(node.First); node.Second = Normalize(node.Second);
            if (node.First == null) return node.Second;
            if (node.Second == null) return node.First;
            return node;
        }

        private static IEnumerable<DockLayoutNode> EnumerateGroups(DockLayoutNode node)
        {
            if (node == null) yield break;
            if (node.IsTabs) { yield return node; yield break; }
            foreach (var child in EnumerateGroups(node.First)) yield return child;
            foreach (var child in EnumerateGroups(node.Second)) yield return child;
        }

        private static IEnumerable<TabControl> EnumerateTabs(Control control)
        {
            foreach (Control child in control.Controls)
            {
                if (child is TabControl tabs) yield return tabs;
                foreach (var nested in EnumerateTabs(child)) yield return nested;
            }
        }

        private static void CaptureSplitRatios(Control control)
        {
            foreach (Control child in control.Controls)
            {
                if (child is SplitContainer split && split.Tag is DockLayoutNode node) node.Ratio = GetSplitterRatio(split);
                CaptureSplitRatios(child);
            }
        }

        private static DockLayoutNode ConvertLegacyGroups(IList<LegacyDockGroupState> groups)
        {
            var valid = (groups ?? new List<LegacyDockGroupState>()).Where(x => x?.PanelIds?.Count > 0).ToList();
            if (valid.Count == 0) return null;
            DockLayoutNode Build(int index, float remaining)
            {
                var legacy = valid[index];
                var group = NewGroup(); group.Id = string.IsNullOrWhiteSpace(legacy.Id) ? group.Id : legacy.Id;
                group.PanelIds = new List<string>(legacy.PanelIds); group.ActivePanelId = legacy.ActivePanelId;
                if (index == valid.Count - 1) return group;
                var weight = Math.Max(.05F, legacy.Weight);
                return new DockLayoutNode { Type = "split", Id = Guid.NewGuid().ToString("N"), Orientation = Orientation.Horizontal, Ratio = weight / remaining, First = group, Second = Build(index + 1, Math.Max(.05F, remaining - weight)) };
            }
            return Build(0, Math.Max(1F, valid.Sum(x => Math.Max(.05F, x.Weight))));
        }

        private void HideTabAt(TabControl tabs, Point point)
        {
            var page = GetTabAt(tabs, point);
            if (page != null) HidePanel(page.Tag as string);
        }

        private static TabPage GetTabAt(TabControl tabs, Point point)
        {
            for (var index = 0; index < tabs.TabPages.Count; index++) if (tabs.GetTabRect(index).Contains(point)) return tabs.TabPages[index];
            return null;
        }

        private void OnLayoutChanged() => LayoutStateChanged?.Invoke(this, EventArgs.Empty);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearDropIndicator();
                EndSplitterDrag();
                RemoveDragSurfaces();
            }
            base.Dispose(disposing);
        }

        private sealed class DockDropIndicatorWindow : Form
        {
            private const int WsExTransparent = 0x20;
            private const int WsExToolWindow = 0x80;
            private const int WsExNoActivate = 0x08000000;

            public DockDropIndicatorWindow()
            {
                AutoScaleMode = AutoScaleMode.None;
                BackColor = SystemColors.Highlight;
                FormBorderStyle = FormBorderStyle.None;
                Opacity = 0.28;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
                    return parameters;
                }
            }

            public void ShowAt(Form owner, Rectangle bounds)
            {
                if (Bounds != bounds) Bounds = bounds;
                if (Visible) return;
                if (owner != null) Show(owner);
                else Show();
            }
        }

        private sealed class SplitterIndicatorWindow : Form
        {
            private const int WsExTransparent = 0x20;
            private const int WsExToolWindow = 0x80;
            private const int WsExNoActivate = 0x08000000;

            public SplitterIndicatorWindow()
            {
                AutoScaleMode = AutoScaleMode.None;
                BackColor = SystemColors.ControlDark;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
            }

            protected override bool ShowWithoutActivation => true;

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= WsExTransparent | WsExToolWindow | WsExNoActivate;
                    return parameters;
                }
            }

            public void ShowAt(Form owner, Rectangle bounds)
            {
                if (Bounds != bounds) Bounds = bounds;
                if (Visible) return;
                if (owner != null) Show(owner);
                else Show();
            }
        }

        private sealed class DockDragSurface : Control
        {
            private const int WsExTransparent = 0x20;

            public DockDragSurface()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.SupportsTransparentBackColor, true);
                BackColor = Color.Transparent;
                TabStop = false;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= WsExTransparent;
                    return parameters;
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // Поверхность нужна только как OLE drop target и не закрашивает редактор.
            }
        }
        private enum DockDropZone { Center, Top, Bottom, Left, Right }

        private sealed class DockPanelRegistration
        {
            public DockPanelRegistration(string id, string title, Control content, bool visibleByDefault) { Id = id; Title = title; Content = content; VisibleByDefault = visibleByDefault; }
            public string Id { get; }
            public string Title { get; }
            public string CustomTitle { get; set; }
            public string DisplayTitle => string.IsNullOrWhiteSpace(CustomTitle) ? Title : CustomTitle;
            public Control Content { get; }
            public bool VisibleByDefault { get; }
        }

        private sealed class DockWorkspaceState
        {
            public int Version { get; set; }
            public DockLayoutNode Root { get; set; }
            public List<LegacyDockGroupState> Groups { get; set; }
            public Dictionary<string, string> Titles { get; set; } = new Dictionary<string, string>();
            public Dictionary<string, string> LastGroups { get; set; } = new Dictionary<string, string>();
        }

        private sealed class DockLayoutNode
        {
            public string Type { get; set; } = "tabs";
            public string Id { get; set; }
            public Orientation Orientation { get; set; }
            public float Ratio { get; set; } = .5F;
            public DockLayoutNode First { get; set; }
            public DockLayoutNode Second { get; set; }
            public List<string> PanelIds { get; set; } = new List<string>();
            public string ActivePanelId { get; set; }
            [JsonIgnore] public bool IsTabs => !string.Equals(Type, "split", StringComparison.OrdinalIgnoreCase);
            public DockLayoutNode Clone() => new DockLayoutNode { Type = Type, Id = Id, Orientation = Orientation, Ratio = Ratio, ActivePanelId = ActivePanelId, PanelIds = new List<string>(PanelIds ?? new List<string>()), First = First?.Clone(), Second = Second?.Clone() };
        }

        private sealed class LegacyDockGroupState
        {
            public string Id { get; set; }
            public float Weight { get; set; } = 1F;
            public List<string> PanelIds { get; set; } = new List<string>();
            public string ActivePanelId { get; set; }
        }
    }
}
