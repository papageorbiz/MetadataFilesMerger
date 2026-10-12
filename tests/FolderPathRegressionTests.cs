using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;
using MetadataFilesMerger;

internal static class FolderPathRegressionTests
{
    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        if (!expected.SequenceEqual(actual))
            throw new Exception("Expected: " + String.Join(" | ", expected) +
                "\nActual: " + String.Join(" | ", actual));
    }

    private static void Decomposes(string path, params string[] expected)
    {
        Equal(expected, FolderNameModel.Empty().Decompose(path));
    }

    public static int Main(string[] args)
    {
        string purchasing = "Cabinet/PURCHASING DEPT/PURCHASING 2024/TANKERS/SPARES/AEGEA/AGE-V1-240010";
        Decomposes(purchasing, purchasing.Split('/'));
        Decomposes("Cabinet/Reports/24/01/2024/Invoices", "Cabinet", "Reports", "24/01/2024", "Invoices");
        Decomposes("Cabinet/Report 2024/02/29 final/Child", "Cabinet", "Report 2024/02/29 final", "Child");
        Decomposes("Cabinet/31/02/2024/Child", "Cabinet", "31", "02", "2024", "Child");
        Decomposes("Cabinet/AGE-V1-240010/Child", "Cabinet", "AGE-V1-240010", "Child");

        string root = Path.Combine(Path.GetTempPath(), "MetadataMergerRegression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new AppSettings {
                PrimaryFolder = Path.Combine(root, "primary"),
                SecondaryFolder = Path.Combine(root, "secondary"),
                OutputFolder = Path.Combine(root, "output"),
                FolderStartPatterns = new List<string> { "Smart folder" }
            };
            Directory.CreateDirectory(settings.PrimaryFolder);
            Directory.CreateDirectory(settings.SecondaryFolder);
            using (var logger = new RunLogger(Path.Combine(root, "logs")))
            {
                var service = new FileMergeService(settings, logger);
                var serializer = new JavaScriptSerializer();
                // Explicit numeric folder levels must remain separate even if they resemble a date.
                string explicitPath = "Cabinet/24/01/2024";
                var original = new Dictionary<string, object> {
                    { "FiledInFolders", new[] { purchasing, explicitPath, "Cabinet/A_B/C" } },
                    { "FiledInFoldersInfo", new object[] {
                        Info(purchasing, purchasing.Split('/')),
                        Info(explicitPath, explicitPath.Split('/')),
                        Info("Cabinet/A_B/C", new[] { "Cabinet", "A_B/C" })
                    } }
                };
                string secondary = serializer.Serialize(new { comments = new[] {
                    new { value = "FiledInFolders: Cabinet/Reports/24/01/2024/Invoices" }
                } });
                var merged = Merge(service, settings, serializer.Serialize(original), secondary);
                Equal(new[] { purchasing, explicitPath, "Cabinet/A-B-C", "Cabinet/Reports/24-01-2024/Invoices" },
                    ((object[])merged["FiledInFolders"]).Cast<string>());
                var infos = ((object[])merged["FiledInFoldersInfo"]).Cast<Dictionary<string, object>>().ToArray();
                Equal(purchasing.Split('/'), ((object[])infos[0]["PathParts"]).Cast<string>());
                Equal(explicitPath.Split('/'), ((object[])infos[1]["PathParts"]).Cast<string>());
                Equal(new[] { "Cabinet", "A-B-C" }, ((object[])infos[2]["PathParts"]).Cast<string>());

                // Paths supplied only by secondary metadata must also keep the purchasing hierarchy.
                merged = Merge(service, settings, "{\"FiledInFolders\":[]}",
                    serializer.Serialize(new { comments = new[] { new { value = "FiledInFolders: " + purchasing } } }));
                Equal(new[] { purchasing }, ((object[])merged["FiledInFolders"]).Cast<string>());
                infos = ((object[])merged["FiledInFoldersInfo"]).Cast<Dictionary<string, object>>().ToArray();
                Equal(purchasing.Split('/'), ((object[])infos[0]["PathParts"]).Cast<string>());

                // Database boundaries must survive final per-folder character replacement.
                string[] databaseParts = {
                    "Cabinet", "OPERATIONS DEPT", "VOYAGES", "ALIAI (AIA) MARE 6 INC.",
                    "V 55 ST SHIPPING 24/12/2025", "AO: OPL FLUSHING 28-29/12"
                };
                string databasePath = String.Join("/", databaseParts);
                var lookupPaths = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) {
                    { databasePath, databaseParts }
                };
                var lookup = (FolderPathLookup)Activator.CreateInstance(typeof(FolderPathLookup),
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { lookupPaths }, null);
                var lookupService = new FileMergeService(settings, logger);
                var resolver = (FolderPathResolver)typeof(FileMergeService)
                    .GetField("_folderPathResolver", BindingFlags.Instance | BindingFlags.NonPublic)
                    .GetValue(lookupService);
                typeof(FolderPathResolver).GetField("_folderPathLookup", BindingFlags.Instance | BindingFlags.NonPublic)
                    .SetValue(resolver, lookup);
                var resolved = resolver.Resolve(databasePath);
                if (resolved.Source != "Lookup Table") throw new Exception("Database lookup was bypassed.");
                Equal(databaseParts, resolved.Parts);
                string[] expectedDatabaseParts = {
                    "Cabinet", "OPERATIONS DEPT", "VOYAGES", "ALIAI (AIA) MARE 6 INC.",
                    "V 55 ST SHIPPING 24-12-2025", "AO: OPL FLUSHING 28-29-12"
                };
                string expectedDatabasePath = String.Join("/", expectedDatabaseParts);
                string missesBeforeLookup = ReadActiveLog(logger.LookupMissLogPath);
                foreach (bool alreadyInPrimary in new[] { false, true })
                {
                    string primary = alreadyInPrimary
                        ? serializer.Serialize(new { FiledInFolders = new[] { databasePath },
                            FiledInFoldersInfo = new[] { Info(databasePath, databaseParts) } })
                        : "{\"FiledInFolders\":[]}";
                    merged = Merge(lookupService, settings, primary, serializer.Serialize(new {
                        comments = new[] { new { value = "FiledInFolders: " + databasePath } }
                    }));
                    Equal(new[] { expectedDatabasePath }, ((object[])merged["FiledInFolders"]).Cast<string>());
                    var info = (Dictionary<string, object>)((object[])merged["FiledInFoldersInfo"])[0];
                    Equal(expectedDatabaseParts, ((object[])info["PathParts"]).Cast<string>());
                    if ((string)info["FullPath"] != expectedDatabasePath ||
                        (string)info["Name"] != expectedDatabaseParts.Last())
                        throw new Exception("Database folder metadata was not sanitized consistently.");
                    Equal(databaseParts, resolver.Resolve(databasePath).Parts);
                }
                Console.WriteLine("Database lookup: six levels preserved for existing and added paths; cache unchanged.");
                if (ReadActiveLog(logger.LookupMissLogPath) != missesBeforeLookup)
                    throw new Exception("Database hits were logged as misses.");

                if (args.Length == 2)
                {
                    string primary = File.ReadAllText(args[0]);
                    merged = Merge(service, settings, primary, File.ReadAllText(args[1]));
                    var expected = (Dictionary<string, object>)serializer.DeserializeObject(primary);
                    Equal(((object[])expected["FiledInFolders"]).Cast<string>(), ((object[])merged["FiledInFolders"]).Cast<string>());
                    if (serializer.Serialize(expected["FiledInFoldersInfo"]) != serializer.Serialize(merged["FiledInFoldersInfo"]))
                        throw new Exception("Sample folder metadata changed.");
                    Console.WriteLine("User sample: all paths and folder metadata preserved.");
                }
            }
            string missLog;
            using (var logger = new RunLogger(Path.Combine(root, "miss-logs")))
            {
                missLog = logger.LookupMissLogPath;
                var service = new FileMergeService(settings, logger);
                Merge(service, settings,
                    "{\"FiledInFolders\":[\"Cabinet/A_B\",\"Cabinet/A_B\"]}",
                    "{\"comments\":[{\"value\":\"FiledInFolders: Cabinet/Child\"}]}");
            }
            var missLines = File.ReadAllLines(missLog);
            if (missLines.Length != 4 ||
                !missLines[1].Contains("\tDatabaseLookupMiss\tsample.json\tCabinet/A_B\t") ||
                !missLines[2].Contains("\tDatabaseLookupMiss\tsample.json\tCabinet/Child\t") ||
                !missLines.Last().EndsWith("\tTotalPaths\t\t\t2"))
                throw new Exception("Lookup miss log must contain original paths once per file and a final total.");
            using (var logger = new RunLogger(Path.Combine(root, "empty-logs")))
                missLog = logger.LookupMissLogPath;
            if (!File.ReadAllLines(missLog).Last().EndsWith("\tTotalPaths\t\t\t0"))
                throw new Exception("Empty lookup miss log must contain a zero total.");
            Console.WriteLine("Lookup miss logs: primary/secondary paths, deduplication, database hits and totals passed.");

            string statisticsLog;
            var runStatistics = new MergeStatistics();
            runStatistics.Found();
            runStatistics.Added(true);
            runStatistics.PathChecked("Lookup Table");
            runStatistics.PathChecked("Standard Rules");
            runStatistics.Complete();
            using (var logger = new RunLogger(Path.Combine(root, "statistics-logs")))
            {
                logger.WriteStatistics(runStatistics);
                statisticsLog = logger.StatisticsLogPath;
            }
            string statisticsText = File.ReadAllText(statisticsLog);
            if (!Path.GetFileName(statisticsLog).StartsWith("statistics-") ||
                !statisticsText.Contains("JSON files processed            : 1") ||
                !statisticsText.Contains("Total paths checked             : 2") ||
                !statisticsText.Contains("Identified in database          : 1") ||
                !statisticsText.Contains("Managed by application algorithms: 1") ||
                !statisticsText.Contains("Started                         : ") ||
                !statisticsText.Contains("Ended                           : "))
                throw new Exception("The final statistics log is missing required user-facing totals.");
            Console.WriteLine("Final statistics log: required totals and dates passed.");
            Console.WriteLine("Folder path regression checks passed.");
            return 0;
        }
        finally { Directory.Delete(root, true); }
    }

    private static object Info(string path, string[] parts)
    {
        return new { Name = parts.Last(), FullPath = path, PathParts = parts };
    }

    private static string ReadActiveLog(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var reader = new StreamReader(stream))
            return reader.ReadToEnd();
    }

    private static Dictionary<string, object> Merge(FileMergeService service, AppSettings settings, string primary, string secondary)
    {
        string path = Path.Combine(settings.PrimaryFolder, "sample.json");
        File.WriteAllText(path, primary);
        File.WriteAllText(Path.ChangeExtension(path, ".eml"), "Subject: Regression test\r\n\r\nTest");
        File.WriteAllText(Path.Combine(settings.SecondaryFolder, "sample.json"), secondary);
        typeof(FileMergeService).GetMethod("MergeOne", BindingFlags.Instance | BindingFlags.NonPublic)
            .Invoke(service, new object[] { new WorkItem { PrimaryPath = path, RelativePath = "sample.json" }, new MergeStatistics() });
        return (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(
            File.ReadAllText(Path.Combine(settings.OutputFolder, "sample.json")));
    }
}
