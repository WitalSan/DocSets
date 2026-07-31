using System.Diagnostics;
using WeifenLuo.WinFormsUI.Docking;
using DocSets.Desktop.Panels;

namespace DocSets.Desktop;

internal sealed class MainForm : Form
{
    private readonly DesktopSettingsStore settingsStore = new();
    private readonly DesktopDocumentSession session = new();
    private readonly DockPanel dockPanel = new()
    {
        Dock = DockStyle.Fill,
        DocumentStyle = DocumentStyle.DockingWindow,
        // Тема должна быть назначена до первого Show/LoadFromXml.
        Theme = new VS2015BlueTheme()
    };
    private readonly StatusStrip status = new();
    private readonly ToolStripStatusLabel documentStatus = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel dirtyStatus = new();
    private readonly MenuStrip menu = new();
    private readonly ToolStripMenuItem recentMenu = new("Недавние");
    private readonly ToolStripMenuItem panelsMenu = new("Панели");
    private readonly Dictionary<string, DesktopDockContent> panels = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ToolStripMenuItem> panelMenuItems = new(StringComparer.OrdinalIgnoreCase);
    private DesktopSettings settings;
    private TreeToolWindow tree;
    private PropertiesToolWindow properties;
    private CodeDocument code;
    private PreviewDocument preview;
    private NoteDocument note;
    private SearchToolWindow search;
    private LogToolWindow log;
    private bool closingAccepted;

    public MainForm()
    {
        Text = "DocSets";
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        settings = settingsStore.Load();
        RestoreBoundsFromSettings();
        CreatePanels();
        CreateMenu();
        status.Items.Add(documentStatus);
        status.Items.Add(dirtyStatus);
        Controls.Add(dockPanel);
        Controls.Add(status);
        Controls.Add(menu);
        MainMenuStrip = menu;
        WireEvents();
        Shown += async (_, __) => await InitializeAsync();
        FormClosing += OnFormClosing;
        FormClosed += (_, __) => SaveApplicationState();
    }

    private void CreatePanels()
    {
        tree = Register(new TreeToolWindow());
        properties = Register(new PropertiesToolWindow());
        code = Register(new CodeDocument());
        preview = Register(new PreviewDocument());
        note = Register(new NoteDocument());
        search = Register(new SearchToolWindow());
        log = Register(new LogToolWindow());
        search.SetItemsProvider(session.AllItems);
        note.SaveImageAsync = session.SaveImageAsync;
    }

    private T Register<T>(T panel) where T : DesktopDockContent
    {
        panels.Add(panel.PersistId, panel);
        panel.DockStateChanged += (_, __) => UpdatePanelMenu(panel);
        panel.FormClosed += (_, __) => UpdatePanelMenu(panel);
        return panel;
    }

    private void CreateMenu()
    {
        var file = new ToolStripMenuItem("Файл");
        file.DropDownItems.Add(Item("Создать…", Keys.Control | Keys.N, async (_, __) => await CreateAsync()));
        file.DropDownItems.Add(Item("Открыть…", Keys.Control | Keys.O, async (_, __) => await OpenAsync()));
        file.DropDownItems.Add(recentMenu);
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Сохранить", Keys.Control | Keys.S, async (_, __) => await SaveAsync()));
        file.DropDownItems.Add(Item("Закрыть DocSet", Keys.Control | Keys.W, (_, __) => CloseDocument()));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Выход", Keys.Alt | Keys.F4, (_, __) => Close()));

        var edit = new ToolStripMenuItem("Правка");
        edit.DropDownItems.Add(Item("Отменить", Keys.Control | Keys.Z, (_, __) => note.ExecuteCommand("undo")));
        edit.DropDownItems.Add(Item("Повторить", Keys.Control | Keys.Y, (_, __) => note.ExecuteCommand("redo")));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Вырезать", Keys.Control | Keys.X, (_, __) => note.ExecuteCommand("cut")));
        edit.DropDownItems.Add(Item("Копировать", Keys.Control | Keys.C, (_, __) => CopyActive()));
        edit.DropDownItems.Add(Item("Вставить", Keys.Control | Keys.V, (_, __) => note.ExecuteCommand("paste")));
        edit.DropDownItems.Add(Item("Выделить всё", Keys.Control | Keys.A, (_, __) => SelectAllActive()));
        edit.DropDownItems.Add(new ToolStripSeparator());
        edit.DropDownItems.Add(Item("Переименовать", Keys.F2, (_, __) => tree.RenameSelected()));
        edit.DropDownItems.Add(Item("Удалить", Keys.Delete, (_, __) => DeleteItem(tree.SelectedItem)));
        edit.DropDownItems.Add(Item("Найти…", Keys.Control | Keys.F, (_, __) => ShowPanel(search, DockState.DockBottom)));

        foreach (var panel in panels.Values)
        {
            var item = new ToolStripMenuItem(panel.Text) { CheckOnClick = true, Tag = panel.PersistId };
            item.Click += (_, __) => TogglePanel(panel);
            panelsMenu.DropDownItems.Add(item);
            panelMenuItems.Add(panel.PersistId, item);
        }
        panelsMenu.DropDownItems.Add(new ToolStripSeparator());
        panelsMenu.DropDownItems.Add(Item("Сбросить расположение", Keys.None, (_, __) => ResetLayout()));
        menu.Items.AddRange(new ToolStripItem[] { file, edit, panelsMenu });
        RefreshRecentMenu();
    }

