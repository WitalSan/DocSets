using System;
using System.Linq;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class IconProviderTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LoadsAndScalesCopyAndSyncResources()
        {
            var copy = IconProvider.Get(AppIcon.Copy, 18);
            var sync = IconProvider.Get(AppIcon.Sync, 24);
            var play = IconProvider.Get(AppIcon.Play, 20);
            var pause = IconProvider.Get(AppIcon.Pause, 20);
            var delete = IconProvider.Get(AppIcon.Delete, 20);
            Assert.Equal(18, copy.Width);
            Assert.Equal(18, copy.Height);
            Assert.Equal(24, sync.Width);
            Assert.Equal(24, sync.Height);
            Assert.Equal(20, play.Width);
            Assert.Equal(20, pause.Width);
            Assert.Equal(20, delete.Width);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ImportReportUsesDpiAutoscalingAndSharedToolbarIcons()
        {
            using (var report = new OneNoteImportReportDialog())
            {
                report.CreateControl();
                Assert.Equal(System.Windows.Forms.AutoScaleMode.Dpi, report.AutoScaleMode);
                var toolbar = FindToolStrip(report);
                Assert.NotNull(toolbar);
                var buttons = toolbar.Items.OfType<System.Windows.Forms.ToolStripButton>().ToArray();
                Assert.True(buttons.Length >= 3, "Import toolbar must contain three command buttons.");
                Assert.True(buttons.Take(3).All(x => x.Image != null), "Every import command button must have an icon.");
                Assert.Equal(DpiService.IconSize(report, 16), toolbar.ImageScalingSize.Width);
                Assert.True(buttons.Take(3).All(x => x.Image.Width > 0 && x.Image.Height > 0), "Toolbar icons must have a positive size.");
            }
        }

        private static System.Windows.Forms.ToolStrip FindToolStrip(System.Windows.Forms.Control root)
        {
            foreach (System.Windows.Forms.Control child in root.Controls)
            {
                if (child is System.Windows.Forms.ToolStrip strip) return strip;
                var nested = FindToolStrip(child);
                if (nested != null) return nested;
            }
            return null;
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ReturnsCachedImageForSameIconAndSize()
        {
            Assert.Same(IconProvider.Get(AppIcon.Sync, 18), IconProvider.Get(AppIcon.Sync, 18));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void RejectsNonPositiveSize()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => IconProvider.Get(AppIcon.Copy, 0));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DefaultIconSizeIsPositiveAndStable()
        {
            var first = IconProvider.IconSize;
            Assert.True(first > 0);
            Assert.Equal(first, IconProvider.IconSize);
        }
    }
}
