using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MetadataFilesMerger
{
    internal static class ConsoleUi
    {
        public static void DrawHeader()
        {
            try
            {
                if (!Console.IsOutputRedirected) Console.Clear();
            }
            catch (IOException) { }
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ╔══════════════════════════════════════════════════════════════════╗");
            Console.WriteLine("  ║                    METADATA FILES MERGER v2.00                   ║");
            Console.WriteLine("  ║                                                                  ║");
            Console.WriteLine("  ╚══════════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
        }

        public static void ShowConfiguration(AppSettings s)
        {
            Console.WriteLine("\n  CONFIGURATION");
            ShowPath("Primary", s.PrimaryFolder);
            ShowPath("Secondary", s.SecondaryFolder);
            ShowPath("Merged output", s.OutputFolder);
            ShowPath("Logs", s.EffectiveLogFolder);
            Console.WriteLine("  Templates     : " + String.Join(", ", s.FolderStartPatterns));
            Console.WriteLine("  Template merge: " + (s.MergeOnlyTemplateMatchingPaths ? "Only matching secondary paths" : "All secondary paths"));
            Console.WriteLine("  Name training : " + (s.FolderNameTrainingEnabled ? "Enabled" : "Disabled") +
                " (" + s.TrainingSource + ")");
            Console.WriteLine("  Workers       : " + s.WorkerCount);
        }

        private static void ShowPath(string label, string value)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write("  " + label.PadRight(14) + ": ");
            Console.ResetColor();
            Console.WriteLine(String.IsNullOrWhiteSpace(value) ? "(not configured)" : value);
        }

        public static AppSettings PromptForSettings(AppSettings current)
        {
            DrawHeader();
            Console.WriteLine("\n  Enter each folder path. Press Enter to keep the current value.\n");
            current.PrimaryFolder = Prompt("Primary folder", current.PrimaryFolder);
            current.SecondaryFolder = Prompt("Secondary folder", current.SecondaryFolder);
            current.OutputFolder = Prompt("Merged output folder", current.OutputFolder);
            current.LogFolder = Prompt("Log folder", current.EffectiveLogFolder);
            return current;
        }

        private static string Prompt(string label, string current)
        {
            Console.Write("  " + label + (String.IsNullOrWhiteSpace(current) ? "" : " [" + current + "]") + ": ");
            string value = (Console.ReadLine() ?? "").Trim().Trim('"');
            return value.Length == 0 ? current : Path.GetFullPath(value);
        }

        public static void ShowProgress(MergeStatistics s, CancellationToken token)
        {
            while (!token.WaitHandle.WaitOne(250))
            {
                double rate = s.Elapsed.TotalSeconds <= 0 ? 0 : s.Processed / s.Elapsed.TotalSeconds;
                Console.Write("\r  Found {0:N0} │ Done {1:N0} │ Updated {2:N0} │ Errors {3:N0} │ Lookup {4:N0} │ Training {5:N0} │ Rules {6:N0} │ Lookup% {7:N1} │ {8:N0}/s   ",
                    s.Discovered, s.Processed, s.Merged, s.Errors,
                    s.LookupTableRecognitions, s.NameTrainingRecognitions, s.StandardRulesRecognitions,
                    s.LookupHitRatio, rate);
            }
        }

        public static void ShowFinalStatistics(MergeStatistics s, string log)
        {
            Console.WriteLine("\n\n  RUN COMPLETE");
            Console.WriteLine("  Processed : {0:N0}   Updated: {1:N0}   Unchanged: {2:N0}", s.Processed, s.Merged, s.Unchanged);
            Console.WriteLine("  Resumed   : {0:N0}   Errors : {1:N0}   Time     : {2}", s.Skipped, s.Errors, s.Elapsed.ToString(@"hh\:mm\:ss"));
            Console.WriteLine("  Lookup    : {0:N0}   Training: {1:N0}   Rules    : {2:N0}   Lookup hit: {3:N1}%",
                s.LookupTableRecognitions, s.NameTrainingRecognitions, s.StandardRulesRecognitions, s.LookupHitRatio);
            Console.WriteLine("  Log       : " + log);
        }

        public static void ManageFolderStartPatterns(AppSettings settings)
        {
            while (true)
            {
                DrawHeader();
                Console.WriteLine("\n  FOLDER-START TEMPLATES");
                Console.WriteLine("  Exact phrases and * wildcard expressions are supported.\n");
                if (settings.FolderStartPatterns.Count == 0)
                    Console.WriteLine("  (No templates configured)");
                else
                    for (int i = 0; i < settings.FolderStartPatterns.Count; i++)
                        Console.WriteLine("  [{0}] {1}", i + 1, settings.FolderStartPatterns[i]);

                Console.WriteLine("\n  [A] Add template");
                Console.WriteLine("  [R] Remove template");
                Console.WriteLine("  [B] Back to main menu");
                Console.Write("\n  Select an option: ");
                string choice = (Console.ReadLine() ?? "").Trim();
                if (choice.Equals("B", StringComparison.OrdinalIgnoreCase)) return;

                if (choice.Equals("A", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Write("  New template: ");
                    string pattern = (Console.ReadLine() ?? "").Trim();
                    if (pattern.Length > 0 && !settings.FolderStartPatterns.Exists(
                        x => x.Equals(pattern, StringComparison.OrdinalIgnoreCase)))
                    {
                        settings.FolderStartPatterns.Add(pattern);
                        SavePatterns(settings);
                    }
                }
                else if (choice.Equals("R", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Write("  Number to remove: ");
                    int number;
                    if (Int32.TryParse(Console.ReadLine(), out number) &&
                        number >= 1 && number <= settings.FolderStartPatterns.Count)
                    {
                        settings.FolderStartPatterns.RemoveAt(number - 1);
                        SavePatterns(settings);
                    }
                }
            }
        }

        private static void SavePatterns(AppSettings settings)
        {
            try { settings.SavePatterns(); }
            catch (Exception ex)
            {
                WriteError("\n  Could not save templates: " + ex.Message);
                Pause();
            }
        }

        public static void ToggleTemplateMergeFilter(AppSettings settings)
        {
            settings.MergeOnlyTemplateMatchingPaths = !settings.MergeOnlyTemplateMatchingPaths;
            try { settings.SaveMergeOptions(); }
            catch (Exception ex)
            {
                WriteError("\n  Could not save merge option: " + ex.Message);
                Pause();
            }
        }

        public static void ManageFolderNameTraining(AppSettings settings)
        {
            while (true)
            {
                DrawHeader();
                FolderNameModel model = FolderNameModel.Load(settings.FolderNameModelPath);
                Console.WriteLine("\n  INTELLIGENT FOLDER RECOGNITION");
                Console.WriteLine("  Source        : " + settings.TrainingSource);
                Console.WriteLine("  Decomposition : " + (settings.FolderNameTrainingEnabled ? "Enabled" : "Disabled"));
                Console.WriteLine("  Model names   : {0:N0} ({1:N0} containing /)", model.Count, model.NamesContainingSlash);
                Console.WriteLine("  Model file    : " + settings.FolderNameModelPath);

                Console.WriteLine("\n  [1] Configure SQL Server source");
                Console.WriteLine("  [2] Configure spreadsheet source");
                Console.WriteLine("  [3] Manage ad-hoc folder names");
                Console.WriteLine("  [4] Select active source");
                WriteAccent("  [5] Run training");
                Console.WriteLine("  [6] Toggle trained decomposition");
                Console.WriteLine("  [B] Back to main menu");
                Console.Write("\n  Select an option: ");

                string choice = (Console.ReadLine() ?? "").Trim();
                if (choice.Equals("B", StringComparison.OrdinalIgnoreCase))
                    return;
                if (choice == "1")
                    ConfigureSqlTraining(settings);
                else if (choice == "2")
                    ConfigureSpreadsheetTraining(settings);
                else if (choice == "3")
                    ManageAdHocFolderNames(settings);
                else if (choice == "4")
                    SelectTrainingSource(settings);
                else if (choice == "5")
                    RunTraining(settings);
                else if (choice == "6")
                    ToggleFolderNameTraining(settings);
            }
        }

        public static void InspectFolderString(AppSettings settings)
        {
            DrawHeader();
            Console.WriteLine("\n  INSPECT FOLDER STRING\n");
            Console.WriteLine("  Paste a full folder path or folder name to see how it will be split.\n");

            string value = PromptText("String", "");
            if (String.IsNullOrWhiteSpace(value))
                return;

            FolderPathResolver resolver = new FolderPathResolver(settings, null);
            string[] paths = new SecondaryPathParser(settings).Parse(value).ToArray();
            if (paths.Length == 0)
                paths = new[] { value.Trim() };

            Console.WriteLine();
            Console.WriteLine("  Paths       : {0:N0}", paths.Length);
            for (int pathIndex = 0; pathIndex < paths.Length; pathIndex++)
            {
                ResolvedPathParts resolved = resolver.Resolve(paths[pathIndex]);
                string[] parts = resolved.Parts
                    .Where(part => !String.IsNullOrWhiteSpace(part))
                    .Select(FolderNameSanitizer.Sanitize)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                Console.WriteLine();
                Console.WriteLine("  PATH {0}", pathIndex + 1);
                Console.WriteLine("  Full path   : " + paths[pathIndex]);
                Console.WriteLine("  Recognition: " + resolved.Source);
                Console.WriteLine("  Parts       : {0:N0}", parts.Length);
                for (int index = 0; index < parts.Length; index++)
                    Console.WriteLine("  [{0}] {1}", index + 1, parts[index]);
            }

            Pause();
        }

        public static void ExportDatabaseFolderPaths(AppSettings settings)
        {
            DrawHeader();
            Console.WriteLine("\n  EXPORT DATABASE FOLDER PATHS\n");
            Console.WriteLine("  Uses the SQL Server settings from Intelligent Folder Recognition.\n");

            string defaultPath = Path.Combine(
                String.IsNullOrWhiteSpace(settings.OutputFolder)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                    : settings.OutputFolder,
                "folder-paths-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".xlsx");
            string outputPath = PromptText("Excel output path", defaultPath).Trim('"');
            if (String.IsNullOrWhiteSpace(outputPath))
                return;

            FolderHierarchyExportService service = new FolderHierarchyExportService();
            string validation = service.Validate(settings, outputPath);
            if (validation != null)
            {
                WriteError("\n  " + validation);
                Pause();
                return;
            }

            TrainingProgress progress = new TrainingProgress();
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                };
                Console.CancelKeyPress += handler;
                try
                {
                    Task<FolderHierarchyExportResult> task = Task.Run(
                        delegate { return service.Export(settings, outputPath, progress, cancellation.Token); });
                    int frame = 0;
                    while (!task.IsCompleted)
                    {
                        Thread.Sleep(150);
                        ShowTrainingProgress(progress, frame++);
                    }
                    FolderHierarchyExportResult result = task.GetAwaiter().GetResult();
                    ShowTrainingProgress(progress, frame);
                    Console.WriteLine("\n\n  EXPORT COMPLETE");
                    Console.WriteLine("  Source rows : {0:N0}", result.SourceRows);
                    Console.WriteLine("  Paths       : {0:N0}", result.ExportedPaths);
                    Console.WriteLine("  Max levels  : {0:N0}", result.MaximumLevels);
                    Console.WriteLine("  Excel file  : " + result.OutputPath);
                }
                catch (OperationCanceledException)
                {
                    WriteError("\n\n  Export was cancelled.");
                }
                catch (Exception ex)
                {
                    WriteError("\n\n  Export failed: " + ex.Message);
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                }
            }

            Pause();
        }

        private static void ConfigureSqlTraining(AppSettings settings)
        {
            DrawHeader();
            Console.WriteLine("\n  SQL SERVER TRAINING SOURCE");
            Console.WriteLine("  For a named instance, use SERVER\\INSTANCE and port 0 for SQL Browser discovery.\n");
            settings.SqlServer = PromptText("Server address", settings.SqlServer);
            settings.SqlPort = PromptInteger("Port (0 = automatic)", settings.SqlPort, 0, 65535);
            settings.SqlDatabase = PromptText("Database", settings.SqlDatabase);
            settings.SqlUsername = PromptText("Username", settings.SqlUsername);
            string password = PromptPassword("Password", settings.SqlPassword.Length > 0);
            if (password != null)
                settings.SqlPassword = password;
            settings.TrainingSql = PromptText("SQL query", settings.TrainingSql);
            settings.TrainingSource = "Database";
            SaveTrainingSettings(settings);
        }

        private static void ConfigureSpreadsheetTraining(AppSettings settings)
        {
            DrawHeader();
            Console.WriteLine("\n  SPREADSHEET TRAINING SOURCE");
            Console.WriteLine("  Supported formats: .xlsx, .csv, .tsv, and .txt. Names are read from column A.\n");
            string value = PromptText("Spreadsheet path", settings.TrainingSpreadsheetPath);
            if (!String.IsNullOrWhiteSpace(value))
                settings.TrainingSpreadsheetPath = Path.GetFullPath(value.Trim('"'));
            settings.TrainingSource = "Spreadsheet";
            SaveTrainingSettings(settings);
        }

        private static void ManageAdHocFolderNames(AppSettings settings)
        {
            while (true)
            {
                DrawHeader();
                Console.WriteLine("\n  AD-HOC FOLDER NAMES\n");
                if (settings.AdHocFolderNames.Count == 0)
                    Console.WriteLine("  (No names configured)");
                else
                    for (int index = 0; index < settings.AdHocFolderNames.Count; index++)
                        Console.WriteLine("  [{0}] {1}", index + 1, settings.AdHocFolderNames[index]);

                Console.WriteLine("\n  [A] Add folder name");
                Console.WriteLine("  [R] Remove folder name");
                Console.WriteLine("  [U] Use ad-hoc names as active source");
                Console.WriteLine("  [B] Back");
                Console.Write("\n  Select an option: ");
                string choice = (Console.ReadLine() ?? "").Trim();
                if (choice.Equals("B", StringComparison.OrdinalIgnoreCase))
                    return;
                if (choice.Equals("A", StringComparison.OrdinalIgnoreCase))
                {
                    string name = PromptText("Folder name", "");
                    if (name.Length > 0 && !settings.AdHocFolderNames.Exists(
                        value => value.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    {
                        settings.AdHocFolderNames.Add(name);
                        SaveTrainingSettings(settings);
                    }
                }
                else if (choice.Equals("R", StringComparison.OrdinalIgnoreCase))
                {
                    int number = PromptInteger("Number to remove", 0, 0, settings.AdHocFolderNames.Count);
                    if (number > 0)
                    {
                        settings.AdHocFolderNames.RemoveAt(number - 1);
                        SaveTrainingSettings(settings);
                    }
                }
                else if (choice.Equals("U", StringComparison.OrdinalIgnoreCase))
                {
                    settings.TrainingSource = "AdHoc";
                    SaveTrainingSettings(settings);
                }
            }
        }

        private static void SelectTrainingSource(AppSettings settings)
        {
            Console.WriteLine("\n  [1] Database");
            Console.WriteLine("  [2] Spreadsheet");
            Console.WriteLine("  [3] Ad-hoc names");
            Console.Write("\n  Select source: ");
            string choice = (Console.ReadLine() ?? "").Trim();
            if (choice == "1")
                settings.TrainingSource = "Database";
            else if (choice == "2")
                settings.TrainingSource = "Spreadsheet";
            else if (choice == "3")
                settings.TrainingSource = "AdHoc";
            else
                return;
            SaveTrainingSettings(settings);
        }

        private static void RunTraining(AppSettings settings)
        {
            FolderNameTrainingService service = new FolderNameTrainingService();
            string validation = service.Validate(settings);
            if (validation != null)
            {
                WriteError("\n  " + validation);
                Pause();
                return;
            }

            TrainingProgress progress = new TrainingProgress();
            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                };
                Console.CancelKeyPress += handler;
                try
                {
                    Task<TrainingResult> task = Task.Run(
                        delegate { return service.Train(settings, progress, cancellation.Token); });
                    int frame = 0;
                    while (!task.IsCompleted)
                    {
                        Thread.Sleep(150);
                        ShowTrainingProgress(progress, frame++);
                    }
                    TrainingResult result = task.GetAwaiter().GetResult();
                    ShowTrainingProgress(progress, frame);
                    Console.WriteLine("\n\n  TRAINING COMPLETE");
                    Console.WriteLine("  Source rows       : {0:N0}", result.SourceRows);
                    Console.WriteLine("  Valid unique names: {0:N0}", result.ValidNames);
                    Console.WriteLine("  Names containing /: {0:N0}", result.NamesContainingSlash);
                    Console.WriteLine("  Model             : " + result.ModelPath);
                }
                catch (OperationCanceledException)
                {
                    WriteError("\n\n  Training was cancelled.");
                }
                catch (Exception ex)
                {
                    WriteError("\n\n  Training failed: " + ex.Message);
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                }
            }
            Pause();
        }

        private static void ShowTrainingProgress(TrainingProgress progress, int frame)
        {
            const int width = 28;
            long? total = progress.Total;
            int filled;
            string count;
            if (total.HasValue)
            {
                filled = total.Value <= 0
                    ? width
                    : (int)Math.Min(width, progress.Processed * width / total.Value);
                count = String.Format("{0:N0}/{1:N0}", progress.Processed, total.Value);
            }
            else
            {
                filled = frame % width;
                count = String.Format("{0:N0}", progress.Processed);
            }
            string bar = total.HasValue
                ? new string('#', filled) + new string('-', width - filled)
                : new string('-', filled) + "#" + new string('-', width - filled - 1);
            Console.Write("\r  [{0}] {1} | {2}   ", bar, count, progress.Stage);
        }

        private static void ToggleFolderNameTraining(AppSettings settings)
        {
            if (!settings.FolderNameTrainingEnabled && !File.Exists(settings.FolderNameModelPath))
            {
                WriteError("\n  Run training before enabling trained decomposition.");
                Pause();
                return;
            }
            settings.FolderNameTrainingEnabled = !settings.FolderNameTrainingEnabled;
            SaveTrainingSettings(settings);
        }

        private static string PromptText(string label, string current)
        {
            Console.Write("  " + label + (String.IsNullOrWhiteSpace(current) ? "" : " [" + current + "]") + ": ");
            string value = (Console.ReadLine() ?? "").Trim();
            return value.Length == 0 ? current : value;
        }

        private static int PromptInteger(string label, int current, int minimum, int maximum)
        {
            Console.Write("  " + label + (current > 0 ? " [" + current + "]" : "") + ": ");
            string raw = (Console.ReadLine() ?? "").Trim();
            if (raw.Length == 0)
                return current;
            int value;
            return Int32.TryParse(raw, out value) && value >= minimum && value <= maximum
                ? value
                : current;
        }

        private static string PromptPassword(string label, bool hasCurrent)
        {
            Console.Write("  " + label + (hasCurrent ? " [configured]" : "") + ": ");
            if (Console.IsInputRedirected)
            {
                string redirected = Console.ReadLine();
                return String.IsNullOrEmpty(redirected) ? null : redirected;
            }

            StringBuilder password = new StringBuilder();
            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter)
                    break;
                if (key.Key == ConsoleKey.Backspace && password.Length > 0)
                {
                    password.Length--;
                    Console.Write("\b \b");
                }
                else if (!Char.IsControl(key.KeyChar))
                {
                    password.Append(key.KeyChar);
                    Console.Write("*");
                }
            }
            Console.WriteLine();
            return password.Length == 0 ? null : password.ToString();
        }

        private static void SaveTrainingSettings(AppSettings settings)
        {
            try
            {
                settings.SaveTrainingSettings();
            }
            catch (Exception ex)
            {
                WriteError("\n  Could not save training settings: " + ex.Message);
                Pause();
            }
        }

        public static void WriteAccent(string text) { Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine(text); Console.ResetColor(); }
        public static void WriteError(string text) { Console.ForegroundColor = ConsoleColor.Red; Console.WriteLine(text); Console.ResetColor(); }
        public static void Pause() { Console.Write("\n  Press Enter to return to the menu..."); Console.ReadLine(); }
    }
}
