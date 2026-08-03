using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DocSets.Desktop;
    
internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => ReportUnhandled("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            ReportUnhandled("AppDomain", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            ReportUnhandled("Task", e.Exception);
            e.SetObserved();
        };

        try
        {
            Application.Run(new MainForm());
        }
        catch (Exception exception)
        {
            ReportUnhandled("Запуск", exception);
        }
    }

    private static void ReportUnhandled(string category, Exception exception)
    {
        DocSetsLog.Current.Error(category, "Необработанная ошибка Desktop-приложения.", exception);
        try
        {
            MessageBox.Show("Произошла ошибка. Подробности записаны в лог.\r\n\r\n" +
                (exception?.Message ?? "Неизвестная ошибка."), "DocSets",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
