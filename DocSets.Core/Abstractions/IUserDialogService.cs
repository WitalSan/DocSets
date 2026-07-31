namespace DocSets
{
    /// <summary>
    /// Платформенные диалоги, необходимые общей логике DocSets.
    /// </summary>
    public interface IUserDialogService
    {
        string Prompt(string caption, string label, string initialValue = "");
        bool Confirm(string message, string caption);
        void ShowInformation(string message, string caption = "DocSets");
        void ShowError(string message, string caption = "DocSets");
    }
}
