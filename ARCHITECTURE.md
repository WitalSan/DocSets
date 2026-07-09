# DocSets architecture

## Структура

```text
DocSets.Core/
  Models/      JSON-модели, состояние, настройки UI
  Storage/     загрузка/сохранение рядом с активным .sln
  Roslyn/      определение и открытие закладок по символам
  Services/    команды и состояние DocSets

DocSets.UI.WinForms/
  TreeViewAdv/ адаптеры модели TreeViewAdv
  Forms/       основной WinForms UI и диалоги
  Editors/     место для inline/editor-контролов
  ContextMenu/ место для меню узлов/колонок
  ToolStrip/   место для панели групп

DocSets.Vsix/
  Package/     AsyncPackage и команда открытия окна
  ToolWindow/  ToolWindowPane
  WinFormsHost/тонкая WPF-обертка WindowsFormsHost

Aga.Controls/  сторонний TreeViewAdv, без бизнес-логики DocSets
```


## Shared workspace storage

DocSets can be shared by several solutions/microservices. On load the extension searches from the current solution directory upward for a file named `DocSets.workspace.json`.

If the file is found:

- it is used instead of the old per-solution `<SolutionName>.docsets.json` file;
- all bookmark paths are saved relative to the directory that contains `DocSets.workspace.json`;
- opening another solution under the same parent tree shows the same sets and bookmarks.

If the file is not found, the old behavior is preserved: settings are stored next to the current solution in `<SolutionName>.docsets.json`.

Recommended layout for microservices:

```text
RepoRoot/
  DocSets.workspace.json
  ServiceA/ServiceA.sln
  ServiceB/ServiceB.sln
  Shared/Shared.sln
```

`DocSets.workspace.json` may initially contain an empty state:

```json
{
  "activeSet": "Default",
  "sets": [
    { "name": "Default", "items": [] }
  ],
  "ui": { "columns": [] }
}
```

You can also copy/rename an existing `<SolutionName>.docsets.json` to `DocSets.workspace.json`; paths will then be interpreted relative to the workspace file directory.
