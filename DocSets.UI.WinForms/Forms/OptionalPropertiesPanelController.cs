using System;
using System.Windows.Forms;

namespace DocSets
{
    /// <summary>
    /// Подключает старую панель свойств только когда она действительно находится
    /// в видимом дереве основного окна. Наличие экземпляра панели само по себе
    /// не должно запускать загрузку данных или применение изменений.
    /// </summary>
    internal sealed class OptionalPropertiesPanelController
    {
        private readonly BookmarkPropertiesPanel panel;
        private readonly Control host;

        public OptionalPropertiesPanelController(BookmarkPropertiesPanel panel, Control host)
        {
            this.panel = panel ?? throw new ArgumentNullException(nameof(panel));
            this.host = host ?? throw new ArgumentNullException(nameof(host));
        }

        public bool IsActive
        {
            get
            {
                if (host.IsDisposed || panel.IsDisposed) return false;
                Control current = panel;
                while (current != null)
                {
                    if (!current.Visible) return false;
                    if (ReferenceEquals(current, host)) return true;
                    current = current.Parent;
                }
                return false;
            }
        }

        public DocumentItem CurrentItem => IsActive ? panel.CurrentItem : null;

        public void LoadSelection(DocumentItem item, bool multiple, bool allPinned,
            bool anyPinned, BookmarkColor? commonColor, bool canPin)
        {
            if (!IsActive) return;
            panel.LoadSelection(item, multiple, allPinned, anyPinned, commonColor, canPin);
        }

        public string GetPendingChangeDescription()
            => IsActive ? panel.GetPendingChangeDescription() : null;

        public bool ApplyToCurrentItem()
            => IsActive && panel.ApplyToCurrentItem();
    }
}
