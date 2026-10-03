using System;
// Command entry point retained in the OverwolfPatcher.Testing namespace for compatibility.
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using Microsoft.Win32;
using Mono.Cecil;

namespace OverwolfPatcher.Testing
{
    internal static class PremiumCommand
    {
        private const string Outplayed = "cghphpbjeabdkomiphingnegihoigeggcfphdofo";
        private const string ProfilerClsid = "{4D7C38E9-7C8A-4F5D-9D2C-1D3E7BC9F1A4}";

        private sealed class KnownProfile
        {
            public KnownProfile(string version, string hash, bool uidGetterStatic)
            {
                Version = version;
                Hash = hash;
                UidGetterStatic = uidGetterStatic;
            }
            public string Version { get; }
            public string Hash { get; }
            public bool UidGetterStatic { get; }
        }

        private static readonly KnownProfile[] KnownProfiles = new[]
        {
            new KnownProfile("0.311.0.6",
                "2C809D434B23E2F7753E79B1B3EEED3BA2DE5CA7D4D8D425D83CB641255ACE8E",
                false),
            new KnownProfile("0.310.1.1",
                "CCE1BFBE33A0DAA6189475583C6680CFC0043F015B1691A23AE8CF23CB45DE2D",
                true),
        };

        private static KnownProfile ProfileByHash(string hash)
        {
            return KnownProfiles.FirstOrDefault(p => string.Equals(p.Hash, hash, StringComparison.OrdinalIgnoreCase));
        }

        private static KnownProfile ProfileByVersion(string version)
        {
            return KnownProfiles.FirstOrDefault(p => string.Equals(p.Version, version, StringComparison.Ordinal));
        }

