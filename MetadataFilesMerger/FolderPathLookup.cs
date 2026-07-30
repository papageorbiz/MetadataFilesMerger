using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace MetadataFilesMerger
{
    internal sealed class FolderPathLookup
    {
        private const string FolderQuery =
            "SELECT [Id], [Id_Parent], [Name], [Description], [FolderType], [Position], [MaxItems] FROM [sys_Folder]";

        private static readonly Regex PrefixedRoot = new Regex(
            @"^[A-Za-z]+-(?<root>.+)$",
            RegexOptions.Compiled);

        private readonly Dictionary<string, string[]> _paths;

        private FolderPathLookup(Dictionary<string, string[]> paths)
        {
            _paths = paths;
        }

        public int Count { get { return _paths.Count; } }

        public static FolderPathLookup Empty()
        {
            return new FolderPathLookup(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase));
        }

        public static FolderPathLookup Load(AppSettings settings, CancellationToken cancellation)
        {
            List<FolderRecord> records = ReadFolders(settings, cancellation);
            Dictionary<int, FolderRecord> byId = records
                .GroupBy(record => record.Id)
                .Select(group => group.First())
                .ToDictionary(record => record.Id);

            Dictionary<string, string[]> paths = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            foreach (FolderRecord record in records)
            {
                string[] parts = BuildPathParts(record, byId);
                string fullPath = JoinPath(parts);
                if (fullPath.Length > 0 && !paths.ContainsKey(fullPath))
                    paths.Add(fullPath, parts);
            }
            return new FolderPathLookup(paths);
        }

        public bool TryResolve(string fullPath, out string[] parts)
        {
            string normalized = NormalizePath(fullPath);
            if (_paths.TryGetValue(normalized, out parts))
                return true;

            string withoutPrefix;
            string prefixedRoot;
            if (TryRemoveRootPrefix(normalized, out withoutPrefix, out prefixedRoot) &&
                _paths.TryGetValue(withoutPrefix, out parts))
            {
                parts = ReplaceFirstPart(parts, prefixedRoot);
                return true;
            }

            parts = null;
            return false;
        }

        public static string Query { get { return FolderQuery; } }

        public static List<FolderRecord> ReadFolders(AppSettings settings, CancellationToken cancellation)
        {
            string dataSource = settings.SqlServer.Trim();
            if (settings.SqlPort > 0)
                dataSource += "," + settings.SqlPort.ToString(CultureInfo.InvariantCulture);

            SqlConnectionStringBuilder builder = new SqlConnectionStringBuilder
            {
                DataSource = dataSource,
                InitialCatalog = settings.SqlDatabase.Trim(),
                UserID = settings.SqlUsername.Trim(),
                Password = settings.SqlPassword ?? "",
                IntegratedSecurity = false,
                ConnectTimeout = 30,
                ApplicationName = "Metadata Files Merger Folder Lookup"
            };

            List<FolderRecord> records = new List<FolderRecord>();
            using (SqlConnection connection = new SqlConnection(builder.ConnectionString))
            using (SqlCommand command = new SqlCommand(FolderQuery, connection))
            {
                command.CommandTimeout = 0;
                connection.Open();
                using (cancellation.Register(delegate { try { command.Cancel(); } catch (SqlException) { } }))
                using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    while (reader.Read())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        records.Add(new FolderRecord
                        {
                            Id = ReadInt(reader, 0),
                            IdParent = ReadInt(reader, 1),
                            Name = reader.IsDBNull(2) ? "" : Convert.ToString(reader.GetValue(2), CultureInfo.InvariantCulture),
                            Position = ReadInt(reader, 5)
                        });
                    }
                }
            }
            return records;
        }

        public static string[] BuildPathParts(FolderRecord record, IDictionary<int, FolderRecord> byId)
        {
            List<string> parts = new List<string>();
            HashSet<int> visited = new HashSet<int>();
            FolderRecord current = record;
            while (current != null && visited.Add(current.Id))
            {
                if (!String.IsNullOrWhiteSpace(current.Name))
                    parts.Add(current.Name.Trim());

                if (current.IdParent == 0 || !byId.TryGetValue(current.IdParent, out current))
                    break;
            }

            parts.Reverse();
            return parts.ToArray();
        }

        public static string JoinPath(IEnumerable<string> parts)
        {
            return String.Join("/", parts
                .Where(part => !String.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim()));
        }

        private static int ReadInt(SqlDataReader reader, int ordinal)
        {
            return reader.IsDBNull(ordinal)
                ? 0
                : Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
        }

        private static bool TryRemoveRootPrefix(string fullPath, out string withoutPrefix, out string prefixedRoot)
        {
            withoutPrefix = null;
            prefixedRoot = null;
            if (String.IsNullOrWhiteSpace(fullPath))
                return false;

            int slash = fullPath.IndexOf('/');
            string firstPart = slash >= 0 ? fullPath.Substring(0, slash).Trim() : fullPath.Trim();
            Match match = PrefixedRoot.Match(firstPart);
            if (!match.Success)
                return false;

            string unprefixedRoot = match.Groups["root"].Value.Trim();
            if (unprefixedRoot.Length == 0)
                return false;

            prefixedRoot = firstPart;
            withoutPrefix = slash >= 0
                ? unprefixedRoot + fullPath.Substring(slash)
                : unprefixedRoot;
            return true;
        }

        private static string[] ReplaceFirstPart(string[] parts, string firstPart)
        {
            string[] result = parts.ToArray();
            if (result.Length == 0)
                return new[] { firstPart };
            result[0] = firstPart;
            return result;
        }

        private static string NormalizePath(string path)
        {
            return (path ?? String.Empty).Trim();
        }
    }

    internal sealed class FolderRecord
    {
        public int Id { get; set; }
        public int IdParent { get; set; }
        public string Name { get; set; }
        public int Position { get; set; }
    }
}
