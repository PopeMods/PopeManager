using Qt.Quick;

[assembly: Qt.Generate(
    MainIncludes = "#include <QtQuickControls2/QQuickStyle>\n#include <QGuiApplication>\n#include <QIcon>\n#include <QCoreApplication>",
    MainStartingUp = "QQuickStyle::setStyle(QStringLiteral(\"Material\"));",
    MainBeforeAppExec = "QGuiApplication::setWindowIcon(QIcon(QCoreApplication::applicationDirPath() + QStringLiteral(\"logo.png\")));",
    Packages = "QuickControls2",
    Libraries = "Qt6::QuickControls2")]

namespace PopeManager
{
    public class Program
    {
        internal static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                File.AppendAllText(Path.Combine(AppPaths.Data, "crash.log"), e.ExceptionObject + Environment.NewLine);
            if (args.Contains("--install", StringComparer.OrdinalIgnoreCase))
            {
                Environment.ExitCode = RunHeadlessInstall();
                return;
            }
            Qml.LoadFromRootModule("Main");
            Qml.WaitForExit();
        }

        private static int RunHeadlessInstall()
        {
            try
            {
                return new ManagerBackend().RunHeadlessInstall(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(e.Message);
                File.AppendAllText(Path.Combine(AppPaths.Data, "crash.log"), e + Environment.NewLine);
                return 1;
            }
        }
    }
}