    private static ToolStripMenuItem Item(string text, Keys shortcut, EventHandler click)
    {
        var item = new ToolStripMenuItem(text, null, click);
        if (shortcut != Keys.None) item.ShortcutKeys = shortcut;
        return item;
    }

    private void WireEvents()
    {
        session.DocumentChanged += (_, __) => BindDocument();
        session.DirtyChanged += (_, __) => UpdateTitle();
        tree.SelectedItemChanged += (_, item) => SelectItem(item);
        tree.ItemEdited += (_, __) => { session.MarkDirty(); UpdateTitle(); };
        tree.DeleteRequested += (_, item) => DeleteItem(item);
        properties.ItemChanged += (_, __) => { session.MarkDirty(); tree.RefreshSelectedCaption(); RefreshContent(); };
        note.ContentChanged += (_, __) => { session.MarkDirty(); preview.SetItem(tree.SelectedItem); };
        note.SaveRequested += async (_, __) => await SaveAsync();
        note.LinkActivated += (_, target) => ActivateLink(target);
        preview.LinkActivated += (_, target) => ActivateLink(target);
        search.ResultActivated += (_, result) =>
        {
            tree.SelectItem(result.Item);
            if (string.Equals(result.Where, "Заметка", StringComparison.OrdinalIgnoreCase))
                ShowPanel(note, DockState.Document);
        };
    }

    private async Task InitializeAsync()
    {
        RestoreLayout();
        if (!string.IsNullOrWhiteSpace(settings.LastDocSet) && Directory.Exists(settings.LastDocSet))
        {
            try { await OpenDirectoryAsync(settings.LastDocSet); }
            catch (Exception exception) { ReportError("Не удалось восстановить последний DocSet.", exception); }
        }
        UpdateTitle();
    }

    private void RestoreLayout()
    {
        if (File.Exists(settingsStore.LayoutPath))
        {
            try
            {
                dockPanel.LoadFromXml(settingsStore.LayoutPath, persist =>
                    panels.TryGetValue(persist, out var panel) ? panel : null);
                foreach (var panel in panels.Values) UpdatePanelMenu(panel);
                return;
            }
            catch (Exception exception)
            {
                DocSetsLog.Current.Error("Layout", "Сохранённое расположение повреждено; применяется стандартное.", exception);
                settingsStore.DeleteLayout();
            }
        }
        ShowDefaultLayout();
    }

    private void ShowDefaultLayout()
    {
        tree.Show(dockPanel, DockState.DockLeft);
        properties.Show(dockPanel, DockState.DockRight);
        code.Show(dockPanel, DockState.Document);
        preview.Show(dockPanel, DockState.Document);
        note.Show(dockPanel, DockState.Document);
        search.Show(dockPanel, DockState.DockBottom);
        log.Hide();
        foreach (var panel in panels.Values) UpdatePanelMenu(panel);
    }

    private void ResetLayout()
    {
        foreach (var panel in panels.Values) panel.Hide();
        settingsStore.DeleteLayout();
        ShowDefaultLayout();
    }

