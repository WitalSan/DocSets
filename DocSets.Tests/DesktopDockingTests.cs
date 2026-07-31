using DocSets.Desktop.Panels;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using WeifenLuo.WinFormsUI.Docking;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class DesktopDockingTests
    {
        private static readonly string[] _expectedIds =
        {
            DocSetsPanelIds.Tree,
            DocSetsPanelIds.Properties,
            DocSetsPanelIds.Code,
            DocSetsPanelIds.Preview,
            DocSetsPanelIds.Note,
            DocSetsPanelIds.Search,
            DocSetsPanelIds.Log
        };

        [TestMethod]
        public void AllDesktopPanelsAreRegisteredInCatalog()
        {
            Assert.SequenceEqual(_expectedIds,
                DesktopPanelCatalog.Definitions.Select(x => x.PersistId));
        }

        [TestMethod]
        public void DesktopPersistIdsAreUnique()
        {
            var ids = DesktopPanelCatalog.Definitions.Select(x => x.PersistId).ToArray();
            Assert.Equal(ids.Length,
                ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [TestMethod]
        public void PanelsMenuContainsEveryRegisteredPanel()
        {
            using (var fixture = new DockFixture())
            using (var menu = new ContextMenuStrip())
            {
                fixture.Manager.PopulateMenu(menu.Items);
                Assert.SequenceEqual(_expectedIds,
                    menu.Items.Cast<ToolStripItem>().Select(x => x.Tag as string));
            }
        }

        [TestMethod]
        public void PanelCanBeHiddenAndShownAgain()
        {
            using (var fixture = new DockFixture())
            {
                fixture.Manager.Show(DocSetsPanelIds.Tree);
                Assert.True(fixture.Manager.IsVisible(DocSetsPanelIds.Tree));
                fixture.Manager.Hide(DocSetsPanelIds.Tree);
                Assert.False(fixture.Manager.IsVisible(DocSetsPanelIds.Tree));
                fixture.Manager.Show(DocSetsPanelIds.Tree);
                Assert.True(fixture.Manager.IsVisible(DocSetsPanelIds.Tree));
            }
        }

        [TestMethod]
        public void ResetLayoutShowsDefaultPanelSet()
        {
            using (var fixture = new DockFixture())
            {
                fixture.Manager.ResetLayout();
                foreach (var definition in DesktopPanelCatalog.Definitions)
                {
                    Assert.Equal(definition.VisibleByDefault,
                        fixture.Manager.IsVisible(definition.PersistId));
                    if (definition.VisibleByDefault)
                        Assert.Equal(definition.DefaultDockState,
                            fixture.Manager.Panels[definition.PersistId].DockState);
                }
            }
        }

        [TestMethod]
        public void SharedPanelControlsComeFromWinFormsUiAssembly()
        {
            var assembly = typeof(DocSetsWinFormsControl).Assembly;
            var types = new[]
            {
                typeof(DocSetsTreeControl),
                typeof(DocSetsPropertiesControl),
                typeof(DocSetsCodeControl),
                typeof(DocSetsPreviewControl),
                typeof(DocSetsJoditControl),
                typeof(DocSetsSearchControl),
                typeof(DocSetsLogPanelControl)
            };
            Assert.True(types.All(type => type.Assembly == assembly));
            Assert.True(types.All(type => typeof(DocSetsPanelControl).IsAssignableFrom(type)));
        }

        private sealed class DockFixture : IDisposable
        {
            private readonly Form _form;
            private readonly List<TestPanelControl> _controls;

            public DockFixture()
            {
                var dockPanel = new DockPanel
                {
                    Dock = DockStyle.Fill,
                    DocumentStyle = DocumentStyle.DockingWindow,
                    Theme = new VS2015BlueTheme()
                };
                _form = new Form();
                _form.Controls.Add(dockPanel);
                _form.CreateControl();
                dockPanel.CreateControl();
                Manager = new DesktopDockManager(dockPanel);
                _controls = DesktopPanelCatalog.Definitions
                    .Select(x => new TestPanelControl(x.PersistId, x.Title))
                    .ToList();
                Manager.Register(_controls);
            }

            public DesktopDockManager Manager { get; }

            public void Dispose()
            {
                _form.Dispose();
            }
        }

        private sealed class TestPanelControl : DocSetsPanelControl
        {
            public TestPanelControl(string persistId, string title)
                : base(persistId, title, new Panel())
            {
            }
        }
    }
}
