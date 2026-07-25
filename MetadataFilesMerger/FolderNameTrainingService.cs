using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Xml.Linq;

namespace MetadataFilesMerger
{
    internal sealed class FolderNameTrainingService
    {
        public TrainingResult Train(
            AppSettings settings,
            TrainingProgress progress,
            CancellationToken cancellation)
        {
            IEnumerable<string> source;
            if (String.Equals(settings.TrainingSource, "Database", StringComparison.OrdinalIgnoreCase))
                source = ReadDatabase(settings, progress, cancellation);
            else if (String.Equals(settings.TrainingSource, "Spreadsheet", StringComparison.OrdinalIgnoreCase))
                source = ReadSpreadsheet(settings, progress, cancellation);
            else
                source = ReadAdHoc(settings, progress, cancellation);

            List<string> names = new List<string>();
            int sourceRows = 0;
            foreach (string rawName in source)
            {
                cancellation.ThrowIfCancellationRequested();
                sourceRows++;
                string name = NormalizeName(rawName);
                if (name.Length > 0 && name.IndexOfAny(new[] { '\r', '\n' }) < 0)
                    names.Add(name);
            }

            List<string> distinctNames = names
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (distinctNames.Count == 0)
                throw new InvalidDataException("The selected training source did not return any valid folder names.");

            progress.Report("Saving model", distinctNames.Count, distinctNames.Count);
            FolderNameModel.Save(settings.FolderNameModelPath, distinctNames);
            FolderNameModel model = FolderNameModel.Load(settings.FolderNameModelPath);
            progress.Report("Complete", model.Count, model.Count);

            return new TrainingResult
            {
                SourceRows = sourceRows,
                ValidNames = model.Count,
                NamesContainingSlash = model.NamesContainingSlash,
                ModelPath = settings.FolderNameModelPath
            };
        }

        public string Validate(AppSettings settings)
        {
            if (String.Equals(settings.TrainingSource, "Database", StringComparison.OrdinalIgnoreCase))
            {
                if (String.IsNullOrWhiteSpace(settings.SqlServer))
                    return "Enter the SQL Server address.";
                if (String.IsNullOrWhiteSpace(settings.SqlDatabase))
                    return "Enter the SQL Server database.";
                if (String.IsNullOrWhiteSpace(settings.SqlUsername))
                    return "Enter the SQL Server username.";
                if (String.IsNullOrWhiteSpace(settings.TrainingSql))
                    return "Enter the SQL training query.";
            }
            else if (String.Equals(settings.TrainingSource, "Spreadsheet", StringComparison.OrdinalIgnoreCase))
            {
                if (String.IsNullOrWhiteSpace(settings.TrainingSpreadsheetPath) ||
                    !File.Exists(settings.TrainingSpreadsheetPath))
                    return "The training spreadsheet does not exist.";
                string extension = Path.GetExtension(settings.TrainingSpreadsheetPath);
                if (!extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) &&
                    !extension.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                    return "Training spreadsheets must be .xlsx, .csv, .tsv, or .txt files.";
            }
            else if (settings.AdHocFolderNames.Count == 0)
            {
                return "Add at least one ad-hoc folder name.";
            }

            if (String.IsNullOrWhiteSpace(settings.FolderNameModelPath))
                return "The folder-name model path is not configured.";
            return null;
        }

        private static IEnumerable<string> ReadDatabase(
            AppSettings settings,
            TrainingProgress progress,
            CancellationToken cancellation)
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
                ApplicationName = "Metadata Files Merger Training"
            };

