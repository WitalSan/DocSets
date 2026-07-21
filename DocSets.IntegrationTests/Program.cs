using System;
using System.IO;

namespace DocSets.Tests
{
    internal static class Program
    {
        internal static readonly string TracePath = Path.Combine(
            Path.GetTempPath(), "DocSets.ToastClipboardIntegration.log");

        internal static void Trace(string message)
            => File.AppendAllText(TracePath, DateTime.Now.ToString("O") + " " + message + Environment.NewLine);

        [STAThread]
        private static int Main()
        {
            try
            {
                File.WriteAllText(TracePath, string.Empty);
                Trace("Запуск процесса.");
                new ToastClipboardIntegrationTests()
                    .TextAndImageRoundTripThroughWindowsClipboardAndTwoToastEditors();
                Console.WriteLine("Интеграционный тест TOAST и Clipboard пройден.");
                Trace("Тест пройден.");
                return 0;
            }
            catch (Exception exception)
            {
                Trace("Ошибка: " + exception);
                Console.Error.WriteLine(exception);
                return 1;
            }
        }
    }
}
