# Архитектура DocSets

## 1. Назначение и границы системы

DocSets — расширение Visual Studio 2022 для хранения и навигации по древовидным
наборам закладок. Закладка может указывать на символ исходного кода или на позицию
в файле. Общие данные сохраняются в `*.docsets.json`, а персональное состояние
пользователя — внутри `.vs` текущего solution.

Расширение рассчитано на Visual Studio 2022 (`[17.0, 18.0)`, amd64), .NET
Framework 4.7.2 и C# 9.

## 2. Компоненты

Solution содержит два проекта:

```text
DocSets.sln
├── DocSets.csproj                 VSIX и вся логика DocSets
└── Aga.Controls/Aga.Controls.csproj
                                      Вендорный WinForms TreeViewAdv
```

Внутри `DocSets.csproj` код разделён логически, но не физически: `Core`, UI и
VSIX-интеграция компилируются в одну сборку `DocSets.dll`.

```text
DocSets.Vsix/
  Package/        регистрация AsyncPackage и команд Visual Studio
  ToolWindow/     ToolWindowPane
  WinFormsHost/   WPF WindowsFormsHost, lifecycle и фоновые таймеры

DocSets.UI.WinForms/
  Forms/          главное окно, свойства закладки и диалоги
  TreeViewAdv/    адаптеры DocumentItem для Aga.Controls.Tree
  Icons/          загрузка и композиция встроенных иконок

DocSets.Core/
  Models/         runtime-модель, storage DTO и локальное состояние
  Storage/        поиск workspace, JSON I/O и преобразование путей
  Roslyn/         создание и разрешение symbol-закладок
  Services/       use cases, команды и интеграция с редактором

Aga.Controls/     сторонний TreeViewAdv без бизнес-логики DocSets
```

Подробная навигация по файлам находится в [REPOSITORY_MAP.md](REPOSITORY_MAP.md).

## 3. Runtime-композиция

Объекты создаются вручную сверху вниз, DI-контейнер не используется:

```text
DocSetsPackage
  └── DocSetsToolWindowCommand
       └── DocSetsToolWindow
            └── DocSetsWinFormsHostControl
                 ├── DocSetsViewModel
                 │    ├── DocSetsStore
                 │    │    └── RoslynBookmarkResolver
                 │    │         └── EditorStateService
                 │    ├── FileBookmarkTrackingService
                 │    ├── DocumentTreeService
                 │    ├── NavigationHistoryService
                 │    ├── PinService
                 │    └── UndoRedoService
                 └── DocSetsWinFormsControl
                      └── Aga.Controls.TreeViewAdv
```

Основной запуск:

1. `DocSetsPackage.InitializeAsync` регистрирует команды расширения.
2. Команда открытия создаёт `DocSetsToolWindow`.
3. Tool window создаёт WPF-host `DocSetsWinFormsHostControl`.
4. Host создаёт один `DocSetsViewModel` и один `DocSetsWinFormsControl`.
5. При `Loaded` загружаются workspace и локальное состояние solution.

Host владеет тремя `DispatcherTimer`:

- повтор загрузки solution — каждые 2 секунды, максимум 30 попыток;
- проверка внешних изменений workspace — каждые 1,5 секунды;
- фиксация истории навигации — каждые 500 миллисекунд.

Таймеры работают на UI dispatcher. Доступ к DTE, editor views и большинству
Visual Studio services также переводится на UI thread через
`ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync`.

## 4. Модель данных

### 4.1 Runtime-дерево

`DocumentSetsState` содержит синтетический корень `Root`. Корневые папки дерева
являются наборами (Sets). Отдельного runtime-класса Set нет.

```text
DocumentSetsState.Root
├── History                         local-only root
├── Pin                             local-only root
├── Set A                           обычный DocumentItem/Folder
│   ├── Folder
│   │   ├── Symbol bookmark
│   │   └── File bookmark
│   └── Bookmark
└── Set B
```

Универсальный узел `DocumentItem` содержит:

- `Id`, `Name`, `Parent`, `Children`;
- `NodeType`: `Item`, `Folder`, legacy-значение `Set`;
- `Type`: `Symbol`, `File`, `Empty`, `Pin`;
- `Symbol` и `Project` для Roslyn-навигации;
- `Path`, `Line`, `Column` для файловой навигации и fallback;
- `Comment`, `Color`, `EditorState`;
- runtime-флаги выделения, History, Pin и local-only.