            List<string> names = new List<string>();
            progress.Report("Connecting to SQL Server", 0, null);
            using (SqlConnection connection = new SqlConnection(builder.ConnectionString))
            using (SqlCommand command = new SqlCommand(settings.TrainingSql, connection))
            {
                command.CommandTimeout = 0;
                connection.Open();
                progress.Report("Fetching SQL folder names", 0, null);
                using (cancellation.Register(delegate { try { command.Cancel(); } catch (SqlException) { } }))
                using (SqlDataReader reader = command.ExecuteReader(CommandBehavior.SequentialAccess))
                {
                    long processed = 0;
                    while (reader.Read())
                    {
                        cancellation.ThrowIfCancellationRequested();
                        if (!reader.IsDBNull(0))
                            names.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture));
                        processed++;
                        if (processed % 100 == 0)
                            progress.Report("Fetching SQL folder names", processed, null);
                    }
                    progress.Report("Fetching SQL folder names", processed, processed);
                }
            }
            return names;
        }

        private static IEnumerable<string> ReadSpreadsheet(
            AppSettings settings,
            TrainingProgress progress,
            CancellationToken cancellation)
        {
            string extension = Path.GetExtension(settings.TrainingSpreadsheetPath);
            List<string> names;
            if (extension.Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                names = ReadXlsx(settings.TrainingSpreadsheetPath, progress, cancellation);
            else
                names = ReadDelimited(settings.TrainingSpreadsheetPath, extension, progress, cancellation);

            int headerIndex = names.FindIndex(name => !String.IsNullOrWhiteSpace(name));
            if (headerIndex >= 0 &&
                names[headerIndex].Trim().Equals("Name", StringComparison.OrdinalIgnoreCase))
                names.RemoveAt(headerIndex);
            return names;
        }

        private static List<string> ReadDelimited(
            string path,
            string extension,
            TrainingProgress progress,
            CancellationToken cancellation)
        {
            string[] lines = File.ReadAllLines(path);
            char delimiter = extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ',';
            List<string> names = new List<string>();
            progress.Report("Reading spreadsheet", 0, lines.Length);
            for (int index = 0; index < lines.Length; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                names.Add(ReadFirstDelimitedValue(lines[index], delimiter));
                if (index % 100 == 0 || index == lines.Length - 1)
                    progress.Report("Reading spreadsheet", index + 1, lines.Length);
            }
            return names;
        }

        private static string ReadFirstDelimitedValue(string line, char delimiter)
        {
            if (String.IsNullOrEmpty(line))
                return "";
            if (line[0] != '"')
            {
                int delimiterIndex = line.IndexOf(delimiter);
                return delimiterIndex < 0 ? line : line.Substring(0, delimiterIndex);
            }

            System.Text.StringBuilder value = new System.Text.StringBuilder();
            for (int index = 1; index < line.Length; index++)
            {
                if (line[index] != '"')
                {
                    value.Append(line[index]);
                    continue;
                }
                if (index + 1 < line.Length && line[index + 1] == '"')
                {
                    value.Append('"');
                    index++;
                    continue;
                }
                break;
            }
            return value.ToString();
        }

        private static List<string> ReadXlsx(
            string path,
            TrainingProgress progress,
            CancellationToken cancellation)
        {
            progress.Report("Opening spreadsheet", 0, null);
            using (FileStream stream = File.OpenRead(path))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read))
            {
                List<string> sharedStrings = ReadSharedStrings(archive);
                ZipArchiveEntry sheet = archive.Entries
                    .Where(entry => entry.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase) &&
                                    entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(entry => entry.FullName, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (sheet == null)
                    throw new InvalidDataException("The .xlsx file does not contain a worksheet.");

                XDocument document;
                using (Stream sheetStream = sheet.Open())
                    document = XDocument.Load(sheetStream);

                List<XElement> rows = document.Descendants()
                    .Where(element => element.Name.LocalName == "row")
                    .ToList();
                List<string> names = new List<string>();
                progress.Report("Reading spreadsheet", 0, rows.Count);
                for (int index = 0; index < rows.Count; index++)
                {
                    cancellation.ThrowIfCancellationRequested();
                    XElement firstCell = rows[index].Elements()
                        .FirstOrDefault(element =>
                            element.Name.LocalName == "c" &&
                            IsFirstColumn((string)element.Attribute("r")));
                    if (firstCell != null)
                        names.Add(ReadCellValue(firstCell, sharedStrings));
                    if (index % 100 == 0 || index == rows.Count - 1)
                        progress.Report("Reading spreadsheet", index + 1, rows.Count);
                }
                return names;
            }
        }

        private static List<string> ReadSharedStrings(ZipArchive archive)
        {
            ZipArchiveEntry entry = archive.GetEntry("xl/sharedStrings.xml");
            if (entry == null)
                return new List<string>();
            XDocument document;
            using (Stream stream = entry.Open())
                document = XDocument.Load(stream);
            return document.Descendants()
                .Where(element => element.Name.LocalName == "si")
                .Select(item => String.Concat(item.Descendants()
                    .Where(element => element.Name.LocalName == "t")
                    .Select(element => element.Value)))
                .ToList();
        }

        private static bool IsFirstColumn(string reference)
        {
            if (String.IsNullOrWhiteSpace(reference))
                return false;
            int letterCount = 0;
            while (letterCount < reference.Length && Char.IsLetter(reference[letterCount]))
                letterCount++;
            return reference.Substring(0, letterCount).Equals("A", StringComparison.OrdinalIgnoreCase);
        }

        private static string ReadCellValue(XElement cell, IList<string> sharedStrings)
        {
            string type = (string)cell.Attribute("t");
            if (String.Equals(type, "inlineStr", StringComparison.OrdinalIgnoreCase))
                return String.Concat(cell.Descendants()
                    .Where(element => element.Name.LocalName == "t")
                    .Select(element => element.Value));

            XElement valueElement = cell.Elements().FirstOrDefault(element => element.Name.LocalName == "v");
            if (valueElement == null)
                return "";
            if (!String.Equals(type, "s", StringComparison.OrdinalIgnoreCase))
                return valueElement.Value;

            int sharedIndex;
            return Int32.TryParse(valueElement.Value, out sharedIndex) &&
                   sharedIndex >= 0 && sharedIndex < sharedStrings.Count
                ? sharedStrings[sharedIndex]
                : "";
        }

        private static IEnumerable<string> ReadAdHoc(
            AppSettings settings,
            TrainingProgress progress,
            CancellationToken cancellation)
        {
            List<string> names = new List<string>();
            progress.Report("Reading ad-hoc names", 0, settings.AdHocFolderNames.Count);
            for (int index = 0; index < settings.AdHocFolderNames.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                names.Add(settings.AdHocFolderNames[index]);
                progress.Report("Reading ad-hoc names", index + 1, settings.AdHocFolderNames.Count);
            }
            return names;
        }

        private static string NormalizeName(string value)
        {
            return (value ?? String.Empty).Trim();
        }
    }
}
