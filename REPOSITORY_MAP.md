# Карта репозитория DocSets

## Назначение

DocSets — расширение Visual Studio 2022 для организации закладок по файлам и
символам в древовидные наборы. Закладки могут совместно использоваться через
`*.docsets.json`, тогда как персональное состояние интерфейса хранится в `.vs`.

## Карта верхнего уровня

```text
DocSets/
├── DocSets.Core/                 Модель, хранение и интеграция с редактором
│   ├── Models/
│   │   └── Model.cs              Модель дерева, DTO, локальное/UI-состояние
│   ├── Roslyn/
│   │   └── RoslynBookmarkResolver.cs
│   │                              Создание и открытие symbol-закладок
│   ├── Services/
│   │   ├── DocSetsViewModel.cs   Центральная прикладная логика и команды
│   │   ├── DocumentTreeService.cs
│   │   │                          Чистые алгоритмы дерева и drag-and-drop
│   │   ├── NavigationHistoryService.cs
│   │   │                          Local-only журнал навигации
│   │   ├── PinService.cs         Pin-ссылки и миграция TargetId
│   │   ├── UndoRedoService.cs    Ограниченная история снимков
│   │   ├── EditorStateService.cs Снимок и восстановление состояния редактора
│   │   ├── FileBookmarkTrackingService.cs
│   │   │                          Отслеживание позиции file-закладок
│   │   └── RelayCommand.cs       Адаптер ICommand
│   └── Storage/
│       └── DocSetsStore.cs       Workspace discovery и JSON persistence
├── DocSets.UI.WinForms/          Пользовательский интерфейс
│   ├── Forms/
│   │   ├── DocSetsWinFormsControl.cs
│   │   │                          Главное окно, дерево, меню, фильтры и вкладки
│   │   ├── BookmarkPropertiesPanel.cs
│   │   ├── BookmarkPropertiesDialog.cs
│   │   ├── CodePreviewHighlighter.cs
│   │   └── PromptDialog.cs
│   ├── Icons/
│   │   └── IconProvider.cs       Загрузка, масштабирование и overlay иконок
│   └── TreeViewAdv/
│       ├── BookmarkTreeNode.cs   Адаптер DocumentItem → Aga TreeNode
│       └── OverflowNodeTextBox.cs
├── DocSets.Vsix/                 Интеграция с Visual Studio
│   ├── Package/
│   │   ├── DocSetsPackage.cs     AsyncPackage и регистрация расширения
│   │   └── DocSetsToolWindowCommand.cs
│   │                              Команды VS и открытие Tool Window
│   ├── ToolWindow/
│   │   └── DocSetsToolWindow.cs  ToolWindowPane
│   └── WinFormsHost/
│       └── DocSetsWinFormsHostControl.cs
│                                  WPF-host, lifecycle и фоновые таймеры
├── Aga.Controls/                 Вендорный TreeViewAdv (отдельная сборка)
├── -Icons-/                      Встроенные PNG-ресурсы DocSets
├── Properties/                   Метаданные основной сборки
├── Resources/                    Ресурсы VSCT
├── DocSets.csproj                Основной .NET Framework/VSIX проект
├── Aga.Controls/Aga.Controls.csproj
├── DocSets.sln                   Solution из двух проектов
├── DocSetsPackage.vsct           Меню, команды и горячие клавиши VS
├── source.extension.vsixmanifest Manifest VSIX
├── DocSets.docsets.json          Workspace самого проекта
├── Publish.cmd                   Локальная установка Debug VSIX
└── *_CHANGES.md                  Заметки по отдельным реализованным функциям
```

## Проекты и зависимости

```text
DocSets (VSIX, .NET Framework 4.7.2, C# 9)
├── Aga.Controls
├── Microsoft.VisualStudio.SDK
├── Microsoft.VisualStudio.LanguageServices
├── Microsoft.CodeAnalysis.CSharp.Workspaces
├── Newtonsoft.Json
├── WPF / WindowsFormsIntegration
└── Windows Forms

Aga.Controls (.NET Framework 4.7.2)
└── Windows Forms / System.Drawing
```

Папки `DocSets.Core`, `DocSets.UI.WinForms` и `DocSets.Vsix` являются логическими
слоями, но компилируются в одну сборку `DocSets.dll`. Физически изолирована только
библиотека `Aga.Controls`.

## Запуск расширения

```text
DocSetsPackage.InitializeAsync
    → DocSetsToolWindowCommand.InitializeAsync
        → пользователь открывает Tool Window
            → DocSetsToolWindow
                → DocSetsWinFormsHostControl
                    ├── DocSetsViewModel
                    └── DocSetsWinFormsControl
```

`DocSetsWinFormsHostControl` управляет жизненным циклом окна и таймерами:

- повторная загрузка после открытия solution — каждые 2 секунды, до 30 попыток;
- проверка внешнего изменения workspace — каждые 1,5 секунды;
- фиксация истории навигации — каждые 500 миллисекунд.

## Основной поток данных

```text
WinForms events
    → ICommand / методы DocSetsViewModel
        → изменение DocumentItem
            → DocumentItem.TreeChanged
                ├── обновление UI
                └── DocSetsViewModel.SaveAsync
                    → FileBookmarkTrackingService
                    → DocSetsStore.SaveAsync
                        → *.docsets.json
```

Для перехода по закладке поток обратный:

```text
OpenBookmarkCommand
    → DocSetsStore.OpenBookmarkAsync
        ├── Symbol: RoslynBookmarkResolver.TryOpenBookmarkBySymbolAsync
        └── fallback/File: открыть Path:Line:Column
            → EditorStateService.RestoreAsync
```