`DocumentItem.Children` — `ObservableCollection<DocumentItem>`. Добавление,
удаление, перемещение и изменение свойства поднимают единое событие `TreeChanged`
до корня. ViewModel использует его для обновления UI и сохранения workspace.

### 4.2 Persisted-представление

В памяти используется дерево, но в workspace JSON оно преобразуется в плоский
массив `items`. Иерархия задаётся через `id` и `parent`:

```json
{
  "activeSet": "Default",
  "items": [
    {
      "id": "default",
      "parent": "",
      "name": "Default",
      "nodeType": "Folder",
      "line": 1,
      "column": 1
    },
    {
      "id": "docsets-store-load",
      "parent": "default",
      "name": "DocSetsStore.LoadAsync",
      "type": "Symbol",
      "symbol": "DocSets.DocSetsStore.LoadAsync",
      "project": "DocSets",
      "path": "DocSets.Core\\Storage\\DocSetsStore.cs",
      "line": 68,
      "column": 9
    }
  ],
  "ui": {
    "columns": []
  }
}
```

Перед сериализацией узлам назначаются уникальные читаемые ID. Узлы с
`IsLocalOnly` в workspace не записываются.

Загрузка поддерживает миграции старых файлов:

- прежний массив `sets`, в котором каждый Set содержал собственные `items`;
- `NodeType.Set`, нормализуемый в корневую папку;
- поле `isFolder`, существовавшее до `NodeType`;
- GUID-подобные ID, заменяемые читаемыми;
- symbol-строки в legacy `FullyQualifiedFormat`.

## 5. Два уровня хранения

### 5.1 Общий workspace

`DocSetsStore` начинает поиск в каталоге активного `.sln` и поднимается по всем
родительским каталогам. В каждом каталоге обнаруживаются:

- все файлы `*.docsets.json`;
- legacy-файл `DocSets.workspace.json`.

Пользователь выбирает workspace в UI. Выбор сохраняется локально для solution.
Если сохранённого выбора нет, предпочтение отдаётся workspace с именем solution,
затем первому найденному. Если файлов нет, используется новый
`<SolutionName>.docsets.json` рядом с solution.

Пути закладок сохраняются относительно каталога выбранного workspace. Поэтому один
workspace в корне монорепозитория может обслуживать несколько вложенных solutions:

```text
RepoRoot/
  Shared.docsets.json
  ServiceA/ServiceA.sln
  ServiceB/ServiceB.sln
  Shared/Shared.sln
```

Сохранение workspace:

1. сериализуется через Newtonsoft.Json;
2. записывается во временный файл рядом с целевым;
3. существующий файл заменяется через `File.Replace`;
4. если replace недоступен, используется copy/delete;
5. новые файлы устанавливаются через `File.Move`.

Вызовы сохранения сериализованы `SemaphoreSlim`. Для обнаружения внешних изменений
запоминаются `LastWriteTimeUtc` и длина файла. При изменении выполняется полный
reload с попыткой восстановить активный Set и путь выбранного узла.

Синхронизация не выполняет merge: файл workspace является общей последней версией,
а конфликтующие параллельные изменения должны разрешаться внешним инструментом или
пользователем.

### 5.2 Локальное состояние solution

Персональное состояние хранится отдельно:

```text
<solution-directory>/.vs/DockSets/<solution-name>.json
```

`SolutionLocalState` содержит:

- путь выбранного workspace;
- `ActiveViewId`;
- collapsed/selected IDs для каждого представления дерева;
- раскладку колонок и высоту панели свойств;
- режим активации дерева;
- текстовый и цветовые фильтры;
- видимость панели свойств;
- History;
- Pin-ссылки.

Это состояние не предназначено для совместного использования и обычно исключено
из Git вместе с каталогом `.vs`.

## 6. Прикладная логика

`DocSetsViewModel` — координатор use cases. Он отвечает за:

- загрузку, переключение и внешний reload workspace;
- выбор Set и одного/нескольких узлов;
- создание, переименование, удаление и перемещение дерева;
- drag-and-drop и copy/paste, включая JSON clipboard;
- создание и обновление закладок из активного редактора;
- orchestration History, Pin и Undo/Redo через отдельные сервисы;
- миграцию ID и сохранение локального состояния;
- маршрутизацию команд через `ICommand`.

