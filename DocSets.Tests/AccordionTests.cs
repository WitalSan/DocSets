using System.Collections.Generic;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocSets.Tests
{
    [TestClass]
    public sealed class AccordionTests
    {
        [TestMethod]
        public void ApplyStateRestoresOrderAndExpansion()
        {
            var host = CreateHost(out var properties, out var comment, out var code);
            host.ApplyState(new[] { "code", "properties", "comment" }, new[] { "code", "comment" });
            Assert.SequenceEqual(new[] { "code", "properties", "comment" }, host.SectionOrder);
            Assert.SequenceEqual(new[] { "code", "comment" }, host.ExpandedSections);
            Assert.True(code.Expanded);
            Assert.True(comment.Expanded);
            Assert.False(properties.Expanded);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ApplyStateAppendsUnknownOrMissingSectionsSafely()
        {
            var host = CreateHost(out _, out _, out _);
            host.ApplyState(new[] { "missing", "comment", "comment" }, new string[0]);
            Assert.SequenceEqual(new[] { "comment", "properties", "code" }, host.SectionOrder);
            Assert.Equal(0, host.ExpandedSections.Count);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ExpansionChangesHeightAndRaisesStateChanged()
        {
            var host = CreateHost(out var properties, out _, out _);
            var changes = 0;
            host.StateChanged += (_, __) => changes++;
            var collapsedHeight = properties.CurrentHeight;
            properties.Expanded = true;
            Assert.True(properties.CurrentHeight > collapsedHeight);
            Assert.Equal(1, changes);
            properties.Expanded = true;
            Assert.Equal(1, changes);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ApplyingStateDoesNotReportUserStateChange()
        {
            var host = CreateHost(out _, out _, out _);
            var changes = 0;
            host.StateChanged += (_, __) => changes++;
            host.ApplyState(new[] { "code", "comment", "properties" }, new[] { "properties" });
            Assert.Equal(0, changes);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void ContentEnabledIsIndependentFromHeaderExpansion()
        {
            var host = CreateHost(out var properties, out _, out _);
            properties.ContentEnabled = false;
            properties.Expanded = true;
            Assert.False(properties.ContentEnabled);
            Assert.True(properties.Expanded);
        }

        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void LayoutCreatesScrollExtentFromExpandedSections()
        {
            var host = CreateHost(out var properties, out var comment, out var code);
            host.Height = 100;
            properties.Expanded = true;
            comment.Expanded = true;
            code.Expanded = true;
            host.PerformLayout();
            Assert.True(host.AutoScrollMinSize.Height > host.ClientSize.Height);
            Assert.Equal(0, properties.Left);
            Assert.True(comment.Top >= properties.Bottom);
            Assert.True(code.Top >= comment.Bottom);
        }

        private static ExperimentalAccordionHost CreateHost(
            out ExperimentalAccordionSection properties,
            out ExperimentalAccordionSection comment,
            out ExperimentalAccordionSection code)
        {
            var host = new ExperimentalAccordionHost { Width = 500, Height = 300 };
            properties = new ExperimentalAccordionSection("properties", "Свойства", new Panel(), 180, false);
            comment = new ExperimentalAccordionSection("comment", "Комментарий", new Panel(), 130, false);
            code = new ExperimentalAccordionSection("code", "Код", new Panel(), 210, false);
            host.AddSection(properties);
            host.AddSection(comment);
            host.AddSection(code);
            return host;
        }
    }
}
