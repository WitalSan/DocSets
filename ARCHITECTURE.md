# DocSets architecture

Проект очищен от старой WPF/XAML-реализации. WPF остался только как минимальный `WindowsFormsHost`, необходимый для размещения WinForms-контрола внутри VS ToolWindow.

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

## Удалено

- `DocSetsToolWindowControl.xaml`
- `DocSetsToolWindowControl.xaml.cs`
- `BindingProxy.cs`
- старые WPF-resource привязки
- `UpgradeLog.htm`

`DocSetsToolWindow` теперь создает `DocSetsWinFormsHostControl`, а тот размещает `DocSetsWinFormsControl`.

## Storage id lifecycle

`DocumentItem` does not keep JSON `id`/`parent` at runtime. The runtime model is a physical tree (`Files`/`Children`). During save, `DocumentSet.Items` builds a temporary flat DTO list and generates unique ids from `Name`, then `Name-1`, `Name-2`, etc. During load, the flat DTO list is converted back into the runtime tree and storage ids are discarded.
