using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace DocSets
{
    public static class DocSetsPanelIds
    {
        public const string Tree = "tree";
        public const string Properties = "properties";
        public const string Code = "code";
        public const string Preview = "preview";
        public const string Note = "note";
        public const string Search = "search";
        public const string Log = "log";
    }

    public abstract class DocSetsPanelControl : UserControl
    {
        protected DocSetsPanelControl(string persistId, string title, Control content)
        {
            if (string.IsNullOrWhiteSpace(persistId))
                throw new ArgumentException("Не задан идентификатор панели.", nameof(persistId));
            if (content == null)
                throw new ArgumentNullException(nameof(content));

            PersistId = persistId;
            Title = title ?? persistId;
            Dock = DockStyle.Fill;
            content.Dock = DockStyle.Fill;
            Controls.Add(content);
        }

        public string PersistId { get; }
        public string Title { get; }
    }

    public sealed class DocSetsTreeControl : DocSetsPanelControl
    {
        internal DocSetsTreeControl(Control content)
            : base(DocSetsPanelIds.Tree, "DocSets", content)
        {
        }
    }

    public sealed class DocSetsPropertiesControl : DocSetsPanelControl
    {
        internal DocSetsPropertiesControl(Control content)
            : base(DocSetsPanelIds.Properties, "Свойства", content)
        {
        }
    }

    public sealed class DocSetsCodeControl : DocSetsPanelControl
    {
        internal DocSetsCodeControl(Control content)
            : base(DocSetsPanelIds.Code, "Код", content)
        {
        }
    }

    public sealed class DocSetsPreviewControl : DocSetsPanelControl
    {
        internal DocSetsPreviewControl(Control content)
            : base(DocSetsPanelIds.Preview, "Preview", content)
        {
        }
    }

    public sealed class DocSetsJoditControl : DocSetsPanelControl
    {
        internal DocSetsJoditControl(Control content)
            : base(DocSetsPanelIds.Note, "Заметка", content)
        {
        }
    }

    public sealed class DocSetsSearchControl : DocSetsPanelControl
    {
        internal DocSetsSearchControl(Control content)
            : base(DocSetsPanelIds.Search, "Поиск", content)
        {
        }
    }

    public sealed class DocSetsLogPanelControl : DocSetsPanelControl
    {
        internal DocSetsLogPanelControl(Control content)
            : base(DocSetsPanelIds.Log, "Лог", content)
        {
        }
    }

    /// <summary>
    /// Создаёт один комплект связанных общих панелей для одного экземпляра модели представления.
    /// Каждый контрол принадлежит только одной панели и не переиспользуется между контейнерами.
    /// </summary>
    public sealed class DocSetsPanelComposition : IDisposable
    {
        private readonly IReadOnlyList<DocSetsPanelControl> _panels;

        private DocSetsPanelComposition(
            DocSetsWinFormsControl owner,
            IReadOnlyList<DocSetsPanelControl> panels)
        {
            Owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _panels = panels ?? throw new ArgumentNullException(nameof(panels));
        }

        public DocSetsWinFormsControl Owner { get; }
        public IReadOnlyList<DocSetsPanelControl> Panels => _panels;

        public DocSetsPanelControl GetPanel(string persistId)
            => _panels.FirstOrDefault(x => string.Equals(
                x.PersistId, persistId, StringComparison.OrdinalIgnoreCase));

        public static DocSetsPanelComposition Create(DocSetsViewModel viewModel)
        {
            var owner = new DocSetsWinFormsControl(viewModel);
            var content = owner.DetachPanelsForExternalDocking();
            var panels = new DocSetsPanelControl[]
            {
                new DocSetsTreeControl(owner),
                new DocSetsPropertiesControl(content[DocSetsPanelIds.Properties]),
                new DocSetsCodeControl(content[DocSetsPanelIds.Code]),
                new DocSetsPreviewControl(content[DocSetsPanelIds.Preview]),
                new DocSetsJoditControl(content[DocSetsPanelIds.Note]),
                new DocSetsSearchControl(content[DocSetsPanelIds.Search]),
                new DocSetsLogPanelControl(content[DocSetsPanelIds.Log])
            };
            return new DocSetsPanelComposition(owner, panels);
        }

        public void Dispose()
        {
            foreach (var panel in _panels)
                panel.Dispose();
        }
    }
}
