using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace MetadataFilesMerger
{
    internal sealed class FileMergeService
    {
        private const string Prefix = "FiledInFolders:";
        private readonly AppSettings _settings;
        private readonly RunLogger _logger;
        private readonly FolderNameModel _folderNameModel;

        public FileMergeService(AppSettings settings, RunLogger logger)
        {
            _settings = settings;
            _logger = logger;
            _folderNameModel = settings.FolderNameTrainingEnabled
                ? FolderNameModel.Load(settings.FolderNameModelPath)
                : FolderNameModel.Empty();
        }

        public MergeStatistics Run(CancellationToken cancellation)
        {
            Directory.CreateDirectory(_settings.OutputFolder);
            MergeStatistics statistics = new MergeStatistics();
            using (BlockingCollection<WorkItem> queue = new BlockingCollection<WorkItem>(_settings.WorkerCount * 8))
            using (CancellationTokenSource progressStop = new CancellationTokenSource())
            {
                Task progress = Task.Run(delegate { ConsoleUi.ShowProgress(statistics, progressStop.Token); });
                Task[] workers = Enumerable.Range(0, _settings.WorkerCount)
                    .Select(i => Task.Run(delegate { Consume(queue, statistics, cancellation); })).ToArray();

                try
                {
                    foreach (string file in Directory.EnumerateFiles(_settings.PrimaryFolder, "*.json", SearchOption.AllDirectories))
                    {
                        if (cancellation.IsCancellationRequested) break;
                        statistics.Found();
                        string relative = GetRelativePath(_settings.PrimaryFolder, file);
                        string output = Path.Combine(_settings.OutputFolder, relative);
                        string emlOutput = Path.ChangeExtension(output, ".eml");
                        if (File.Exists(output) && File.Exists(emlOutput)) { statistics.Skip(); continue; }
                        queue.Add(new WorkItem { PrimaryPath = file, RelativePath = relative }, cancellation);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { statistics.Error(); _logger.Error(null, "Error while enumerating primary folder", ex); }
                finally
                {
                    queue.CompleteAdding();
                    Task.WaitAll(workers);
                    progressStop.Cancel();
                    progress.Wait();
                }
            }
            _logger.Info(null, String.Format("Run finished. Processed={0}, merged={1}, unchanged={2}, skipped={3}, errors={4}",
                statistics.Processed, statistics.Merged, statistics.Unchanged, statistics.Skipped, statistics.Errors));
            return statistics;
        }

        private void Consume(BlockingCollection<WorkItem> queue, MergeStatistics statistics, CancellationToken cancellation)
        {
            try
            {
                foreach (WorkItem item in queue.GetConsumingEnumerable(cancellation))
                {
                    try
                    {
                        MergeOutcome outcome = MergeOne(item);
                        statistics.Added(outcome.Changed);
                        if (outcome.Changed)
                            _logger.Info(item.PrimaryPath, "Merge successful: folder metadata was updated.");
                        try
                        {
                            _logger.SednaMerge(
                                Path.Combine(_settings.SecondaryFolder, item.RelativePath),
                                outcome.MergedEntryCount);
                            _logger.DashReplacement(
                                Path.Combine(_settings.OutputFolder, item.RelativePath),
                                outcome.DashFolderCount);
                        }
                        catch (Exception auditException)
                        {
                            _logger.Error(item.RelativePath, "Could not update a daily audit log.", auditException);
                        }
                    }
                    catch (Exception ex)
                    {
                        statistics.Error();
                        _logger.Error(item.RelativePath, ex.Message, ex);
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private MergeOutcome MergeOne(WorkItem item)
        {
            MergeOutcome outcome = new MergeOutcome();
            string destination = Path.Combine(_settings.OutputFolder, item.RelativePath);
            string sourceEml = Path.ChangeExtension(item.PrimaryPath, ".eml");
            string destinationEml = Path.ChangeExtension(destination, ".eml");
            if (!File.Exists(sourceEml))
                throw new FileNotFoundException("Matching primary EML file was not found.", sourceEml);

            // A previous version/run may already have produced the JSON. Repair its
            // missing EML without rewriting the completed merged document.
            if (File.Exists(destination))
            {
                CopyAtomically(sourceEml, destinationEml);
                return outcome;
            }

            string secondary = Path.Combine(_settings.SecondaryFolder, item.RelativePath);
            if (!File.Exists(secondary))
                throw new FileNotFoundException("Matching secondary file was not found.", secondary);

            JavaScriptSerializer serializer = CreateSerializer();
            Dictionary<string, object> secondaryJson = serializer.DeserializeObject(File.ReadAllText(secondary, Encoding.UTF8)) as Dictionary<string, object>;
            HashSet<string> incoming = ReadSecondaryPaths(secondaryJson);
            if (_settings.MergeOnlyTemplateMatchingPaths)
                incoming = new HashSet<string>(incoming.Where(IsTemplateMatchingPath), StringComparer.Ordinal);

            Dictionary<string, object> primaryJson = serializer.DeserializeObject(File.ReadAllText(item.PrimaryPath, Encoding.UTF8)) as Dictionary<string, object>;
            if (primaryJson == null) throw new InvalidDataException("Primary JSON root must be an object.");

            List<string> paths = ReadPrimaryPaths(primaryJson);
            HashSet<string> existing = new HashSet<string>(
                paths.Select(NormalizePath).Where(x => x.Length > 0),
                StringComparer.Ordinal);
            List<string> addedPaths = new List<string>();
            bool changed = false;
            foreach (string path in incoming)
            {
                string normalizedPath = NormalizePath(path);
                if (normalizedPath.Length > 0 && existing.Add(normalizedPath))
                {
                    paths.Add(normalizedPath);
                    addedPaths.Add(normalizedPath);
                    changed = true;
                }
            }
            List<object> folderInfo = null;
            if (primaryJson.ContainsKey("FiledInFoldersInfo") || addedPaths.Count > 0)
            {
                folderInfo = MergeFolderInfo(primaryJson, addedPaths);
                primaryJson["FiledInFoldersInfo"] = folderInfo.ToArray();
            }

            Dictionary<string, string[]> knownPathParts = BuildKnownPathParts(folderInfo);
            HashSet<string> foldersRequiringDash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool pathsSanitized;
            paths = SanitizeFiledInFolders(
                paths,
                knownPathParts,
                foldersRequiringDash,
                out pathsSanitized);
            primaryJson["FiledInFolders"] = paths.ToArray();
            changed = changed || pathsSanitized;

            if (folderInfo != null)
                changed = SanitizeFolderInfo(folderInfo, foldersRequiringDash) || changed;

            if (!changed)
            {
                CopyAtomically(sourceEml, destinationEml);
                CopyAtomically(item.PrimaryPath, destination);
                return outcome;
            }

            string directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Copy the companion first. The JSON is the completion marker used
                // by resume logic, so a stopped run can never skip a missing EML.
                CopyAtomically(sourceEml, destinationEml);
                File.WriteAllText(temporary, serializer.Serialize(primaryJson), new UTF8Encoding(false));
                File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            outcome.Changed = changed;
            outcome.MergedEntryCount = addedPaths.Count;
            outcome.DashFolderCount = foldersRequiringDash.Count;
            return outcome;
        }

        private static void CopyAtomically(string source, string destination)
        {
            string directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.Copy(source, temporary, true);
                if (File.Exists(destination))
                    File.Replace(temporary, destination, null);
                else
                    File.Move(temporary, destination);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private HashSet<string> ReadSecondaryPaths(Dictionary<string, object> root)
        {
            if (root == null) throw new InvalidDataException("Secondary JSON root must be an object.");
            object commentsValue;
            object[] comments;
            if (!root.TryGetValue("comments", out commentsValue) || (comments = commentsValue as object[]) == null)
                return new HashSet<string>(StringComparer.Ordinal);

            HashSet<string> result = new HashSet<string>(StringComparer.Ordinal);
            foreach (object value in comments)
            {
                Dictionary<string, object> comment = value as Dictionary<string, object>;
                object raw;
                if (comment == null || !comment.TryGetValue("value", out raw)) continue;
                string text = raw as string;
                if (text == null || !text.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) continue;
                int colon = text.IndexOf(':');
                foreach (string path in ParseSecondaryPaths(text.Substring(colon + 1)))
                    result.Add(path);
            }
            return result;
        }

        private IEnumerable<string> ParseSecondaryPaths(string value)
        {
            string currentPath = null;
            foreach (CommaFragment commaFragment in SplitCommaFragments(value))
            {
                string fragment = NormalizePath(commaFragment.Value);
                if (fragment.Length == 0) continue;

                if (IsSecondaryPathStart(fragment))
                {
                    if (!String.IsNullOrEmpty(currentPath))
                        yield return currentPath;
                    currentPath = fragment;
                }
                else if (!String.IsNullOrEmpty(currentPath))
                {
                    // A fragment without a slash, or a standalone date-like fragment
                    // such as 01/01/2020, is part of the preceding path; the comma is
                    // path content rather than a path separator. Preserve the original
                    // comma spacing so values such as 15,000 are not changed to 15, 000.
                    currentPath += commaFragment.Separator + fragment;
                }
            }

            if (!String.IsNullOrEmpty(currentPath))
                yield return currentPath;
        }

        private bool IsSecondaryPathStart(string fragment)
        {
            if (IsDateExpression(fragment))
                return false;

            if (IsCabinetPath(fragment))
                return true;

            return IsFolderStartTemplate(fragment);
        }

        private static string GetFirstPathSegment(string path)
        {
            string normalized = NormalizePath(path);
            int slash = normalized.IndexOf('/');
            return slash >= 0 ? normalized.Substring(0, slash).Trim() : normalized;
        }

        private static bool IsCabinetPath(string path)
        {
            return String.Equals(GetFirstPathSegment(path), "Cabinet", StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<CommaFragment> SplitCommaFragments(string value)
        {
            if (value == null)
                yield break;

            int start = 0;
            string separator = String.Empty;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != ',')
                    continue;

                yield return new CommaFragment(separator, value.Substring(start, i - start));

                int nextStart = i + 1;
                while (nextStart < value.Length && Char.IsWhiteSpace(value[nextStart]))
                    nextStart++;

                separator = value.Substring(i, nextStart - i);
                start = nextStart;
                i = nextStart - 1;
            }

            yield return new CommaFragment(separator, value.Substring(start));
        }

        private bool IsFolderStartTemplate(string value)
        {
            string firstSegment = GetFirstPathSegment(value);
            foreach (string pattern in _settings.FolderStartPatterns)
                if (WildcardMatch(value, pattern) || WildcardMatch(firstSegment, pattern))
                    return true;
            return false;
        }

        private bool IsTemplateMatchingPath(string path)
        {
            string normalizedPath = NormalizePath(path);
            if (normalizedPath.Length == 0)
                return false;

            string firstSegment = normalizedPath;
            int slash = normalizedPath.IndexOf('/');
            if (slash >= 0)
                firstSegment = normalizedPath.Substring(0, slash).Trim();

            foreach (string pattern in _settings.FolderStartPatterns)
            {
                if (WildcardMatch(normalizedPath, pattern) || WildcardMatch(firstSegment, pattern))
                    return true;
            }
            return false;
        }

        private static bool IsDateExpression(string value)
        {
            return DateLikeExpression.IsDateExpression(value);
        }

        private static bool WildcardMatch(string value, string pattern)
        {
            int valueIndex = 0, patternIndex = 0, starIndex = -1, matchIndex = 0;
            while (valueIndex < value.Length)
            {
                if (patternIndex < pattern.Length &&
                    Char.ToUpperInvariant(pattern[patternIndex]) == Char.ToUpperInvariant(value[valueIndex]))
                {
                    valueIndex++;
                    patternIndex++;
                }
                else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                {
                    starIndex = patternIndex++;
                    matchIndex = valueIndex;
                }
                else if (starIndex >= 0)
                {
                    patternIndex = starIndex + 1;
                    valueIndex = ++matchIndex;
                }
                else
                {
                    return false;
                }
            }
            while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
                patternIndex++;
            return patternIndex == pattern.Length;
        }

        private static List<string> ReadPrimaryPaths(Dictionary<string, object> root)
        {
            object value;
            if (!root.TryGetValue("FiledInFolders", out value)) return new List<string>();
            object[] array = value as object[];
            if (array == null) throw new InvalidDataException("Primary FiledInFolders property must be an array.");
            return array.Select(x => x as string).Where(x => x != null).ToList();
        }

        private List<object> MergeFolderInfo(
            Dictionary<string, object> root,
            IEnumerable<string> addedPaths)
        {
            object value;
            object[] array;
            if (!root.TryGetValue("FiledInFoldersInfo", out value))
            {
                array = new object[0];
            }
            else
            {
                array = value as object[];
                if (array == null)
                    throw new InvalidDataException("Primary FiledInFoldersInfo property must be an array.");
            }

            List<object> result = array.ToList();
            HashSet<string> existingFullPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (object item in array)
            {
                Dictionary<string, object> info = item as Dictionary<string, object>;
                object fullPathValue;
                if (info != null &&
                    info.TryGetValue("FullPath", out fullPathValue) &&
                    fullPathValue is string)
                {
                    string fullPath = NormalizePath((string)fullPathValue);
                    if (fullPath.Length > 0)
                        existingFullPaths.Add(fullPath);
                }
            }

            foreach (string addedPath in addedPaths)
            {
                string fullPath = NormalizePath(addedPath);
                if (fullPath.Length == 0 || !existingFullPaths.Add(fullPath))
                    continue;

                string[] pathParts = _folderNameModel.Decompose(fullPath);

                result.Add(new Dictionary<string, object>
                {
                    { "Gid", "SEDNA" },
                    { "Name", pathParts.Length > 0 ? pathParts[pathParts.Length - 1] : fullPath },
                    { "PathParts", pathParts },
                    { "FullPath", fullPath }
                });
            }

            return result;
        }

        private static Dictionary<string, string[]> BuildKnownPathParts(IEnumerable<object> folderInfo)
        {
            Dictionary<string, string[]> result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            if (folderInfo == null)
                return result;

            foreach (object item in folderInfo)
            {
                Dictionary<string, object> info = item as Dictionary<string, object>;
                object fullPathValue;
                object pathPartsValue;
                object[] rawParts;
                if (info == null ||
                    !info.TryGetValue("FullPath", out fullPathValue) || !(fullPathValue is string) ||
                    !info.TryGetValue("PathParts", out pathPartsValue) ||
                    (rawParts = pathPartsValue as object[]) == null)
                    continue;

                string fullPath = NormalizePath((string)fullPathValue);
                string[] parts = rawParts.Select(part => part as string)
                    .Where(part => !String.IsNullOrWhiteSpace(part))
                    .Select(part => part.Trim())
                    .ToArray();
                if (fullPath.Length > 0 &&
                    parts.Length > 0 &&
                    PathPartsMatchFullPath(parts, fullPath) &&
                    !FolderNameModel.ContainsSplitDateLikeExpression(parts) &&
                    !result.ContainsKey(fullPath))
                    result.Add(fullPath, parts);
            }
            return result;
        }

        private List<string> SanitizeFiledInFolders(
            IEnumerable<string> paths,
            IDictionary<string, string[]> knownPathParts,
            ISet<string> foldersRequiringDash,
            out bool changed)
        {
            changed = false;
            List<string> result = new List<string>();
            HashSet<string> unique = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in paths)
            {
                string normalizedPath = NormalizePath(path);
                string[] pathParts;
                if (!knownPathParts.TryGetValue(normalizedPath, out pathParts))
                    pathParts = _folderNameModel.Decompose(normalizedPath);

                foreach (string pathPart in pathParts)
                    TrackFolderRequiringDash(pathPart, foldersRequiringDash);
                string sanitizedPath = String.Join("/", pathParts.Select(SanitizeFolderName));
                if (!String.Equals(path, sanitizedPath, StringComparison.Ordinal))
                    changed = true;
                if (sanitizedPath.Length > 0 && unique.Add(sanitizedPath))
                    result.Add(sanitizedPath);
                else if (sanitizedPath.Length > 0)
                    changed = true;
            }
            return result;
        }

        private bool SanitizeFolderInfo(
            IEnumerable<object> folderInfo,
            ISet<string> foldersRequiringDash)
        {
            bool changed = false;
            foreach (object item in folderInfo)
            {
                Dictionary<string, object> info = item as Dictionary<string, object>;
                if (info == null)
                    continue;

                object pathPartsValue;
                object[] pathParts;
                string[] originalParts = null;
                bool pathPartsRecalculated = false;
                if (info.TryGetValue("PathParts", out pathPartsValue) &&
                    (pathParts = pathPartsValue as object[]) != null)
                {
                    originalParts = pathParts.Select(part => part as string)
                        .Where(part => !String.IsNullOrWhiteSpace(part))
                        .Select(part => part.Trim())
                        .ToArray();
                }

                object fullPathValue;
                string originalFullPath = info.TryGetValue("FullPath", out fullPathValue)
                    ? fullPathValue as string
                    : null;
                if (originalParts != null &&
                    originalParts.Length > 0 &&
                    ((!String.IsNullOrWhiteSpace(originalFullPath) &&
                    !PathPartsMatchFullPath(originalParts, originalFullPath)) ||
                    FolderNameModel.ContainsSplitDateLikeExpression(originalParts)))
                {
                    originalParts = null;
                    pathPartsRecalculated = true;
                }
                if ((originalParts == null || originalParts.Length == 0) &&
                    !String.IsNullOrWhiteSpace(originalFullPath))
                {
                    originalParts = _folderNameModel.Decompose(originalFullPath);
                    pathPartsRecalculated = true;
                }

                if (originalParts != null && originalParts.Length > 0)
                {
                    foreach (string originalPart in originalParts)
                        TrackFolderRequiringDash(originalPart, foldersRequiringDash);
                    string[] sanitizedParts = originalParts.Select(SanitizeFolderName).ToArray();
                    if (pathPartsRecalculated ||
                        !originalParts.SequenceEqual(sanitizedParts, StringComparer.Ordinal))
                    {
                        info["PathParts"] = sanitizedParts;
                        changed = true;
                    }

                    string sanitizedFullPath = String.Join("/", sanitizedParts);
                    if (!String.Equals(originalFullPath, sanitizedFullPath, StringComparison.Ordinal))
                    {
                        info["FullPath"] = sanitizedFullPath;
                        changed = true;
                    }
                }

                object nameValue;
                string originalName = info.TryGetValue("Name", out nameValue) ? nameValue as string : null;
                if (originalName != null)
                {
                    TrackFolderRequiringDash(originalName, foldersRequiringDash);
                    string sanitizedName = SanitizeFolderName(originalName);
                    if (!String.Equals(originalName, sanitizedName, StringComparison.Ordinal))
                    {
                        info["Name"] = sanitizedName;
                        changed = true;
                    }
                }
            }
            return changed;
        }

        private static void TrackFolderRequiringDash(string folderName, ISet<string> affectedFolders)
        {
            if (!String.IsNullOrEmpty(folderName) &&
                (folderName.IndexOf('/') >= 0 || folderName.IndexOf('\\') >= 0))
                affectedFolders.Add(folderName);
        }

        private static string SanitizeFolderName(string value)
        {
            return (value ?? String.Empty)
                .Replace('_', '-')
                .Replace('/', '-')
                .Replace('\\', '-');
        }

        private static bool PathPartsMatchFullPath(IEnumerable<string> pathParts, string fullPath)
        {
            return String.Equals(
                String.Join("/", pathParts),
                NormalizePath(fullPath),
                StringComparison.Ordinal);
        }

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 256 };
        }

        private static string NormalizePath(string path)
        {
            return (path ?? String.Empty).Trim();
        }

        private sealed class CommaFragment
        {
            public CommaFragment(string separator, string value)
            {
                Separator = separator;
                Value = value;
            }

            public string Separator { get; private set; }
            public string Value { get; private set; }
        }

        private static string GetRelativePath(string root, string file)
        {
            string normalized = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Uri rootUri = new Uri(normalized);
            return Uri.UnescapeDataString(rootUri.MakeRelativeUri(new Uri(Path.GetFullPath(file))).ToString())
                .Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
