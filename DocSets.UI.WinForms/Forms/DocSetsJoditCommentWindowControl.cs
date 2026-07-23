namespace DocSets
{
    /// <summary>
    /// Отдельная сессия экспериментального HTML-редактора Jodit.
    /// Общая логика выбора, сохранения и навигации наследуется от CKEditor.
    /// </summary>
    internal sealed class DocSetsJoditCommentWindowControl
        : DocSetsCkEditorCommentWindowControl
    {
        public DocSetsJoditCommentWindowControl()
            : base(new JoditCommentControl(), "Jodit")
        {
        }
    }
}
