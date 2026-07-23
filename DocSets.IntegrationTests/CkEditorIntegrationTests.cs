using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class CkEditorIntegrationTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void HtmlTableAndFormattingRoundTripThroughCkEditor()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    // У отдельного STA-потока нет контекста WinForms. Без него продолжение
                    // EnsureCoreWebView2Async может не вернуться в оконный поток теста.
                    SynchronizationContext.SetSynchronizationContext(
                        new WindowsFormsSynchronizationContext());
                    var scenario = RunScenarioAsync();
                    while (!scenario.IsCompleted)
                    {
                        Application.DoEvents();
                        Thread.Sleep(10);
                    }
                    scenario.GetAwaiter().GetResult();
                }
                catch (Exception exception) { failure = exception; }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            if (!thread.Join(TimeSpan.FromMinutes(2)))
                throw new TimeoutException("Интеграционный тест CKEditor не завершился за две минуты.");
            if (failure != null) throw new Exception("Ошибка интеграционного теста CKEditor.", failure);
        }

        private static async Task RunScenarioAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "DocSets.CkEditorIntegrationTests",
                Guid.NewGuid().ToString("N"));
            var assets = Path.Combine(root, "assets");
            Directory.CreateDirectory(Path.Combine(assets, "images"));
            try
            {
                using (var form = new Form { Width = 900, Height = 600, ShowInTaskbar = false })
                using (var editor = new CkEditorCommentControl(Path.Combine(root, "WebView2"))
                       { Dock = DockStyle.Fill })
                {
                    editor.SetAssetDirectory(assets);
                    form.Controls.Add(editor);
                    form.Show();
                    try
                    {
                        await WaitUntilAsync(() => editor.IsReady, "CKEditor не инициализирован.");
                    }
                    catch (TimeoutException exception)
                    {
                        throw new TimeoutException(
                            exception.Message + " Этап: " + editor.InitializationStage, exception);
                    }

                    // Пустая HTML-заметка — первый сценарий открытия нового редактора.
                    // Он не должен зависать и обязан вернуть пустое содержимое.
                    editor.LoadComment(string.Empty);
                    await WaitUntilAsync(async () =>
                        string.IsNullOrEmpty(await editor.GetCurrentCommentAsync()),
                        "CKEditor не открыл пустую HTML-заметку.");

                    const string html =
                        "<h2>Проверка CKEditor</h2>" +
                        "<p><span style=\"color:#e91e63\"><strong>Форматированный текст</strong></span></p>" +
                        "<table><tbody><tr><td>Колонка</td><td>Значение</td></tr></tbody></table>";
                    editor.LoadComment(html);
                    await WaitUntilAsync(async () =>
                    {
                        var value = await editor.GetCurrentCommentAsync();
                        return value.Contains("Проверка CKEditor") && value.Contains("<table") &&
                               value.Contains("Колонка") && value.Contains("Значение");
                    }, "CKEditor не вернул HTML с таблицей.");
                }
            }
            finally
            {
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string message)
            => await WaitUntilAsync(() => Task.FromResult(condition()), message);

        private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
        {
            var limit = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < limit)
            {
                Application.DoEvents();
                if (await condition()) return;
                await Task.Delay(50);
            }
            throw new TimeoutException(message);
        }
    }
}
