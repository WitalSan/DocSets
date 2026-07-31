# Архитектура DocSets

## 1. Цель разделения

DocSets работает в двух средах:

- расширение Visual Studio 2022 на .NET Framework 4.7.2;
- самостоятельное приложение `DocSets.Desktop` на .NET 8.

Модель, хранение и прикладные алгоритмы не должны зависеть от Visual Studio SDK,
WinForms или конкретного редактора. Общий код собирается один раз и подключается
через ссылки на проекты. Подключение общих исходников через `Compile/Link` запрещено.

## 2. Сборки и зависимости

```text
                            netstandard2.0
          ┌─────────────────────────────────────────┐
          │ DocSets.Model                           │
          │ доменная модель и DTO                   │
          └───────────────────┬─────────────────────┘
                              │
          ┌───────────────────▼─────────────────────┐
          │ DocSets.Serialization                   │
          │ JSON, каталоги DocSet, assets, журнал   │
          └───────────────────┬─────────────────────┘
                              │
          ┌───────────────────▼─────────────────────┐
          │ DocSets.Core                            │
          │ use cases, дерево, поиск, Pin, история  │
          │ и контракты внешней среды               │
          └───────────────┬───────────────┬─────────┘
                          │               │
                net472    │               │ net8.0-windows
          ┌───────────────▼──────┐  ┌─────▼──────────────────┐
          │ DocSets VSIX         │  │ DocSets.Desktop        │
          │ VS SDK, Roslyn, DTE  │  │ самостоятельный host   │
          └──────────────────────┘  └────────────────────────┘

          ┌─────────────────────────────────────────┐
          │ DocSets.Editor.Jodit                    │
          │ net472 + net8.0-windows                 │
          │ общий WebView2/Jodit-редактор заметок   │
          └─────────────────────────────────────────┘
```

Допустимое направление зависимостей — только сверху вниз в приведённой цепочке:
`Model <- Serialization <- Core <- host`. Обратные ссылки и зависимости
`netstandard2.0`-сборок от Visual Studio SDK запрещены.

### DocSets.Model (`netstandard2.0`)

Содержит:

- `DocumentItem`, `DocumentSetsState` и связанные перечисления;
- модель тегов, источников и локального состояния;
- DTO каталожного формата DocSet;
- переносимые описания ссылок `DocumentLink`.

В этой сборке нет файлового ввода-вывода и платформенного UI.

### DocSets.Serialization (`netstandard2.0`)

Содержит:

- `DirectoryDocSetStore`;
- `DocSetDocumentRepository`;
- `DocSetsWorkspaceStore`;
- `AssetStorageService`;
- общий журнал `DocSetsLog`.

Сборка отвечает за преобразование модели в файлы и обратно, атомарное сохранение,
пути источников и локальное хранилище изображений.

### DocSets.Core (`netstandard2.0`)

Содержит платформенно-независимые операции:

- `DocumentTreeService`;
- `BookmarkSearchService`;
- `TagService` и `RecentBookmarksService`;
- `NavigationHistoryService` и `PinService`;
- `UndoRedoService`;
- форматирование краткого представления заметок;
- поиск и проверку источников кода;
- контракты host-среды.

### DocSets.Editor.Jodit (`net472;net8.0-windows`)

Общий редактор HTML-заметок. Он собирается для обеих сред и содержит один набор
Jodit/Prism-ресурсов. VSIX использует WebView2 из Visual Studio, Desktop — пакет
WebView2 Runtime. Редактор не знает о DTE, Roslyn и составе основного окна.

### VSIX (`net472`)

Содержит интеграцию с Visual Studio и размещает общий UI расширения:

- `AsyncPackage`, команды и Tool Window;
- `RoslynBookmarkResolver`;
- `EditorStateService` и `FileBookmarkTrackingService`;
- реализации узких сервисов Visual Studio;
- composition root, создающий общий `DocSetsViewModel` и `DocSetsWinFormsControl`.

### DocSets.Desktop (`net8.0-windows`)

Самостоятельное приложение содержит:

- лёгкую оболочку `MainForm` над общим `DocSetsWinFormsControl`;
- Desktop-реализации узких внешних сервисов;
- то же дерево, свойства, поиск, код, preview, заметку и лог, что и VSIX;
- открытие, создание и сохранение каталога `*.DocSets`;
- локальные настройки окна и раскладки общего контрола;
- общий Jodit-редактор заметок.

## 3. Инверсия платформенных зависимостей

