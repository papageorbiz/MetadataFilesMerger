using System;
using System.IO;
using System.Threading;

namespace MetadataFilesMerger
{
    internal class Program
    {
        static void Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            Console.Title = "Metadata Files Merger";

            AppSettings settings = AppSettings.Load();
            while (true)
            {
                ConsoleUi.DrawHeader();
                ConsoleUi.ShowConfiguration(settings);
                Console.WriteLine();
                ConsoleUi.WriteAccent("  [1] Start merge");
                Console.WriteLine("  [2] Change folders");
                Console.WriteLine("  [3] Open latest log");
                Console.WriteLine("  [4] Manage folder-start templates");
                Console.WriteLine("  [5] Toggle template-only merge filter");
                Console.WriteLine("  [6] Intelligent Folder Recognition");
                Console.WriteLine("  [7] Exit");
                Console.Write("\n  Select an option: ");

                string choice = Console.ReadLine();
                if (choice == "1")
                    Run(settings);
                else if (choice == "2")
                    settings = ConsoleUi.PromptForSettings(settings);
                else if (choice == "3")
                    RunLogger.OpenLatest(settings.EffectiveLogFolder);
                else if (choice == "4")
                    ConsoleUi.ManageFolderStartPatterns(settings);
                else if (choice == "5")
                    ConsoleUi.ToggleTemplateMergeFilter(settings);
                else if (choice == "6")
                    ConsoleUi.ManageFolderNameTraining(settings);
                else if (choice == "7")
                    return;
            }
        }

        private static void Run(AppSettings settings)
        {
            string validation = settings.Validate();
            if (validation != null)
            {
                ConsoleUi.WriteError("\n  " + validation);
                ConsoleUi.Pause();
                return;
            }

            using (CancellationTokenSource cancellation = new CancellationTokenSource())
            using (RunLogger logger = new RunLogger(settings.EffectiveLogFolder))
            {
                ConsoleCancelEventHandler handler = delegate(object sender, ConsoleCancelEventArgs e)
                {
                    e.Cancel = true;
                    cancellation.Cancel();
                };
                Console.CancelKeyPress += handler;
                try
                {
                    FileMergeService service = new FileMergeService(settings, logger);
                    MergeStatistics result = service.Run(cancellation.Token);
                    ConsoleUi.ShowFinalStatistics(result, logger.LogPath);
                }
                catch (Exception ex)
                {
                    logger.Error(null, "Fatal run error", ex);
                    ConsoleUi.WriteError("\n  The run could not start: " + ex.Message);
                }
                finally
                {
                    Console.CancelKeyPress -= handler;
                }
            }
            ConsoleUi.Pause();
        }
    }
}
