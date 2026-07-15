namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class DpiServiceTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LogicalPixelsStayUnchangedAt96Dpi()
        {
            Assert.Equal(16, DpiService.Scale(16, 96));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LogicalPixelsScaleAtCommonDpiValues()
        {
            Assert.Equal(20, DpiService.Scale(16, 120));
            Assert.Equal(24, DpiService.Scale(16, 144));
            Assert.Equal(32, DpiService.Scale(16, 192));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void PhysicalPixelsConvertBetweenMonitors()
        {
            Assert.Equal(32, DpiService.ScaleBetween(16, 96, 192));
            Assert.Equal(16, DpiService.ScaleBetween(32, 192, 96));
        }
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ZeroRemainsZero()
        {
            Assert.Equal(0, DpiService.Scale(0, 192));
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void DpiBelow96UsesSafeBaseline()
        {
            Assert.Equal(16, DpiService.Scale(16, 72));
        }
    }
}