Платформенные операции описаны узкими интерфейсами в
`DocSets.Core/Abstractions`: `IDocSetWorkspaceService`,
`ISolutionContextService`, `IUserDialogService`, `IClipboardService`,
`INavigationService`, `IActiveDocumentService`, `IPreviewService` и
`IEditorTrackingService`. Интерфейса, дублирующего API ViewModel, и единого
«гигантского» host-service нет.

Обе композиции создают один тип `DocSetsViewModel`, передавая разные реализации
этих сервисов через конструктор. ViewModel и общий UI не создают Visual Studio-
сервисы внутри себя. Неподдерживаемые в Desktop IDE-функции представлены
явными Desktop-реализациями с безопасным ограниченным поведением.

## 4. Runtime-композиция

### Visual Studio

```text
DocSetsPackage
  └─ DocSetsToolWindow
      └─ DocSetsWinFormsHostControl (composition root)
          ├─ DocSetWorkspaceService : IDocSetWorkspaceService
          ├─ VisualStudio*Service / FileBookmarkTrackingService
          ├─ DocSetsViewModel
          └─ DocSetsWinFormsControl
```

### Desktop

```text
Program
  └─ MainForm (composition root)
      ├─ DocSetWorkspaceService : IDocSetWorkspaceService
      ├─ Desktop*Service
      ├─ DocSetsViewModel
      └─ DocSetsWinFormsControl
```

DI-контейнер пока не нужен: зависимости явно собираются в composition root каждой
среды. Это сохраняет граф прозрачным и позволяет тестировать сервисы напрямую.

## 5. Модель и хранение

`DocumentSetsState` содержит корень дерева. Универсальный `DocumentItem` может быть
папкой, закладкой на символ, файл, локальной Pin-ссылкой или служебным узлом.
Изменения коллекций и свойств поднимают `TreeChanged` до корня.

Каталожный документ имеет вид:

```text
Example.DocSets/
  docset.json
  assets/
    images/
```

Пути закладок разрешаются через `Sources`. Источник по умолчанию не записывается в
каждую ссылку. Ссылка без имени source всегда относится к локальному source-default
текущего DocSet. Assets адресуются переносимыми ссылками `asset:...`.

Сохранение выполняется репозиторием атомарно через временный файл. Персональные
настройки VSIX хранятся под `.vs`, Desktop — под
`%LocalAppData%/DocSets/Desktop`.

## 6. Состояние переиспользования UI

Бизнес-логика, сериализация и Jodit являются отдельными сборками. `Aga.Controls`
переведён на SDK-style и собирается для `net472` и `net8.0-windows` без изменения
публичного интерфейса. `DocSets.UI.WinForms` также является настоящей multi-target
сборкой и содержит единственный `DocSetsWinFormsControl`, дерево `TreeViewAdv`,
панели и UI-команды. VSIX и Desktop ссылаются на эту сборку как на проект; linked
files и копий Desktop-панелей больше нет.

Единственный `DocSetsViewModel` находится в `DocSets.Core`. Различия сред
локализованы в реализациях узких сервисов и composition root. Внешний Jodit-
редактор использует тот же ViewModel и тот же контрол связи с выбранной заметкой.

## 7. Правила развития

- Новое поле документа сначала добавляется в `DocSets.Model`, затем в DTO и
  преобразования `DocSets.Serialization`.
- Операции над деревом и поиском реализуются в `DocSets.Core` без ссылок на UI.
- Любое обращение к DTE, Roslyn или активному редактору остаётся в VSIX-адаптере.
- Общий исходный код не подключается через linked files.
- Форматы, спецификации и комментарии в исходниках документируются на русском.
- Host не должен молча подменять недоступную функцию: он возвращает результат или
  явно сообщает пользователю, что операция в этой среде не поддерживается.
- Сохранение не выполняется на каждое промежуточное событие WebView2; редактор
  фиксирует итоговую ревизию при явном сохранении, потере фокуса или idle.

## 8. Проверка архитектурных изменений

После изменения границ сборок необходимо проверить:

1. сборку `DocSets.Model`, `DocSets.Serialization` и `DocSets.Core` для
   `netstandard2.0`;
2. сборку `DocSets.Editor.Jodit` для `net472` и `net8.0-windows`;
3. сборку VSIX на .NET Framework 4.7.2;
4. сборку и запуск `DocSets.Desktop` на .NET 8;
5. сборку импортера, модульных и интеграционных тестов;
6. открытие одного DocSet в обеих средах без изменения формата;
7. round trip заметок, изображений, ссылок и source-default;
8. отсутствие ссылок Visual Studio SDK в трёх `netstandard2.0`-сборках.
