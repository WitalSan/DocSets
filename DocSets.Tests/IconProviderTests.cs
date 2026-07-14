using System;

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
            Assert.Equal(18, copy.Width);
            Assert.Equal(18, copy.Height);
            Assert.Equal(24, sync.Width);
            Assert.Equal(24, sync.Height);
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
