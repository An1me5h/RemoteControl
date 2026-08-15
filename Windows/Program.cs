using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RemoteControl;

static class Program
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    static void Main()
    {
        // GUI subsystem (see RemoteControl.csproj) never allocates a console on its own,
        // so double-clicking the exe - or launching it from the Startup folder / Task
        // Scheduler for a real background start - runs silently with just the tray icon,
        // no window to close. Launching from an existing terminal (dotnet run, or the exe
        // directly from cmd/PowerShell) reattaches to that terminal's console instead, so
        // the live packet log still shows up there exactly like before.
        bool hasConsole = AttachConsole(ATTACH_PARENT_PROCESS);
        if (hasConsole)
        {
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError()) { AutoFlush = true });
        }
        else
        {
            // No console to attach to - Console.WriteLine calls throughout TrayApp would
            // otherwise hit an invalid handle. Routing to TextWriter.Null makes every call
            // site a safe no-op without having to special-case each one.
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new TrayApp(hasConsole));
    }
}
