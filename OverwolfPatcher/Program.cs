using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OverwolfPatcher
{
    class Program
    {
        static int Main(string[] args)
        {
            // Double-clicking the exe should auto-launch premium mode instead of
            // showing help, so non-technical users get the patched experience
            // without touching PowerShell.
            var launchedWithoutArgs = args.Length == 0;
            if (launchedWithoutArgs)
            {
                EnsureVisibleConsole();
                args = new[] { "instrument", "--mode", "premium" };
            }
            try
            {
                var exitCode = Testing.PremiumCommand.Run(args, launchedWithoutArgs);
                if (launchedWithoutArgs) PauseIfInteractive();
                return exitCode;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("FAILED: " + error.Message);
                if (launchedWithoutArgs) PauseIfInteractive();
                return 1;
            }
        }

        // Explorer gives a double-clicked console exe its own console, but stdout
        // is redirected rather than attached, so the window would close the
        // instant the process exits and take the error text with it.
        static void EnsureVisibleConsole()
        {
            try
            {
                if (Console.IsOutputRedirected || Console.IsInputRedirected)
                {
                    if (!AttachConsole(AttachParentProcess)) AllocConsole();
                    RebindConsoleStreams();
                }
                var window = GetConsoleWindow();
                if (window != IntPtr.Zero) ShowWindow(window, ShowNormal);
            }
            catch
            {
                // A hidden console is cosmetic; never block the run because it failed.
            }
        }

        static void RebindConsoleStreams()
        {
            var output = Console.OpenStandardOutput();
            if (output != Stream.Null) Console.SetOut(new StreamWriter(output) { AutoFlush = true });
            var error = Console.OpenStandardError();
            if (error != Stream.Null) Console.SetError(new StreamWriter(error) { AutoFlush = true });
        }

        static void PauseIfInteractive()
        {
            try
            {
                if (!Environment.UserInteractive) return;
                if (Console.IsInputRedirected || Console.IsOutputRedirected) return;
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey(true);
            }
            catch
            {
                // Never block shutdown because the pause itself failed.
            }
        }

        const int AttachParentProcess = -1;
        const int ShowNormal = 1;

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool AllocConsole();

        [DllImport("kernel32.dll")]
        static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        static extern bool ShowWindow(IntPtr window, int command);
    }
}
