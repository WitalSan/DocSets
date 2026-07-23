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
        private static int Main(string[] args)
        {
            try
            {
                File.WriteAllText(TracePath, string.Empty);
                Trace("Запуск процесса.");
                var filter = args != null && args.Length > 0
                    ? args[0].Trim().ToLowerInvariant()
                    : "all";
                if (filter == "all" || filter == "toast")
                    new ToastClipboardIntegrationTests()
                        .TextAndImageRoundTripThroughWindowsClipboardAndTwoToastEditors();
                if (filter == "all" || filter == "jodit")
                    new JoditIntegrationTests()
                        .HtmlTableFormattingAndAssetLinkRoundTripThroughJodit();
                if (filter == "all" || filter == "drop")
                    new ExternalSymbolDropIntegrationTests()
                        .SameExternalDropPipelineInsertsMarkdownAndHtmlLinks();
                Console.WriteLine("Интеграционные тесты TOAST, Jodit и Clipboard пройдены.");
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
