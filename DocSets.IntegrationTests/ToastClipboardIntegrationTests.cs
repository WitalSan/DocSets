using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets.Tests
{
    [Microsoft.VisualStudio.TestTools.UnitTesting.TestClass]
    public sealed class ToastClipboardIntegrationTests
    {
        [Microsoft.VisualStudio.TestTools.UnitTesting.TestMethod]
        public void TextAndImageRoundTripThroughWindowsClipboardAndTwoToastEditors()
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
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
                throw new TimeoutException("Интеграционный тест TOAST не завершился за две минуты.");
            if (failure != null) throw new Exception("Ошибка интеграционного теста TOAST.", failure);
        }

        private static async Task RunScenarioAsync()
        {
            Program.Trace("Начало сценария.");
            var root = Path.Combine(Path.GetTempPath(), "DocSets.IntegrationTests", Guid.NewGuid().ToString("N"));
            var assets = Path.Combine(root, "assets");
            var images = Path.Combine(assets, "images");
            Directory.CreateDirectory(images);
            var imagePath = Path.Combine(images, "clipboard-test.png");
            using (var bitmap = new Bitmap(32, 24))
            {
                using (var graphics = Graphics.FromImage(bitmap)) graphics.Clear(Color.CornflowerBlue);
                bitmap.Save(imagePath, ImageFormat.Png);
            }

            try
            {
                using (var form = new Form { Width = 900, Height = 600, ShowInTaskbar = false })
                using (var first = new ToastCommentControl(Path.Combine(root, "WebView2-1")) { Dock = DockStyle.Fill })
                {
                    Program.Trace("Первый ToastCommentControl создан.");
                    form.Controls.Add(first);
                    first.SetAssetDirectory(assets);
                    form.Show();
                    await WaitUntilAsync(() => first.IsReady, "Первый TOAST не инициализирован.");
                    Program.Trace("Первый TOAST готов.");

                    PutHtmlWithImageOnClipboard(imagePath);
                    first.FocusEditorFromHost();
                    await Task.Delay(100);
                    await first.PasteFromClipboardAsync();
                    Program.Trace("Первая вставка отправлена.");
                    var firstMarkdown = await WaitForMarkdownAsync(first);
                    Program.Trace("Первый Markdown получен: " + firstMarkdown);

                    Assert.True(firstMarkdown.Contains("Текст интеграционного теста"));
                    Assert.True(firstMarkdown.Contains("asset:images/clipboard-test.png"));

                    await first.SelectAllAndCopyAsync();
                    Program.Trace("Копирование из первого TOAST выполнено.");
                    await WaitUntilAsync(() => Clipboard.ContainsData(DataFormats.Html),
                        "TOAST не записал HTML в буфер обмена.");

                    using (var secondForm = new Form { Width = 900, Height = 600, ShowInTaskbar = false })
                    using (var second = new ToastCommentControl(Path.Combine(root, "WebView2-2")) { Dock = DockStyle.Fill })
                    {
                        Program.Trace("Второй ToastCommentControl создан.");
                        secondForm.Controls.Add(second);
                        second.SetAssetDirectory(assets);
                        secondForm.Show();
                        await WaitUntilAsync(() => second.IsReady, "Второй TOAST не инициализирован.");
                        Program.Trace("Второй TOAST готов.");
                        second.FocusEditorFromHost();
                        await Task.Delay(100);
                        await second.PasteFromClipboardAsync();
                        Program.Trace("Вторая вставка отправлена.");
                        var secondMarkdown = await WaitForMarkdownAsync(second);
                        Program.Trace("Второй Markdown получен: " + secondMarkdown);
                        Assert.Equal(Normalize(firstMarkdown), Normalize(secondMarkdown));
                    }
                }
            }
            finally
            {
                Clipboard.Clear();
                // Дочерний процесс WebView2 может короткое время удерживать файл
                // BrowserMetrics после Dispose. Это не относится к проверяемому сценарию.
                try { if (Directory.Exists(root)) Directory.Delete(root, true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static void PutHtmlWithImageOnClipboard(string imagePath)
        {
            var fragment = "<p>Текст интеграционного теста</p><p><img src=\"" +
                new Uri(imagePath).AbsoluteUri + "\" alt=\"Тест\"></p>";
            var data = new DataObject();
            data.SetData(DataFormats.Html, ToastCommentControl.BuildClipboardHtml(fragment));
            data.SetData(DataFormats.UnicodeText, "Текст интеграционного теста");
            Clipboard.SetDataObject(data, true);
        }

        private static async Task<string> WaitForMarkdownAsync(ToastCommentControl editor)
        {
            string markdown = null;
            await WaitUntilAsync(async () =>
            {
                markdown = await editor.GetCurrentCommentAsync();
                return markdown.Contains("Текст интеграционного теста") && markdown.Contains("asset:images/");
            }, "TOAST не вставил текст с изображением.");
            return markdown;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, string message)
            => await WaitUntilAsync(() => Task.FromResult(condition()), message);

        private static async Task WaitUntilAsync(Func<Task<bool>> condition, string message)
        {
            var limit = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < limit)
            {
                Application.DoEvents();
                if (await condition()) return;
                await Task.Delay(50);
            }
            throw new TimeoutException(message);
        }

        private static string Normalize(string value)
            => (value ?? string.Empty).Replace("\r\n", "\n").Trim();
    }
}
