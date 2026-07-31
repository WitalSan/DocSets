namespace DocSets
{
    /// <summary>
    /// Отдельная сессия экспериментального HTML-редактора Jodit.
    /// Общая логика выбора, сохранения и навигации предоставляется HTML-хостом.
    /// </summary>
    public sealed class DocSetsJoditCommentWindowControl
        : DocSetsHtmlCommentWindowControl
    {
        public DocSetsJoditCommentWindowControl()
            : base(new JoditCommentControl(), "Jodit")
        {
        }
    }
}
