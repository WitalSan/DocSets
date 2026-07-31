using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace DocSets.Tests
{
    [TestClass]
    public class RelayCommandTests
    {
        [TestMethod]
        public void ExecuteAndCanExecuteUseProvidedDelegates()
        {
            var executed = false;
            var enabled = false;
            var command = new RelayCommand(() => executed = true, () => enabled);

            Assert.False(command.CanExecute(null));
            enabled = true;
            Assert.True(command.CanExecute(null));

            command.Execute(null);

            Assert.True(executed);
        }

        [TestMethod]
        public void RaiseCanExecuteChangedNotifiesSubscribers()
        {
            var notifications = 0;
            var command = new RelayCommand(() => { });
            command.CanExecuteChanged += (_, __) => notifications++;

            command.RaiseCanExecuteChanged();

            Assert.Equal(1, notifications);
        }
    }
}
