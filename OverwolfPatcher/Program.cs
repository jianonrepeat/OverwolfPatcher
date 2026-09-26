using System;

namespace OverwolfPatcher
{
    class Program
    {
        static int Main(string[] args)
        {
            // Double-clicking the exe should auto-launch premium mode instead of
            // showing help, so non-technical users get the patched experience
            // without touching PowerShell.
            if (args.Length == 0) args = new[] { "instrument", "--mode", "premium" };
            var launchedWithoutArgs = false;
            try
            {
                var exitCode = Testing.PremiumCommand.Run(args);
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
    }
}
