using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace MetadataFilesMerger
{
    internal sealed class RunLogger : IDisposable
    {
        private readonly object _sync = new object();
        private readonly StreamWriter _writer;
        private readonly StreamWriter _lookupMissWriter;
        private long _lookupMissCount;
        private bool _disposed;
        public string LogPath { get; private set; }
        public string LookupMissLogPath { get; private set; }
        public string StatisticsLogPath { get; private set; }

        public RunLogger(string logDirectory)
        {
            Directory.CreateDirectory(logDirectory);
            LogPath = Path.Combine(logDirectory, "merge-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            _writer = new StreamWriter(LogPath, false, new UTF8Encoding(false), 65536);
            _writer.AutoFlush = true;
            EnsureDailyAuditLogs();
            LookupMissLogPath = Path.Combine(logDirectory,
                "database-lookup-misses-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + ".log");
            _lookupMissWriter = new StreamWriter(LookupMissLogPath, false, new UTF8Encoding(false));
            _lookupMissWriter.AutoFlush = true;
            _lookupMissWriter.WriteLine("Timestamp\tEvent\tFile\tFullPath\tTotalPaths");
            Info(null, "Run started");
        }

        public void DatabaseLookupMiss(string fileName, string fullPath)
        {
            lock (_sync)
            {
                _lookupMissWriter.WriteLine("{0}\tDatabaseLookupMiss\t{1}\t{2}\t",
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    EscapeTabValue(fileName), EscapeTabValue(fullPath));
                _lookupMissCount++;
            }
        }

        public void Info(string file, string message) { Write("INFO", file, message, null); }
        public void Error(string file, string message, Exception ex) { Write("ERROR", file, message, ex); }

        public void WriteStatistics(MergeStatistics statistics)
        {
            if (statistics == null) throw new ArgumentNullException("statistics");

            lock (_sync)
            {
                StatisticsLogPath = Path.Combine(
                    Path.GetDirectoryName(LogPath),
                    "statistics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff") + ".log");

                using (StreamWriter writer = new StreamWriter(StatisticsLogPath, false, new UTF8Encoding(false)))
                {
                    DateTime started = statistics.StartedUtc.ToLocalTime();
                    DateTime ended = statistics.EndedUtc.ToLocalTime();
                    writer.WriteLine("============================================================");
                    writer.WriteLine("                 PROCESS STATISTICS");
                    writer.WriteLine("============================================================");
                    writer.WriteLine();
                    writer.WriteLine("PROCESS TIME");
                    writer.WriteLine("  Started                         : {0}", started.ToString("yyyy-MM-dd HH:mm:ss zzz"));
                    writer.WriteLine("  Ended                           : {0}", ended.ToString("yyyy-MM-dd HH:mm:ss zzz"));
                    writer.WriteLine("  Duration                        : {0}", (ended - started).ToString(@"hh\:mm\:ss"));
                    writer.WriteLine();
                    writer.WriteLine("FILES");
                    writer.WriteLine("  JSON files processed            : {0:N0}", statistics.Processed);
                    writer.WriteLine();
                    writer.WriteLine("PATH IDENTIFICATION");
                    writer.WriteLine("  Total paths checked             : {0:N0}", statistics.PathsChecked);
                    writer.WriteLine("  Identified in database          : {0:N0}", statistics.DatabasePathsIdentified);
                    writer.WriteLine("  Managed by application algorithms: {0:N0}", statistics.AlgorithmPathsManaged);
                    writer.WriteLine();
                    writer.WriteLine("RESULT");
                    writer.WriteLine("  JSON files updated              : {0:N0}", statistics.Merged);
                    writer.WriteLine("  JSON files unchanged            : {0:N0}", statistics.Unchanged);
                    writer.WriteLine("  JSON files skipped/resumed      : {0:N0}", statistics.Skipped);
                    writer.WriteLine("  Errors                          : {0:N0}", statistics.Errors);
                    writer.WriteLine();
                    writer.WriteLine("============================================================");
                }
            }
        }

        public void SednaMerge(string secondaryFile, int mergedEntries)
        {
            if (mergedEntries <= 0)
                return;
            WriteDailyAudit(
                "sedna-" + DateTime.Now.ToString("ddMMyyyy") + ".log",
                "INFO",
                "SednaMerge",
                Path.GetFileName(secondaryFile),
                "MergedEntries",
                mergedEntries);
        }

        public void DashReplacement(string comboFile, int affectedFolders)
        {
            if (affectedFolders <= 0)
                return;
            WriteDailyAudit(
                "dash-" + DateTime.Now.ToString("ddMMyyyy") + ".log",
                "INFO",
                "DashReplacement",
                Path.GetFileName(comboFile),
                "FoldersRequiringDash",
                affectedFolders);
        }

        public void PathIdentification(
            string fileName,
            string fullPath,
            string source,
            RecognitionSnapshot snapshot)
        {
            lock (_sync)
            {
                string path = Path.Combine(Path.GetDirectoryName(LogPath), "path-identification-" + DateTime.Now.ToString("ddMMyyyy") + ".log");
                EnsureTsvLogHeader(path, PathIdentificationHeader);
                using (StreamWriter writer = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    writer.Write('\t');
                    writer.Write("INFO");
                    writer.Write('\t');
                    writer.Write("PathIdentification");
                    writer.Write('\t');
                    writer.Write(EscapeTabValue(fileName));
                    writer.Write('\t');
                    writer.Write(EscapeTabValue(source));
                    writer.Write('\t');
                    writer.Write(EscapeTabValue(fullPath));
                    writer.Write('\t');
                    writer.Write(snapshot == null ? 0 : snapshot.LookupTableRecognitions);
                    writer.Write('\t');
                    writer.Write(snapshot == null ? 0 : snapshot.NameTrainingRecognitions);
                    writer.Write('\t');
                    writer.Write(snapshot == null ? 0 : snapshot.StandardRulesRecognitions);
                    writer.Write('\t');
                    writer.WriteLine((snapshot == null ? 0 : snapshot.LookupHitRatio).ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        private void Write(string level, string file, string message, Exception ex)
        {
            lock (_sync)
            {
                _writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                _writer.Write(" ["); _writer.Write(level); _writer.Write("] ");
                if (!String.IsNullOrEmpty(file)) { _writer.Write(file); _writer.Write(" | "); }
                _writer.WriteLine(message);
                if (ex != null) _writer.WriteLine(ex.ToString());
            }
        }

        private void WriteDailyAudit(
            string logName,
            string level,
            string eventName,
            string fileName,
            string countLabel,
            int count)
        {
            lock (_sync)
            {
                string path = Path.Combine(Path.GetDirectoryName(LogPath), logName);
                EnsureTsvLogHeader(path, "Timestamp\tLevel\tEvent\tFile\t" + countLabel);
                using (StreamWriter writer = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    writer.Write('\t');
                    writer.Write(level);
                    writer.Write('\t');
                    writer.Write(eventName);
                    writer.Write('\t');
                    writer.Write(EscapeTabValue(fileName));
                    writer.Write('\t');
                    writer.WriteLine(count);
                }
            }
        }

        private void EnsureDailyAuditLogs()
        {
            lock (_sync)
            {
                string directory = Path.GetDirectoryName(LogPath);
                EnsureTsvLogHeader(
                    Path.Combine(directory, "sedna-" + DateTime.Now.ToString("ddMMyyyy") + ".log"),
                    "Timestamp\tLevel\tEvent\tFile\tMergedEntries");
                EnsureTsvLogHeader(
                    Path.Combine(directory, "dash-" + DateTime.Now.ToString("ddMMyyyy") + ".log"),
                    "Timestamp\tLevel\tEvent\tFile\tFoldersRequiringDash");
                EnsureTsvLogHeader(
                    Path.Combine(directory, "path-identification-" + DateTime.Now.ToString("ddMMyyyy") + ".log"),
                    PathIdentificationHeader);
            }
        }

        private const string PathIdentificationHeader =
            "Timestamp\tLevel\tEvent\tFile\tSource\tFullPath\tLookupTableCount\tNameTrainingCount\tStandardRulesCount\tLookupHitRatioPercent";

        private static void EnsureTsvLogHeader(string path, string header)
        {
            FileInfo file = new FileInfo(path);
            if (file.Exists && file.Length > 0)
                return;

            using (StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false)))
                writer.WriteLine(header);
        }

        private static string EscapeTabValue(string value)
        {
            return (value ?? String.Empty)
                .Replace("\\", "\\\\")
                .Replace("\t", "\\t")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                try
                {
                    _lookupMissWriter.WriteLine("{0}\tTotalPaths\t\t\t{1}",
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"), _lookupMissCount);
                }
                finally
                {
                    try { _lookupMissWriter.Dispose(); }
                    finally { _writer.Dispose(); }
                }
            }
        }

        public static void OpenLatest(string logDirectory)
        {
            OpenLatest(logDirectory, "merge-*.log");
        }

        public static void OpenLatestLookupMisses(string logDirectory)
        {
            OpenLatest(logDirectory, "database-lookup-misses-*.log");
        }

        private static void OpenLatest(string logDirectory, string pattern)
        {
            try
            {
                if (!Directory.Exists(logDirectory)) throw new DirectoryNotFoundException("No log directory exists yet.");
                string[] files = Directory.GetFiles(logDirectory, pattern);
                if (files.Length == 0) throw new FileNotFoundException("No log file exists yet.");
                Array.Sort(files, delegate(string left, string right)
                {
                    return File.GetLastWriteTimeUtc(left).CompareTo(File.GetLastWriteTimeUtc(right));
                });
                Process.Start(new ProcessStartInfo(files[files.Length - 1]) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ConsoleUi.WriteError("\n  " + ex.Message);
                ConsoleUi.Pause();
            }
        }
    }
}