Чистые алгоритмы вынесены из ViewModel:

- `DocumentTreeService` рассчитывает владельцев, иерархию, допустимость и позиции
  копирования/перемещения;
- `NavigationHistoryService` владеет local-only History, дедупликацией и лимитом;
- `PinService` владеет local-only Pin, разрешением `TargetId` и миграцией ссылок;
- `UndoRedoService` хранит упорядоченные снимки и ограничивает их количество.

Эти сервисы не зависят от Visual Studio SDK, UI и файлового storage. Создание
снимков, применение восстановленного состояния и момент сохранения остаются во
ViewModel.

Типичный поток изменения:

```text
WinForms event
  → RelayCommand / метод DocSetsViewModel
    → изменение DocumentItem
      → Root.TreeChanged
        ├── DocSetsWinFormsControl обновляет дерево/toolbar
        └── DocSetsViewModel запускает SaveAsync
          → FileBookmarkTrackingService обновляет file positions
          → DocSetsStore сохраняет workspace
```

Persistent user operations run through `ExecuteMutationAsync`. The outer mutation
captures one Undo snapshot, suppresses intermediate saves raised by `TreeChanged`,
and performs one final workspace save. Calls to `SaveAsync` inside the scope only
mark that final save as required. History and Pin changes are local-only and do not
write the shared workspace.

UI-состояние (`IsExpanded`, `IsMultiSelected`) не вызывает сохранение общего
workspace. Оно фиксируется через `SolutionLocalState`.

## 7. Типы закладок и навигация

### 7.1 Symbol bookmark

При создании `RoslynBookmarkResolver`:

1. получает активный document и позицию DTE selection;
2. находит соответствующий Roslyn document;
3. ищет ближайший объявленный symbol по цепочке syntax ancestors;
4. сохраняет имя symbol вместе с containing type/namespace, но без параметров;
5. сохраняет имя проекта и файловую позицию как fallback;
6. привязывает `EditorState` к строке декларации symbol.

При открытии resolver просматривает документы подходящего проекта, сравнивает
объявленные symbols с сохранённым именем и открывает найденную декларацию. Если
symbol не разрешён, используется сохранённый файл, строка и колонка.

Поддерживаются методы, конструкторы, свойства, индексаторы, события, поля, классы,
структуры, интерфейсы, перечисления и делегаты.

### 7.2 File bookmark

File bookmark всегда открывается по `Path`, `Line`, `Column`.
`FileBookmarkTrackingService` связывает такую закладку с `IWpfTextView` файла и
обновляет позицию из текущей каретки. Подписки удаляются при закрытии view или когда
закладка исчезает из модели.

### 7.3 Editor state и preview

`EditorStateService` сохраняет относительно якорной строки:

- позицию каретки;
- начало и конец selection;
- исходный выделенный текст;
- первую видимую строку;
- code preview.

Для symbol bookmark якорь — строка декларации. Для file bookmark — сохранённая
строка. При восстановлении selection сначала ищется сохранённый текст с
нормализованными пробелами в окне ±300 строк, затем используется координатный
fallback.

Без selection preview содержит до шести строк: декларацию symbol с присоединённым
комментарием либо текущую и следующие строки файла.

## 8. History и Pin

### History

History — local-only корневая папка, сохраняемая в `SolutionLocalState`.

- активная позиция проверяется каждые 500 мс;
- новая запись создаётся только при переходе к другому symbol или файлу;
- повторная цель обновляется и переносится в начало;
- переход из History подавляет создание дубликата;
- внутри одного symbol перемещения не создают новые записи;
- максимальный размер — 2000 записей;
- pinned history-записи не удаляются при обрезке лимита.

### Pin

Pin — local-only корневая папка. Pin-узел не копирует исходную закладку, а хранит
`TargetId`. Перед выполнением команды ViewModel разрешает его в исходный
`DocumentItem`. При миграции ID ссылки Pin и локальные IDs представлений также
переписываются.

## 9. Undo/Redo

Undo/Redo хранится в `UndoRedoService` только в памяти текущего Tool Window и
очищается при загрузке или смене workspace.