        private static bool IsKnownVersion(string version)
        {
            return ProfileByVersion(version) != null;
        }
        internal static int Run(string[] args, bool autoCloseOverwolf = false)
        {
            // Double-clicking the exe passes no args. Probing the install as
            // "status" then throws on missing/updated installs and the temporary
            // console closes at once (issue #24). Show help instead so the
            // window has something readable before Program pauses.
            if (args.Length == 0) args = new[] { "--help" };
            var command = args[0].ToLowerInvariant();
            if (command == "--help" || command == "help")
            {
                Console.WriteLine("OverwolfPatcher status|stage|apply|restore|baseline|instrument [--install DIR] [--output DIR] [--backup DIR]");
                Console.WriteLine("Run from PowerShell so output stays visible; double-clicking only shows this help.");
                Console.WriteLine("Examples:");
                Console.WriteLine("  OverwolfPatcher.exe status");
                Console.WriteLine("  OverwolfPatcher.exe baseline --entry launcher --wait-ms 5000");
                Console.WriteLine("stage/apply default to Outplayed plan 61. Other apps: --app EXTENSION_ID --plans 1,2");
                Console.WriteLine("status is read-only. stage writes a copy. apply/restore require Overwolf to be closed.");
                Console.WriteLine("baseline launches OverwolfLauncher.exe without profiling; use --entry managed to reproduce direct Overwolf.exe.");
                Console.WriteLine("instrument launches OverwolfLauncher.exe so profiling reaches only its managed Overwolf.exe child; --mode bootstrap|observe|flags|neutral|premium (default neutral).");
                Console.WriteLine("instrument options: --mode MODE --profiler PROFILER_X64_DLL --log LOG_FILE --wait-ms N");
                Console.WriteLine("Premium mode defaults to --plans 1-99 (all common plan IDs). Narrow with --plans 61 or --plans 1,2,3.");
                Console.WriteLine("--close-overwolf closes a running Overwolf before continuing; --force-close-overwolf also kills it if it does not exit.");
                Console.WriteLine("Local legacy subscription API testing only; login is required. No server subscription is granted.");
                return 0;
            }
            if (!new[] { "status", "stage", "apply", "restore", "baseline", "instrument" }.Contains(command)) throw new ArgumentException("Unknown command. Use --help.");
            var options = new Dictionary<string, string>();
            for (int i = 1; i < args.Length; i += 2)
            {
                if (i + 1 == args.Length || !new[] { "--install", "--output", "--backup", "--app", "--plans", "--mode", "--profiler", "--log", "--wait-ms", "--entry", "--close-overwolf", "--force-close-overwolf" }.Contains(args[i]) || options.ContainsKey(args[i]))
                    throw new ArgumentException("Invalid or duplicate option: " + args[i]);
                options.Add(args[i], args[i + 1]);
            }
            // The double-clicked exe has no way to tell the user to close Overwolf
            // first, so it closes it instead. Typed invocations still refuse.
            var closeOverwolf = autoCloseOverwolf || options.ContainsKey("--close-overwolf");
            var forceCloseOverwolf = autoCloseOverwolf || options.ContainsKey("--force-close-overwolf");
            var install = Path.GetFullPath(Get(options, "--install", DiscoverInstall()));
            var version = ActiveVersion(install);
            var target = Path.Combine(install, version, PremiumAssembly.FileName);
            if (command == "baseline") return Baseline(install, version, options, closeOverwolf, forceCloseOverwolf);
            if (command == "instrument") return Instrument(install, version, target, options, closeOverwolf, forceCloseOverwolf);
            var app = Get(options, "--app", Outplayed);
            if (app.Length != 40 || app.Any(c => c < 'a' || c > 'p')) throw new ArgumentException("Expected a 40-character Overwolf extension ID.");
            var planText = Get(options, "--plans", app == Outplayed ? "61" : null);
            if (planText == null) throw new ArgumentException("Specify --plans for this app.");
            var plans = planText.Split(',').Select(int.Parse).Distinct().ToArray();
            if (plans.Length == 0 || plans.Length > 32 || plans.Any(p => p <= 0)) throw new ArgumentException("Specify 1 to 32 positive plan IDs.");
            var profile = ProfileByVersion(version)
                ?? throw new NotSupportedException("Overwolf " + version + " is not a known reviewed version. The installed Core.dll has not been profiled and may behave differently than expected.");
            Console.WriteLine("Overwolf " + version + " | " + target);
            Console.WriteLine("App: " + app + " | local plans: " + string.Join(",", plans));
            var originalHash = Hash(target);
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Path.GetDirectoryName(target));
                using (var assembly = AssemblyDefinition.ReadAssembly(target, new ReaderParameters { AssemblyResolver = resolver, InMemory = true }))
                {
                    if (assembly.MainModule.Resources.Any(r => r.Name == PremiumAssembly.Marker))
                    {
                        Console.WriteLine("This DLL already contains a local premium test. Restore it before applying again.");
                        return command == "status" ? 0 : 2;
                    }
                    PremiumAssembly.Rewrite(assembly, app, plans);
                    Console.WriteLine("Legacy API structure is compatible. Other apps retain the original methods.");
                    if (command == "status") return 0;
                    if (command == "apply") RequireStopped(closeOverwolf, forceCloseOverwolf);
                    var output = Path.GetFullPath(Get(options, "--output", Path.Combine(Environment.CurrentDirectory,
                        "artifacts", "premium-tests", version, Guid.NewGuid().ToString("N"))));
                    if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
                        throw new IOException("Output directory must be empty to preserve previous tests/backups.");
                    Directory.CreateDirectory(output);
                    var staged = Path.Combine(output, PremiumAssembly.FileName);
                    assembly.Write(staged);
                    using (var check = AssemblyDefinition.ReadAssembly(staged))
                        if (!check.MainModule.Resources.Any(r => r.Name == PremiumAssembly.Marker)) throw new IOException("Staged assembly validation failed.");
                    File.Copy(target, Path.Combine(output, "original.dll"), false);
                    if (Hash(target) != originalHash || Hash(Path.Combine(output, "original.dll")) != originalHash)
                        throw new IOException("Overwolf files changed while staging. Nothing was applied; retry after the update finishes.");
                    new XDocument(new XElement("PremiumTest", new XAttribute("version", version), new XAttribute("app", app),
                        new XAttribute("plans", string.Join(",", plans)), new XAttribute("original", originalHash),
                        new XAttribute("patched", Hash(staged)), new XAttribute("createdUtc", DateTime.UtcNow.ToString("o"))))
                        .Save(Path.Combine(output, "manifest.xml"));
                    Console.WriteLine("Staged DLL and immutable original: " + output);
                    if (command == "stage")
                    {
                        Console.WriteLine("Installation unchanged. Staging does not prove that the app accepts a local plan.");
                        return 0;
                    }
                    RequireStopped(closeOverwolf, forceCloseOverwolf);
                    ReplaceChecked(target, staged, originalHash);
                    Console.WriteLine("Applied local API test. Sign in to Overwolf, then open the app to verify it.");
                    Console.WriteLine("Restore: OverwolfPatcher restore --backup \"" + output + "\"");
                    return 0;
                }
            }
        }

        internal static void Restore(string target, string backup)
        {
            var manifest = XDocument.Load(Path.Combine(backup, "manifest.xml")).Root;
            if (manifest == null || manifest.Name != "PremiumTest") throw new IOException("Invalid backup manifest.");
            var backupVersion = (string)manifest.Attribute("version");
            if (new DirectoryInfo(Path.GetDirectoryName(target)).Name != backupVersion)
                throw new IOException("Backup belongs to a different Overwolf version.");
            var original = Path.Combine(backup, "original.dll");
            var expected = (string)manifest.Attribute("original");
            if (Hash(original) != expected) throw new IOException("Original backup hash does not match its manifest.");
            if (!File.Exists(target))
            {
                File.Copy(original, target, false);
                Console.WriteLine("Restored missing DLL from the original backup.");
                return;
            }
            var current = Hash(target);
            if (current == expected) { Console.WriteLine("Already restored."); return; }
            if (current != (string)manifest.Attribute("patched"))
                throw new IOException("DLL changed since this test (possibly an update). Refusing to overwrite it with an old backup.");
            ReplaceChecked(target, original, current);
            Console.WriteLine("Restored original DLL. Backup retained.");
        }

        internal static void ReplaceChecked(string target, string source, string expectedHash)
        {
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, false);
                if (Hash(target) != expectedHash) throw new IOException("Target changed before replacement; operation cancelled.");
                File.Replace(temporary, target, null);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        internal static string Hash(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }

        static string ActiveVersion(string install)
        {
            var configPath = Path.Combine(install, "Overwolf.exe.config");
            if (File.Exists(configPath))
            {
                var config = XDocument.Load(configPath);
                var paths = config.Descendants().Where(e => e.Name.LocalName == "probing")
                    .SelectMany(e => ((string)e.Attribute("privatePath") ?? "").Split(';'))
                    .Where(p => Version.TryParse(p, out _) && p.Split('.').Length == 4).Distinct().ToArray();
                if (paths.Length == 1) return paths[0];
            }
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = machine.OpenSubKey(@"SOFTWARE\WOW6432Node\Overwolf"))
            {
                var current = key?.GetValue("CurrentVersion") as string;
                if (!string.IsNullOrWhiteSpace(current) && Version.TryParse(current, out _) && Directory.Exists(Path.Combine(install, current)))
                    return current;
            }
            var versions = Directory.GetDirectories(install).Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name) && name.Split('.').Length == 4 && Version.TryParse(name, out _))
                .OrderByDescending(name => name).ToArray();
            if (versions.Length == 1) return versions[0];
            throw new IOException("Cannot uniquely identify the active Overwolf version. Install directory contains " + versions.Length + " version folders.");
        }

        static string DiscoverInstall()
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var key = machine.OpenSubKey(@"SOFTWARE\Overwolf"))
                return key?.GetValue("InstallFolder") as string ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Overwolf");
        }

        static void RequireStopped(bool closeOverwolf, bool forceCloseOverwolf)
        {
            var running = OverwolfProcesses();
            if (running.Count == 0) return;
            var names = Describe(running);
            if (!closeOverwolf)
            {
                foreach (var process in running) process.Dispose();
                throw new InvalidOperationException("Close Overwolf and its apps before applying/restoring. Running: " + names);
            }

            // Ask politely first: anything with a window gets WM_CLOSE so the
            // launcher and games can shut down on their own terms.
            Console.WriteLine("Closing running Overwolf processes first: " + names);
            foreach (var process in running)
            {
                try
                {
                    if (process.MainWindowHandle == IntPtr.Zero || !process.CloseMainWindow()) process.Kill();
                }
                catch { /* Helpers and already-exited processes are handled by the waits below. */ }
            }
            WaitForExit(running, 10000);

            var survivors = running.Where(process => !HasExited(process)).ToList();
            if (survivors.Count > 0 && forceCloseOverwolf)
            {
                Console.WriteLine("Forcing " + survivors.Count + " Overwolf process(es) closed: " + Describe(survivors));
                foreach (var process in survivors)
                {
                    try { process.Kill(); }
                    catch { } // Elevated processes we cannot touch surface as survivors below.
                }
                WaitForExit(survivors, 10000);
                survivors = survivors.Where(process => !HasExited(process)).ToList();
            }
            var leftover = Describe(survivors);
            foreach (var process in running) process.Dispose();
            if (survivors.Count > 0)
                throw new InvalidOperationException("Overwolf is still running and could not be closed: " + leftover +
                    ". Close it manually (or run as administrator) and retry.");
        }

        static List<Process> OverwolfProcesses()
        {
            var own = Process.GetCurrentProcess().Id;
            var found = new List<Process>();
            foreach (var process in Process.GetProcesses())
            {
                var keep = false;
                try { keep = process.Id != own && process.ProcessName.StartsWith("Overwolf", StringComparison.OrdinalIgnoreCase); }
                catch { keep = false; } // Exited between enumeration and inspection.
                if (keep) found.Add(process);
                else process.Dispose();
            }
            return found;
        }

        static string Describe(IEnumerable<Process> processes)
        {
            return string.Join(", ", processes.Select(p =>
            {
                try { return p.ProcessName + "(" + p.Id + ")"; }
                catch { return "unknown"; }
            }).Distinct().OrderBy(value => value).ToArray());
        }

        static bool HasExited(Process process)
        {
            try { return process.HasExited; }
            catch { return true; }
        }

        static void WaitForExit(IEnumerable<Process> processes, int milliseconds)
        {
            var pending = processes.ToList();
            var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
            while (DateTime.UtcNow < deadline)
            {
                pending.RemoveAll(HasExited);
                if (pending.Count == 0) return;
                Thread.Sleep(100);
            }
        }

        static int Instrument(string install, string version, string target, Dictionary<string, string> options, bool closeOverwolf, bool forceCloseOverwolf)
        {
            var profile = ProfileByVersion(version)
                ?? throw new NotSupportedException("The startup profiler is pinned to known reviewed versions; refusing " + version + ".");
            RequireStopped(closeOverwolf, forceCloseOverwolf);
            if (!File.Exists(target) || !string.Equals(Hash(target), profile.Hash, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The installed Core hash does not match the reviewed clean baseline for " + version + "; refusing to profile it.");

            var executable = Path.Combine(install, "Overwolf.exe");
            var launcher = Path.Combine(install, "OverwolfLauncher.exe");
            if (!IsManagedAssembly(executable)) executable = Path.Combine(install, version, "Overwolf.exe");
            if (!File.Exists(launcher)) launcher = Path.Combine(install, version, "OverwolfLauncher.exe");
            if (!File.Exists(executable)) throw new FileNotFoundException("The managed Overwolf executable was not found.", executable);
            if (!File.Exists(launcher)) throw new FileNotFoundException("The native Overwolf launcher was not found.", launcher);
            if (!IsPe64(executable) || !IsPe64(launcher)) throw new NotSupportedException("Overwolf entry executables are not x64 PE files.");
            try { AssemblyName.GetAssemblyName(executable); }
            catch (Exception error) { throw new NotSupportedException("Overwolf.exe is not the managed entry executable.", error); }

            var profiler = Path.GetFullPath(Get(options, "--profiler",
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OverwolfPatcher.Profiler.x64.dll")));
            if (!File.Exists(profiler)) throw new FileNotFoundException("The x64 CLR profiler was not found. Build the native profiler first.", profiler);
            var mode = Get(options, "--mode", "neutral").ToLowerInvariant();
            if (mode != "bootstrap" && mode != "observe" && mode != "flags" && mode != "neutral" && mode != "premium")
                throw new ArgumentException("--mode must be bootstrap, observe, flags, neutral, or premium.");

            var app = Get(options, "--app", Outplayed);
            if (app.Length != 40 || app.Any(c => c < 'a' || c > 'p')) throw new ArgumentException("Expected a 40-character Overwolf extension ID.");
            var planText = Get(options, "--plans", null);
            if (mode == "premium" && string.IsNullOrEmpty(planText)) planText = "1-99";
            var plans = mode == "premium"
                ? ParsePlans(planText)
                : new int[0];
            if (plans.Length == 0 || plans.Length > 128 || plans.Any(p => p <= 0)) throw new ArgumentException("Specify 1 to 128 positive plan IDs, or use --plans 1-99 for all common plans.");
            var waitText = Get(options, "--wait-ms", "0");
            if (!int.TryParse(waitText, out var waitMs) || waitMs < 0 || waitMs > 60000)
                throw new ArgumentException("--wait-ms must be between 0 and 60000.");

            var log = Path.GetFullPath(Get(options, "--log", Path.Combine(Environment.CurrentDirectory,
                "artifacts", "profiler", version + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log")));
            var logDirectory = Path.GetDirectoryName(log);
            if (!string.IsNullOrWhiteSpace(logDirectory)) Directory.CreateDirectory(logDirectory);

            // Some hosted shells expose both Path and PATH. .NET Framework's
            // ProcessStartInfo turns the inherited block into a case-insensitive
            // dictionary and throws when those aliases coexist.
            var inheritedPath = Environment.GetEnvironmentVariable("Path") ?? Environment.GetEnvironmentVariable("PATH");
            if (inheritedPath != null)
            {
                Environment.SetEnvironmentVariable("PATH", null);
                Environment.SetEnvironmentVariable("Path", inheritedPath);
            }
            var start = new ProcessStartInfo
            {
                FileName = launcher,
                Arguments = "-from-desktop",
                WorkingDirectory = File.Exists(Path.Combine(install, version, "OverwolfLauncher.exe")) ? Path.Combine(install, version) : install,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            start.EnvironmentVariables["COR_ENABLE_PROFILING"] = "1";
            start.EnvironmentVariables["COR_PROFILER"] = ProfilerClsid;
            start.EnvironmentVariables["COR_PROFILER_PATH"] = profiler;
            start.EnvironmentVariables["COR_PROFILER_PATH_64"] = profiler;
            start.EnvironmentVariables["COMPLUS_ProfAPI_ProfilerCompatibilitySetting"] = "EnableV2Profiler";
            start.EnvironmentVariables["OVERWOLF_PATCHER_TARGET_PROCESS"] = executable;
            start.EnvironmentVariables["OVERWOLF_PATCHER_PROFILER_LOG_PER_PROCESS"] = "1";
            start.EnvironmentVariables["OVERWOLF_PATCHER_PROFILER_MODE"] = mode;
            start.EnvironmentVariables["OVERWOLF_PATCHER_PROFILER_LOG"] = log;
            start.EnvironmentVariables["OVERWOLF_PATCHER_EXPECTED_CORE_SHA256"] = profile.Hash;
            if (mode == "premium")
            {
                start.EnvironmentVariables["OVERWOLF_PATCHER_APP"] = app;
                start.EnvironmentVariables["OVERWOLF_PATCHER_PLANS"] = string.Join(",", plans);
            }
            var launchedPid = 0;
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Overwolf could not be started.");
                launchedPid = process.Id;
                Console.WriteLine("Launched native OverwolfLauncher PID " + process.Id + "; profiler targets managed Overwolf.exe only.");
                if (waitMs > 0) CaptureWindows(process.Id, log, waitMs);
            }
            Console.WriteLine("Mode: " + mode + " | profiler log: " + log);
            if (waitMs > 0) Console.WriteLine("Window diagnostic capture: " + WindowLogPath(log, launchedPid));
            Console.WriteLine("Installed files were not modified. Remove the profiling environment by launching Overwolf normally.");
            return 0;
        }

        static int Baseline(string install, string version, Dictionary<string, string> options, bool closeOverwolf, bool forceCloseOverwolf)
        {
            RequireStopped(closeOverwolf, forceCloseOverwolf);
            var managedExecutable = Path.Combine(install, "Overwolf.exe");
            var nativeLauncher = Path.Combine(install, "OverwolfLauncher.exe");
            if (!File.Exists(nativeLauncher)) nativeLauncher = Path.Combine(install, version, "OverwolfLauncher.exe");
            if (!IsManagedAssembly(managedExecutable)) managedExecutable = Path.Combine(install, version, "Overwolf.exe");
            if (!File.Exists(managedExecutable)) throw new FileNotFoundException("The managed Overwolf executable was not found.", managedExecutable);
            var entry = Get(options, "--entry", "launcher").ToLowerInvariant();
            if (entry != "launcher" && entry != "managed") throw new ArgumentException("--entry must be launcher or managed.");
            var executable = entry == "launcher" ? nativeLauncher : managedExecutable;
            if (!File.Exists(executable)) throw new FileNotFoundException("The selected Overwolf executable was not found.", executable);
            if (!IsPe64(executable)) throw new NotSupportedException("The selected Overwolf executable is not an x64 PE.");
            try { AssemblyName.GetAssemblyName(managedExecutable); }
            catch (Exception error) { throw new NotSupportedException("Overwolf.exe is not the managed entry executable.", error); }

            var waitText = Get(options, "--wait-ms", "0");
            if (!int.TryParse(waitText, out var waitMs) || waitMs < 0 || waitMs > 60000)
                throw new ArgumentException("--wait-ms must be between 0 and 60000.");
            var log = Path.GetFullPath(Get(options, "--log", Path.Combine(Environment.CurrentDirectory,
                "artifacts", "profiler", version + "-baseline-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".log")));
            var logDirectory = Path.GetDirectoryName(log);
            if (!string.IsNullOrWhiteSpace(logDirectory)) Directory.CreateDirectory(logDirectory);
            var inheritedPath = Environment.GetEnvironmentVariable("Path") ?? Environment.GetEnvironmentVariable("PATH");
            if (inheritedPath != null)
            {
                Environment.SetEnvironmentVariable("PATH", null);
                Environment.SetEnvironmentVariable("Path", inheritedPath);
            }
            var start = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = entry == "launcher" ? "-from-desktop" : "",
                WorkingDirectory = install,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            foreach (var name in new[] { "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH",
                "COR_PROFILER_PATH_32", "COR_PROFILER_PATH_64", "COMPLUS_ProfAPI_ProfilerCompatibilitySetting" })
                start.EnvironmentVariables.Remove(name);

            var launchedPid = 0;
            using (var process = Process.Start(start))
            {
                if (process == null) throw new InvalidOperationException("Overwolf could not be started.");
                launchedPid = process.Id;
                Console.WriteLine("Launched baseline " + entry + " Overwolf PID " + process.Id + " without CLR profiling.");
                if (waitMs > 0) CaptureWindows(process.Id, log, waitMs);
            }
            if (waitMs > 0) Console.WriteLine("Baseline window diagnostic capture: " + WindowLogPath(log, launchedPid));
            Console.WriteLine("No installation or profiling environment was modified.");
            return 0;
        }

        static void CaptureWindows(int processId, string log, int waitMs)
        {
            try { Thread.Sleep(waitMs); }
            catch (ThreadInterruptedException) { return; }

            var pids = new HashSet<int> { processId };
            foreach (var candidate in Process.GetProcesses())
            {
                using (candidate)
                    if (candidate.ProcessName.StartsWith("Overwolf", StringComparison.OrdinalIgnoreCase)) pids.Add(candidate.Id);
            }
            var lines = new List<string> { "rootPid=" + processId + " observedPids=" + string.Join(",", pids.OrderBy(value => value)) };
            EnumWindows((window, _) =>
            {
                GetWindowThreadProcessId(window, out var owner);
                if (!pids.Contains((int)owner)) return true;
                AddWindow(lines, "top", window);
                EnumChildWindows(window, (child, __) => { AddWindow(lines, "child", child); return true; }, IntPtr.Zero);
                return true;
            }, IntPtr.Zero);
            var path = WindowLogPath(log, processId);
            File.WriteAllLines(path, lines.Count == 0
                ? new[] { "No windows found for PID " + processId + " after " + waitMs + " ms." }
                : lines, Encoding.UTF8);
            Console.WriteLine("Captured " + lines.Count + " window records for PID " + processId + ": " + path);
        }

        static void AddWindow(List<string> lines, string kind, IntPtr handle)
        {
            var title = WindowText(handle);
            var className = new StringBuilder(256);
            GetClassName(handle, className, className.Capacity);
            lines.Add(kind + " hwnd=" + handle + " visible=" + IsWindowVisible(handle) +
                " class=" + className + " title=" + title);
        }

        static string WindowText(IntPtr handle)
        {
            var length = GetWindowTextLength(handle);
            var value = new StringBuilder(Math.Max(1, length + 1));
            GetWindowText(handle, value, value.Capacity);
            return value.ToString().Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string WindowLogPath(string log, int processId)
        {
            var directory = Path.GetDirectoryName(log) ?? Environment.CurrentDirectory;
            var name = Path.GetFileNameWithoutExtension(log) + "." + processId + ".windows.txt";
            return Path.Combine(directory, name);
        }

        [DllImport("user32.dll")]
        static extern bool EnumWindows(EnumWindowsProc callback, IntPtr state);
        [DllImport("user32.dll")]
        static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr state);
        [DllImport("user32.dll")]
        static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetWindowTextLength(IntPtr window);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr window, StringBuilder className, int capacity);
        [DllImport("user32.dll")]
        static extern bool IsWindowVisible(IntPtr window);

        delegate bool EnumWindowsProc(IntPtr window, IntPtr state);

        static int[] ParsePlans(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<int>();
            var parts = text.Split(',').Select(p => p.Trim()).Where(p => !string.IsNullOrEmpty(p)).ToArray();
            var set = new System.Collections.Generic.SortedSet<int>();
            foreach (var part in parts)
            {
                if (part.Contains("-"))
                {
                    var range = part.Split('-');
                    if (range.Length != 2 || !int.TryParse(range[0], out var start) || !int.TryParse(range[1], out var end) || start > end || start <= 0)
                        throw new ArgumentException("Invalid plan range: " + part);
                    for (int v = start; v <= end && set.Count < 128; v++) set.Add(v);
                }
                else if (int.TryParse(part, out var single) && single > 0)
                {
                    set.Add(single);
                }
                else
                {
                    throw new ArgumentException("Invalid plan ID: " + part);
                }
            }
            return set.Take(128).ToArray();
        }

        static bool IsPe64(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length < 0x40) return false;
                stream.Position = 0x3C;
                var peOffset = reader.ReadInt32();
                if (peOffset < 0 || peOffset > stream.Length - 6) return false;
                stream.Position = peOffset;
                if (reader.ReadUInt32() != 0x00004550) return false;
                return reader.ReadUInt16() == 0x8664;
            }
        }

        static bool IsManagedAssembly(string path)
        {
            try { return AssemblyName.GetAssemblyName(path) != null; }
            catch { return false; }
        }

        static string Get(Dictionary<string, string> options, string name, string fallback) => options.TryGetValue(name, out var value) ? value : fallback;
    }
}

