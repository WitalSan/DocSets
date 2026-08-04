using System;

namespace DocSets
{

/// <summary>
/// Тип ссылки, которую может вставить HTML-редактор заметки.
/// </summary>
public enum DocumentLinkKind
{
    Symbol,
    File,
    Bookmark,
    Url
}

/// <summary>
/// Независимое от Visual Studio представление ссылки DocSets.
/// </summary>
[Serializable]
public sealed class DocumentLink
{
    public const string EmbeddedBookmarkPrefix = "embedded-v1:";

    public DocumentLinkKind Kind { get; set; }
    public string Caption { get; set; }
    public string Target { get; set; }
    public string Project { get; set; }
    public string SourceId { get; set; }
}
}
