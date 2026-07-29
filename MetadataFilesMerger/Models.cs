using System;
using System.Threading;

namespace MetadataFilesMerger
{
    internal sealed class MergeStatistics
    {
        private long _discovered, _processed, _merged, _unchanged, _skipped, _errors;
        private readonly DateTime _started = DateTime.UtcNow;

        public long Discovered { get { return Interlocked.Read(ref _discovered); } }
        public long Processed { get { return Interlocked.Read(ref _processed); } }
        public long Merged { get { return Interlocked.Read(ref _merged); } }
        public long Unchanged { get { return Interlocked.Read(ref _unchanged); } }
        public long Skipped { get { return Interlocked.Read(ref _skipped); } }
        public long Errors { get { return Interlocked.Read(ref _errors); } }
        public TimeSpan Elapsed { get { return DateTime.UtcNow - _started; } }

        public void Found() { Interlocked.Increment(ref _discovered); }
        public void Added(bool changed) { Interlocked.Increment(ref _processed); if (changed) Interlocked.Increment(ref _merged); else Interlocked.Increment(ref _unchanged); }
        public void Skip() { Interlocked.Increment(ref _skipped); }
        public void Error() { Interlocked.Increment(ref _errors); }
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
}
