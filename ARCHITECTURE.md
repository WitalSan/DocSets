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

