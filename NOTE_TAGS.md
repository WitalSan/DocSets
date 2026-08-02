# Теги внутри заметок DocSets (`NoteTag`)

## Назначение и граница модели

`NoteTag` — семантическая метка фрагмента содержимого заметки. Система введена для
переноса тегов OneNote и не имеет отношения к тегам узлов дерева DocSets.

Существующая система дерева остаётся отдельной:

- `TagDefinition` описывает тег узла дерева;
- `DocumentItem.TagIds` назначает такие теги папкам и заметкам;
- `TagService`, меню `Tags` и фильтр дерева работают только с ними.

Новая система содержимого:

- `NoteTagStyle` описывает внешний вид и поведение метки в тексте;
- каталог стилей хранится в `docsets.json` в отдельном массиве `noteTagStyles`;
- `NoteTag` описывает один экземпляр метки;
- экземпляр хранится непосредственно в HTML заметки;
- `NoteTag` не добавляет значения в `DocumentItem.TagIds` и не участвует в фильтрации дерева.

## Модель

```csharp
public enum NoteTagBehavior
{
    Marker,
    Toggle,
    Checkbox
}

public sealed class NoteTagStyle
{
    public string Id { get; set; }
    public string Name { get; set; }
    public string Icon { get; set; }
    public string Color { get; set; }
    public NoteTagBehavior Behavior { get; set; }
    public string Source { get; set; }
    public string SourceId { get; set; }
}

public sealed class NoteTag
{
    public string Id { get; set; }
    public string StyleId { get; set; }
    public bool? IsCompleted { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string Source { get; set; }
    public string SourceId { get; set; }
}
```

Поведение:

- `Marker` — визуальная метка без изменяемого состояния;
- `Toggle` — переключаемая метка DocSets; предусмотрена моделью, но не назначается
  автоматически обычным маркерам OneNote;
- `Checkbox` — флажок с состоянием и датой завершения.

Для `Marker` поля `IsCompleted` и `CompletedAt` равны `null`. Это существенно:
OneNote COM возвращает атрибут `completed` и у непереключаемых маркеров, но он не
означает пользовательский флажок.

## Представление в HTML

```html
<span class="docsets-note-tag"
      data-docsets-note-tag-id="73d..."
      data-docsets-tag-style-id="onenote-..."
      data-docsets-tag-state="active"
      data-docsets-tag-behavior="checkbox"
      data-docsets-tag-icon="checkbox"
      data-docsets-tag-name="Дела"
      data-docsets-note-tag-source="onenote"
      data-docsets-note-tag-source-id="{page}/{object}/tag-0">
  <span class="docsets-note-tag-icon" contenteditable="false"></span>
  <span class="docsets-note-tag-content">Сделать импорт вложений</span>
</span>
```

Допустимые состояния `data-docsets-tag-state`:

- `active`;
- `completed`;
- `disabled`.

Для завершённого флажка сохраняется
`data-docsets-note-tag-completed-at="2026-08-02T12:37:18.0000000+00:00"`.

Стиль и поведение дублируются в HTML намеренно: заметка должна корректно
отображаться в Jodit сразу после загрузки. Каталог `NoteTagStyle` остаётся
каноническим описанием семантики и переносится вместе с копируемыми узлами.

## Источник OneNote COM

При `GetPageContent(..., PageInfo.piAll, XMLSchema.xs2013)` OneNote возвращает:

```xml
<one:TagDef index="0" type="0" symbol="3"
            fontColor="automatic" highlightColor="none" name="Дела" />

<one:OE objectID="{...}">
  <one:Tag index="0" completed="true" disabled="false"
           creationDate="..." completionDate="..." />
  <one:T>Готовая задача</one:T>
</one:OE>
```

`TagDef` имеет область страницы. Поэтому импортёр сначала строит таблицу по
`index`, затем преобразует каждый `Tag` внутри `OE`. Одинаковые определения на
разных страницах объединяются по устойчивому идентификатору, рассчитанному из
имени, типа, символа, цвета и поведения.

Сохраняются:

- имя определения;
- `type` и `symbol` в `NoteTagStyle.SourceId`;
- цвет шрифта или подсветки, если OneNote задал его явно;
- состояние `completed` и `completionDate` для флажков;
- `disabled` как состояние HTML;
- Page ID, Object ID и индекс тега в `NoteTag.SourceId`;
- сведения об определениях и экземплярах в отчёте импорта.

Если `Tag` ссылается на отсутствующий `TagDef`, содержимое не исчезает: импортёр
добавляет диагностический placeholder и запись `NotImported` в отчёт.

## Встроенные варианты OneNote

Импортёр распознаёт стандартные варианты OneNote: To Do, Important, Question,
Definition, Highlight, Contact, Address, Phone Number, Web Site, Idea, Password,
Critical, Project A/B, Remember for Later, Movie, Book, Music, Source for Article,
Remember for Blog, Discuss with Person A/B/Manager, Send in Email, Schedule Meeting,
Call Back, To Do Priority 1/2 и Client Request.

Флажками считаются только варианты OneNote, для которых предусмотрено состояние
завершения: To Do, Discuss with Person A/B/Manager, Schedule Meeting, Call Back,
To Do Priority 1/2 и Client Request. Остальные импортируются как `Marker`.

Неизвестные и пользовательские определения не отбрасываются: сохраняются имя,
цвет и исходные числовые параметры, а для изображения используется нейтральная
иконка.

## Отображение и редактирование

Jodit отображает отдельную иконку перед помеченным фрагментом. Поддерживаются
иконки стандартных вариантов OneNote и нейтральная иконка для неизвестных.

Щелчок по иконке:

- у `Marker` ничего не изменяет;
- у `Toggle` переключает `active/completed`;
- у `Checkbox` переключает `active/completed` и добавляет либо удаляет
  `CompletedAt`.

Изменение синхронизируется через обычный механизм Jodit и сохраняется как часть
HTML заметки. Теги не запускают код и не обращаются к OneNote после импорта.

## Хранение и перенос

Пример отдельного раздела манифеста:

```json
{
  "tags": [],
  "noteTagStyles": [
    {
      "id": "onenote-...",
      "name": "Дела",
      "icon": "checkbox",
      "color": "#107c10",
      "behavior": "Checkbox",
      "source": "onenote",
      "sourceId": "type=0;symbol=3"
    }
  ]
}
```

Массивы `tags` и `noteTagStyles` независимы. Совпадение их ID не связывает
системы и не создаёт назначение тегу дерева.

При копировании узлов внутренний буфер DocSets находит используемые в HTML
`data-docsets-tag-style-id` и переносит только соответствующие `NoteTagStyle`.
Это также выполняется отдельно от переноса `TagDefinition`/`TagIds`.

## Проверки

Интеграционный тест на книге `-OneNote.Test-` проверяет:

- три реальных `TagDef` (`Дела`, `Важно`, `Вопрос`);
- пять реальных экземпляров `Tag`;
- преобразование `Дела` в `Checkbox`;
- преобразование `Важно` и `Вопрос` в `Marker`;
- единственное фактически завершённое задание;
- наличие HTML-атрибутов и записей отчёта для каждого экземпляра.

Тест Jodit проверяет переключение checkbox и сохранение времени завершения, а
тест хранилища — round-trip отдельного каталога `noteTagStyles` через
`docsets.json`.