    private async Task CreateAsync()
    {
        if (!CanReplaceDocument()) return;
        using var dialog = new CreateDocSetDialog(InitialDirectory());
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            await session.CreateAsync(dialog.DirectoryPath, dialog.DocSetName);
            RememberDocument(dialog.DirectoryPath);
        }
        catch (Exception exception) { ReportError("Не удалось создать DocSet.", exception); }
    }

    private async Task OpenAsync()
    {
        if (!CanReplaceDocument()) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Открытие DocSet",
            Filter = "Манифест DocSet (docsets.json)|docsets.json|JSON (*.json)|*.json|Все файлы (*.*)|*.*",
            InitialDirectory = InitialDirectory(),
            FileName = "docsets.json",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await OpenDirectorySafeAsync(Path.GetDirectoryName(dialog.FileName));
    }

    private async Task OpenDirectorySafeAsync(string path)
    {
        try { await OpenDirectoryAsync(path); }
        catch (Exception exception) { ReportError("Не удалось открыть DocSet.", exception); }
    }

    private async Task OpenDirectoryAsync(string path)
    {
        await session.OpenAsync(path);
        RememberDocument(session.DirectoryPath);
        DocSetsLog.Current.Info("Хранилище", "DocSet открыт: " + session.DirectoryPath);
    }

    private async Task SaveAsync()
    {
        note.CommitCurrent();
        try
        {
            await session.SaveAsync();
            DocSetsLog.Current.Info("Хранилище", "DocSet сохранён: " + session.DirectoryPath);
        }
        catch (Exception exception) { ReportError("Не удалось сохранить DocSet.", exception); }
    }

    private void CloseDocument()
    {
        if (!CanReplaceDocument()) return;
        session.Close();
    }

    private bool CanReplaceDocument()
    {
        note.CommitCurrent();
        if (!session.IsDirty) return true;
        var answer = MessageBox.Show(this, "Сохранить изменения текущего DocSet?", "DocSets",
            MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (answer == DialogResult.Cancel) return false;
        if (answer == DialogResult.Yes)
        {
            try { session.SaveAsync().GetAwaiter().GetResult(); }
            catch (Exception exception) { ReportError("Не удалось сохранить DocSet.", exception); return false; }
        }
        return true;
    }

    private void BindDocument()
    {
        tree.LoadDocument(session.State, session.Name, settings);
        note.SetAssetDirectory(session.AssetDirectory);
        preview.SetAssetDirectory(session.AssetDirectory);
        if (tree.SelectedItem == null) SelectItem(null);
        UpdateTitle();
    }

    private void SelectItem(DocumentItem item)
    {
        properties.SetItem(item);
        note.SetItem(item);
        code.SetItem(item);
        preview.SetItem(item);
        documentStatus.Text = item?.Display ?? session.DirectoryPath;
    }

    private void RefreshContent() => SelectItem(tree.SelectedItem);

    private void DeleteItem(DocumentItem item)
    {
        if (item == null || session.State == null) return;
        if (MessageBox.Show(this, $"Удалить «{item.Name}»?", "DocSets",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (!Remove(session.State.Sets, item)) return;
        session.MarkDirty();
        tree.LoadDocument(session.State, session.Name, settings);
    }

    private static bool Remove(IList<DocumentItem> items, DocumentItem target)
    {
        if (items.Remove(target)) return true;
        foreach (var item in items)
            if (Remove(item.Children, target)) return true;
        return false;
    }

    private void ActivateLink(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        try
        {
            var uri = new Uri(target, UriKind.RelativeOrAbsolute);
            if (uri.IsAbsoluteUri && uri.Host.Equals("docsets.local", StringComparison.OrdinalIgnoreCase))
            {
                var parts = uri.AbsolutePath.Trim('/').Split('/');
                if (parts.Length >= 2 && parts[0].Equals("bookmark", StringComparison.OrdinalIgnoreCase))
                    tree.SelectItem(session.FindById(Uri.UnescapeDataString(parts[1])));
                return;
            }
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception exception) { ReportError("Не удалось открыть ссылку.", exception); }
    }

    private void CopyActive()
    {
        if (code.ContainsFocus) code.Copy(); else note.ExecuteCommand("copy");
    }

    private void SelectAllActive()
    {
        if (code.ContainsFocus) code.SelectAllText(); else note.ExecuteCommand("selectall");
    }

    private void TogglePanel(DesktopDockContent panel)
    {
        if (panel.Visible) panel.Hide(); else ShowPanel(panel, DefaultDockState(panel));
        UpdatePanelMenu(panel);
    }

    private void ShowPanel(DesktopDockContent panel, DockState state)
    {
        if (panel.DockPanel == null) panel.Show(dockPanel, state); else panel.Show();
        panel.Activate();
        UpdatePanelMenu(panel);
    }

    private static DockState DefaultDockState(DesktopDockContent panel) => panel.PersistId switch
    {
        "tree" => DockState.DockLeft,
        "properties" => DockState.DockRight,
        "search" or "log" => DockState.DockBottom,
        _ => DockState.Document
    };

    private void UpdatePanelMenu(DesktopDockContent panel)
    {
        if (panelMenuItems.TryGetValue(panel.PersistId, out var item)) item.Checked = panel.Visible;
    }

    private void RememberDocument(string path)
    {
        settings.LastDocSet = path;
        settings.RecentDocSets.RemoveAll(value => string.Equals(value, path, StringComparison.OrdinalIgnoreCase));
        settings.RecentDocSets.Insert(0, path);
        if (settings.RecentDocSets.Count > 10) settings.RecentDocSets.RemoveRange(10, settings.RecentDocSets.Count - 10);
        RefreshRecentMenu();
        settingsStore.Save(settings);
    }

    private void RefreshRecentMenu()
    {
        recentMenu.DropDownItems.Clear();
        foreach (var path in settings.RecentDocSets.Where(Directory.Exists))
        {
            var item = new ToolStripMenuItem(path) { ToolTipText = path };
            item.Click += async (_, __) => { if (CanReplaceDocument()) await OpenDirectorySafeAsync(path); };
            recentMenu.DropDownItems.Add(item);
        }
        recentMenu.Enabled = recentMenu.DropDownItems.Count > 0;
    }

    private string InitialDirectory()
    {
        if (Directory.Exists(session.DirectoryPath)) return Path.GetDirectoryName(session.DirectoryPath) ?? session.DirectoryPath;
        if (Directory.Exists(settings.LastDocSet)) return Path.GetDirectoryName(settings.LastDocSet) ?? settings.LastDocSet;
        return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void UpdateTitle()
    {
        var name = string.IsNullOrWhiteSpace(session.Name) ? "DocSets" : session.Name + " — DocSets";
        Text = session.IsDirty ? "* " + name : name;
        dirtyStatus.Text = session.IsDirty ? "Изменён" : "";
        documentStatus.Text = session.State == null ? "DocSet не открыт" : session.DirectoryPath;
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (closingAccepted) return;
        if (!CanReplaceDocument()) { e.Cancel = true; return; }
        closingAccepted = true;
    }

    private void SaveApplicationState()
    {
        tree.CaptureState(settings);
        if (WindowState == FormWindowState.Normal)
        {
            settings.X = Bounds.X; settings.Y = Bounds.Y;
            settings.Width = Bounds.Width; settings.Height = Bounds.Height;
        }
        settings.WindowState = WindowState == FormWindowState.Minimized ? FormWindowState.Normal : WindowState;
        settingsStore.Save(settings);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsStore.LayoutPath));
            dockPanel.SaveAsXml(settingsStore.LayoutPath);
        }
        catch (Exception exception) { DocSetsLog.Current.Error("Layout", "Не удалось сохранить расположение панелей.", exception); }
    }

    private void RestoreBoundsFromSettings()
    {
        StartPosition = FormStartPosition.Manual;
        var bounds = settings.Bounds;
        var visible = Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(bounds));
        Bounds = visible ? bounds : new Rectangle(80, 80, 1400, 900);
        WindowState = settings.WindowState;
    }

    private void ReportError(string message, Exception exception)
    {
        DocSetsLog.Current.Error("Desktop", message, exception);
        MessageBox.Show(this, message + Environment.NewLine + exception.Message, "DocSets",
            MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