Перед изменением ViewModel сериализует полный `DocumentSetsState` и список Pin в
JSON-снимок. `UndoRedoService` управляет порядком снимков, а ViewModel применяет
полученное состояние и сразу сохраняет восстановленный workspace. Лимит истории —
100 снимков; при переполнении удаляется самый старый снимок.

History не входит в undo-снимок; она является журналом навигации, а не частью
редактируемого общего дерева.

## 10. Пользовательский интерфейс

Visual Studio Tool Window является WPF-контейнером, внутри которого через
`WindowsFormsHost` размещён `DocSetsWinFormsControl`.

Главный WinForms-контрол программно строит:

- выбор workspace;
- стандартные вкладки Full-Tree, History и Pin;
- вкладки пользовательских Sets;
- toolbar, поиск и Undo/Redo;
- текстовые и цветовые фильтры;
- `TreeViewAdv` с настраиваемыми колонками;
- контекстные меню и drag-and-drop;
- панель свойств и code preview;
- status bar с активным storage path.

`BookmarkTreeNode` является проекцией `DocumentItem` в модель Aga TreeViewAdv.
Фильтрация создаёт отфильтрованную проекцию, не меняя доменное дерево.

Состояние collapse/selection кэшируется отдельно для Full-Tree и каждого Set, затем
сохраняется по устойчивым ID в `SolutionLocalState`.

## 11. Команды Visual Studio

`DocSetsPackage.vsct` регистрирует:

- открытие Tool Window в меню окон;
- `DocSets: Добавить закладку` в контекстном меню редактора;
- `DocSets: Найти` в контекстном меню редактора;
- `Ctrl+Num+` для добавления закладки.

Команды редактора сначала открывают/получают Tool Window, гарантируют загрузку
ViewModel и передают действие WinForms-контролу.

## 12. Правила расширения

При добавлении нового persisted-поля закладки необходимо синхронно обновить:

1. `DocumentItem`;
2. `DocumentItemStorageDto`;
3. преобразования `AppendFlatItem` и `FromStorageDto`;
4. `DocumentItem.Clone`;
5. clipboard DTO/сериализацию в `DocSetsViewModel`;
6. свойства UI, если поле редактируется пользователем;
7. миграцию, если меняется смысл существующих данных.

При добавлении новой команды дерева обычно изменяются:

1. команда и use case в `DocSetsViewModel`;
2. toolbar/context menu/shortcut в `DocSetsWinFormsControl`;
3. `CaptureUndoState` до первой мутации;
4. правила для History/Pin и multi-selection;
5. проверка сохранения общего и локального состояния.

Команда уровня Visual Studio дополнительно требует согласованных ID в
`DocSetsPackage.vsct` и `DocSetsToolWindowCommand`.

## 13. Архитектурные ограничения

- `DocSetsViewModel` остаётся orchestration-фасадом для команд, selection, Visual
  Studio integration и persistence.
- `DocSetsWinFormsControl` одновременно отвечает за построение UI, UI-state,
  фильтрацию, ввод и orchestration представления.
- Границы `Core/UI/Vsix` не защищены отдельными сборками или интерфейсами; Core
  напрямую использует Visual Studio SDK и UI primitives.
- Сохранение инициируется как событиями модели, так и отдельными командами, поэтому
  при изменениях следует учитывать повторные и параллельно поставленные записи.
- Внешняя синхронизация является polling/reload-механизмом без merge.
- Разрешение symbol bookmark может сканировать документы и syntax nodes проекта.
- Автоматических тестовых проектов в solution сейчас нет.
- Ошибки интеграции с DTE/Roslyn и локального persistence часто обрабатываются как
  best effort, чтобы не нарушать работу Visual Studio.

## 14. Проверка архитектурно значимых изменений

После изменений модели, storage или навигации следует проверить:

1. сборку Debug и Release нужной архитектуры;
2. запуск Experimental Instance (`devenv.exe /rootsuffix Exp`);
3. загрузку legacy и текущего формата workspace;
4. round trip JSON без потери порядка и ID;
5. Symbol/File navigation после редактирования исходников;
6. восстановление caret, selection, viewport и preview;
7. переключение workspace и внешний reload;
8. History, Pin и миграцию ссылок после изменения ID;
9. Undo/Redo для добавления, удаления, переименования и drag-and-drop;
10. восстановление вкладки, collapse, selection и фильтров после перезапуска VS.
