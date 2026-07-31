namespace DocSets
{
    /// <summary>
    /// Текстовый буфер обмена, используемый общей логикой DocSets.
    /// </summary>
    public interface IClipboardService
    {
        bool TryGetText(out string text);
        void SetText(string text);
    }
}
