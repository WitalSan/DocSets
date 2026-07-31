using System;
using System.Windows;

namespace DocSets
{
    /// <summary>
    /// Реализация текстового буфера обмена для Windows.
    /// </summary>
    internal sealed class WindowsClipboardService : IClipboardService
    {
        public bool TryGetText(out string text)
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    text = string.Empty;
                    return false;
                }

                text = Clipboard.GetText() ?? string.Empty;
                return true;
            }
            catch
            {
                text = string.Empty;
                return false;
            }
        }

        public void SetText(string text)
        {
            var data = new DataObject();
            data.SetText(text ?? string.Empty);
            Clipboard.SetDataObject(data, true);
        }
    }
}
