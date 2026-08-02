using System;
using System.Threading;

namespace MetadataFilesMerger
{
    internal sealed class MergeStatistics
    {
        private long _discovered, _processed, _merged, _unchanged, _skipped, _errors;
        private long _lookupTableRecognitions, _nameTrainingRecognitions, _standardRulesRecognitions;
        private readonly DateTime _started = DateTime.UtcNow;

        public long Discovered { get { return Interlocked.Read(ref _discovered); } }
        public long Processed { get { return Interlocked.Read(ref _processed); } }
        public long Merged { get { return Interlocked.Read(ref _merged); } }
        public long Unchanged { get { return Interlocked.Read(ref _unchanged); } }
        public long Skipped { get { return Interlocked.Read(ref _skipped); } }
        public long Errors { get { return Interlocked.Read(ref _errors); } }
        public long LookupTableRecognitions { get { return Interlocked.Read(ref _lookupTableRecognitions); } }
        public long NameTrainingRecognitions { get { return Interlocked.Read(ref _nameTrainingRecognitions); } }
        public long StandardRulesRecognitions { get { return Interlocked.Read(ref _standardRulesRecognitions); } }
        public long TotalRecognitions { get { return LookupTableRecognitions + NameTrainingRecognitions + StandardRulesRecognitions; } }
        public double LookupHitRatio
        {
            get
            {
                long total = TotalRecognitions;
                return total == 0 ? 0 : LookupTableRecognitions * 100.0 / total;
            }
        }
        public TimeSpan Elapsed { get { return DateTime.UtcNow - _started; } }

        public void Found() { Interlocked.Increment(ref _discovered); }
        public void Added(bool changed) { Interlocked.Increment(ref _processed); if (changed) Interlocked.Increment(ref _merged); else Interlocked.Increment(ref _unchanged); }
        public void Skip() { Interlocked.Increment(ref _skipped); }
        public void Error() { Interlocked.Increment(ref _errors); }
        public RecognitionSnapshot Recognized(string source)
        {
            if (String.Equals(source, "Lookup Table", StringComparison.Ordinal))
                Interlocked.Increment(ref _lookupTableRecognitions);
            else if (String.Equals(source, "Name Training", StringComparison.Ordinal))
                Interlocked.Increment(ref _nameTrainingRecognitions);
            else
                Interlocked.Increment(ref _standardRulesRecognitions);

            return new RecognitionSnapshot
            {
                LookupTableRecognitions = LookupTableRecognitions,
                NameTrainingRecognitions = NameTrainingRecognitions,
                StandardRulesRecognitions = StandardRulesRecognitions,
                LookupHitRatio = LookupHitRatio
            };
        }
    }

    internal sealed class RecognitionSnapshot
    {
        public long LookupTableRecognitions { get; set; }
        public long NameTrainingRecognitions { get; set; }
        public long StandardRulesRecognitions { get; set; }
        public double LookupHitRatio { get; set; }
    }

    internal sealed class WorkItem
    {
        public string PrimaryPath { get; set; }
        public string RelativePath { get; set; }
    }

    internal sealed class MergeOutcome
    {
        public bool Changed { get; set; }
        public int MergedEntryCount { get; set; }
        public int DashFolderCount { get; set; }
    }

    internal sealed class TrainingProgress
    {
        private long _processed;
        private long _total = -1;
        private string _stage = "Preparing";

        public long Processed { get { return Interlocked.Read(ref _processed); } }
        public long? Total
        {
            get
            {
                long value = Interlocked.Read(ref _total);
                return value < 0 ? (long?)null : value;
            }
        }
        public string Stage { get { return _stage; } }

        public void Report(string stage, long processed, long? total)
        {
            _stage = stage;
            Interlocked.Exchange(ref _processed, processed);
            Interlocked.Exchange(ref _total, total ?? -1);
        }
    }

    internal sealed class TrainingResult
    {
        public int SourceRows { get; set; }
        public int ValidNames { get; set; }
        public int NamesContainingSlash { get; set; }
        public string ModelPath { get; set; }
    }

    internal sealed class FolderHierarchyExportResult
    {
        public int SourceRows { get; set; }
        public int ExportedPaths { get; set; }
        public int MaximumLevels { get; set; }
        public string OutputPath { get; set; }
    }
}
