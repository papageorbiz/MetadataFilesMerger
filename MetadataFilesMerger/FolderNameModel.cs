using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MetadataFilesMerger
{
    internal sealed class FolderNameModel
    {
        private const string Header = "# MetadataFilesMerger folder-name model v1";
        private readonly HashSet<string> _names;
        private readonly int _maximumSlashCount;

        private FolderNameModel(IEnumerable<string> names)
        {
            _names = new HashSet<string>(
                names.Select(NormalizeName).Where(x => x.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            _maximumSlashCount = _names.Count == 0
                ? 0
                : _names.Max(name => name.Count(character => character == '/'));
        }

        public int Count { get { return _names.Count; } }
        public int NamesContainingSlash { get { return _names.Count(name => name.IndexOf('/') >= 0); } }

        public static FolderNameModel Empty()
        {
            return new FolderNameModel(new string[0]);
        }

        public static FolderNameModel Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return Empty();
            return new FolderNameModel(
                File.ReadLines(path, Encoding.UTF8)
                    .Where(line => !line.StartsWith("#", StringComparison.Ordinal)));
        }

        public static void Save(string path, IEnumerable<string> names)
        {
            string fullPath = Path.GetFullPath(path);
            string directory = Path.GetDirectoryName(fullPath);
            Directory.CreateDirectory(directory);
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                List<string> lines = names
                    .Select(NormalizeName)
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToList();
                lines.Insert(0, Header);
                File.WriteAllLines(temporary, lines, new UTF8Encoding(false));
                if (File.Exists(fullPath))
                    File.Replace(temporary, fullPath, null);
                else
                    File.Move(temporary, fullPath);
            }
            finally
            {
                if (File.Exists(temporary))
                    File.Delete(temporary);
            }
        }

        public string[] Decompose(string fullPath)
        {
            string normalizedPath = (fullPath ?? String.Empty).Trim();
            if (normalizedPath.Length == 0)
                return new string[0];

            string[] rawParts = normalizedPath
                .Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .Where(part => part.Length > 0)
                .ToArray();

            if (_maximumSlashCount == 0 || rawParts.Length < 2)
                return rawParts;

            List<string> result = new List<string>();
            int index = 0;
            while (index < rawParts.Length)
            {
                int lastCandidate = Math.Min(
                    rawParts.Length - 1,
                    index + _maximumSlashCount);
                string match = null;
                int matchEnd = index;

                for (int end = lastCandidate; end > index; end--)
                {
                    string candidate = String.Join("/", rawParts.Skip(index).Take(end - index + 1));
                    if (_names.Contains(candidate))
                    {
                        match = candidate;
                        matchEnd = end;
                        break;
                    }
                }

                if (match == null)
                {
                    result.Add(rawParts[index]);
                    index++;
                }
                else
                {
                    result.Add(match);
                    index = matchEnd + 1;
                }
            }

            return result.ToArray();
        }

        private static string NormalizeName(string value)
        {
            return (value ?? String.Empty).Trim();
        }
    }
}
