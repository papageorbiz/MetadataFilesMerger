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
        public string LogPath { get; private set; }

        public RunLogger(string logDirectory)
        {
            Directory.CreateDirectory(logDirectory);
            LogPath = Path.Combine(logDirectory, "merge-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
            _writer = new StreamWriter(LogPath, false, new UTF8Encoding(false), 65536);
            _writer.AutoFlush = true;
            Info(null, "Run started");
        }

        public void Info(string file, string message) { Write("INFO", file, message, null); }
        public void Error(string file, string message, Exception ex) { Write("ERROR", file, message, ex); }

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
                using (StreamWriter writer = new StreamWriter(path, true, new UTF8Encoding(false)))
                {
                    writer.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
                    writer.Write(" [");
                    writer.Write(level);
                    writer.Write("] Event=");
                    writer.Write(eventName);
                    writer.Write(" | File=\"");
                    writer.Write(EscapeLogValue(fileName));
                    writer.Write("\" | ");
                    writer.Write(countLabel);
                    writer.Write('=');
                    writer.WriteLine(count);
                }
            }
        }

        private static string EscapeLogValue(string value)
        {
            return (value ?? String.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
        }

        public void Dispose() { lock (_sync) { _writer.Dispose(); } }

        public static void OpenLatest(string logDirectory)
        {
            try
            {
                if (!Directory.Exists(logDirectory)) throw new DirectoryNotFoundException("No log directory exists yet.");
                string[] files = Directory.GetFiles(logDirectory, "merge-*.log");
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