## Модель дерева

`DocumentSetsState` содержит синтетический `Root`. Его дочерние элементы — наборы
(корневые папки), History и Pin. Один `DocumentItem` используется для всех видов
узлов.

```text
DocumentSetsState.Root
├── History                 local-only
├── Pin                     local-only, ссылки по TargetId
├── Set / root folder
│   ├── Folder
│   │   ├── Symbol bookmark
│   │   └── File bookmark
│   └── Bookmark
└── Set / root folder
```

Основные признаки `DocumentItem`:

- `NodeType`: `Item`, `Folder`, legacy `Set`;
- `Type`: `Symbol`, `File`, `Empty`, `Pin`;
- `Id` и `Parent` формируют устойчивую идентичность и runtime-дерево;
- `Symbol`, `Project`, `Path`, `Line`, `Column` задают цель перехода;
- `EditorState` хранит каретку, selection, viewport и code preview;
- `IsLocalOnly` исключает History и Pin из общего workspace.

В памяти используется дерево `ObservableCollection<DocumentItem>`. В JSON оно
преобразуется в плоский список `items`, где связь задаётся полем `parent`.

## Хранение данных

### Общий workspace

Файлы `*.docsets.json` обнаруживаются в каталоге solution и его предках. Также
поддерживается legacy-имя `DocSets.workspace.json`.

Содержимое:

- группы и порядок узлов;
- папки и закладки;
- читаемые ID;
- относительные пути;
- editor state и preview.

Запись выполняется через временный файл с последующим `File.Replace`/`Move`.

### Локальное состояние solution

```text
<solution-directory>/.vs/DockSets/<solution-name>.json
```

Содержимое:

- выбранный workspace и активная вкладка;
- свёрнутые и выбранные узлы для каждого представления;
- фильтры, режим активации и состояние панели свойств;
- локальная History;
- Pin-ссылки.

## Ответственность ключевых компонентов

| Компонент | Ответственность |
|---|---|
| `DocSetsPackage` | Регистрация и инициализация VSIX |
| `DocSetsToolWindowCommand` | Команды меню/редактора Visual Studio |
| `DocSetsWinFormsHostControl` | Создание UI, lifecycle, polling |
| `DocSetsWinFormsControl` | Отрисовка дерева, ввод, меню, drag-and-drop, локальное UI-состояние |
| `DocSetsViewModel` | Команды, выбор, Visual Studio integration и persistence orchestration |
| `DocumentTreeService` | Поиск, обход, вставка и планы перемещения дерева |
| `NavigationHistoryService` | Дедупликация, лимит и импорт/экспорт History |
| `PinService` | Создание, разрешение и миграция Pin-ссылок |
| `UndoRedoService` | Порядок, лимит и переходы между undo/redo снимками |
| `DocumentSetsState` | Корень модели и JSON-представление workspace |
| `DocumentItem` | Универсальный узел дерева и события изменений |
| `DocSetsStore` | Поиск workspace, JSON I/O, пути, локальное состояние solution |
| `RoslynBookmarkResolver` | Разрешение текущего/сохранённого символа |
| `EditorStateService` | Снимок и восстановление состояния редактора |
| `FileBookmarkTrackingService` | Обновление Line/Column file-закладок |
| `BookmarkTreeNode` | Проекция доменного узла в Aga TreeViewAdv |

## Где вносить изменения

| Задача | Основные файлы |
|---|---|
| Новое поле закладки/JSON | `Model.cs`, свойства UI, миграция/clone |
| Новый алгоритм дерева | `DocumentTreeService.cs`, затем orchestration в `DocSetsViewModel.cs` |
| Изменение History/Pin | `NavigationHistoryService.cs`, `PinService.cs` |
| Изменение Undo/Redo | `UndoRedoService.cs`, snapshot orchestration в `DocSetsViewModel.cs` |
| Изменение workspace discovery | `DocSetsStore.cs` |
| Навигация по символам | `RoslynBookmarkResolver.cs` |
| Каретка, selection, preview | `EditorStateService.cs` |
| Отслеживание file bookmark | `FileBookmarkTrackingService.cs` |
| Вкладки, фильтры, меню, DnD | `DocSetsWinFormsControl.cs` |
| Команда в меню Visual Studio | `DocSetsPackage.vsct`, `DocSetsToolWindowCommand.cs` |
| Иконки | `-Icons-/`, `IconProvider.cs`, `DocSets.csproj` |
| Packaging/совместимость VS | `source.extension.vsixmanifest`, `DocSets.csproj` |

## Проверки после изменений

1. Собрать `DocSets.sln` в нужной конфигурации и архитектуре.
2. Запустить Experimental Instance Visual Studio (`/rootsuffix Exp`).
3. Проверить загрузку старого и нового форматов JSON.
4. Проверить Symbol и File bookmarks после изменения исходного файла.
5. Проверить переключение workspace и внешний reload.
6. Проверить Undo/Redo, History, Pin и локальное состояние вкладок.
7. Убедиться, что `bin`, `obj` и `.vs` не попали в коммит.

## Текущие архитектурные ограничения

- `DocSetsViewModel` и `DocSetsWinFormsControl` являются крупнейшими компонентами
  и концентрируют несколько ответственностей.
- Слои Core/UI/VSIX не разделены сборками и интерфейсами.
- Автоматических тестовых проектов в solution нет.
- Workspace synchronization выполняет полный reload без merge конфликтов.
- Открытие symbol-закладки может сканировать документы и syntax nodes solution.
- Асинхронные обработчики команд и таймеров требуют осторожного контроля ошибок
  и повторного входа.
