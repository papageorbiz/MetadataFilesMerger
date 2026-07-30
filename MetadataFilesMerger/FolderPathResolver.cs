using System;
using System.IO;
using System.Threading;

namespace MetadataFilesMerger
{
    internal sealed class FolderPathResolver
    {
        private readonly AppSettings _settings;
        private readonly FolderNameModel _folderNameModel;
        private readonly FolderPathLookup _folderPathLookup;

        public FolderPathResolver(AppSettings settings, RunLogger logger)
        {
            _settings = settings;
            _folderNameModel = settings.FolderNameTrainingEnabled
                ? FolderNameModel.Load(settings.FolderNameModelPath)
                : FolderNameModel.Empty();
            _folderPathLookup = LoadFolderPathLookup(settings, logger);
        }

        public ResolvedPathParts Resolve(string fullPath)
        {
            string[] lookupParts;
            if (_folderPathLookup.TryResolve(fullPath, out lookupParts))
                return new ResolvedPathParts(lookupParts, "Lookup Table");

            string source = _settings.FolderNameTrainingEnabled
                ? "Name Training"
                : "Standard Rules";
            return new ResolvedPathParts(_folderNameModel.Decompose(fullPath), source);
        }

        private static FolderPathLookup LoadFolderPathLookup(AppSettings settings, RunLogger logger)
        {
            if (String.IsNullOrWhiteSpace(settings.SqlServer) ||
                String.IsNullOrWhiteSpace(settings.SqlDatabase) ||
                String.IsNullOrWhiteSpace(settings.SqlUsername))
                return FolderPathLookup.Empty();

            try
            {
                FolderPathLookup lookup = FolderPathLookup.Load(settings, CancellationToken.None);
                if (logger != null)
                    logger.Info(null, "Folder path lookup loaded: " + lookup.Count.ToString() + " paths.");
                return lookup;
            }
            catch (Exception ex)
            {
                if (logger != null)
                    logger.Error(null, "Could not load folder path lookup. Folder recognition will use fallback rules.", ex);
                return FolderPathLookup.Empty();
            }
        }
    }

    internal sealed class ResolvedPathParts
    {
        public ResolvedPathParts(string[] parts, string source)
        {
            Parts = parts ?? new string[0];
            Source = source;
        }

        public string[] Parts { get; private set; }
        public string Source { get; private set; }
    }
}
