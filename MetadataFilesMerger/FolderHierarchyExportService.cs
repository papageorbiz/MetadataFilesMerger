using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;

namespace MetadataFilesMerger
{
    internal sealed class FolderHierarchyExportService
    {
        public FolderHierarchyExportResult Export(
            AppSettings settings,
            string outputPath,
            TrainingProgress progress,
            CancellationToken cancellation)
        {
            string validation = Validate(settings, outputPath);
            if (validation != null)
                throw new InvalidDataException(validation);

            progress.Report("Reading sys_Folder", 0, null);
            List<FolderRecord> records = FolderPathLookup.ReadFolders(settings, cancellation);
            progress.Report("Reading sys_Folder", records.Count, records.Count);
            progress.Report("Reconstructing folder paths", 0, records.Count);

            Dictionary<int, FolderRecord> byId = records
                .GroupBy(record => record.Id)
                .Select(group => group.First())
                .ToDictionary(record => record.Id);

            List<FolderPathRow> rows = new List<FolderPathRow>();
            int processed = 0;
            foreach (FolderRecord record in records.OrderBy(record => record.IdParent).ThenBy(record => record.Position).ThenBy(record => record.Id))
            {
                cancellation.ThrowIfCancellationRequested();
                string[] parts = FolderPathLookup.BuildPathParts(record, byId);
                rows.Add(new FolderPathRow { Parts = parts });
                processed++;
                if (processed % 100 == 0 || processed == records.Count)
                    progress.Report("Reconstructing folder paths", processed, records.Count);
            }

            rows = rows
                .OrderBy(row => row.FullPath, StringComparer.OrdinalIgnoreCase)
                .ToList();

            progress.Report("Writing Excel file", rows.Count, rows.Count);
            WriteWorkbook(outputPath, rows);
            progress.Report("Complete", rows.Count, rows.Count);

            return new FolderHierarchyExportResult
            {
                SourceRows = records.Count,
                ExportedPaths = rows.Count,
                MaximumLevels = rows.Count == 0 ? 0 : rows.Max(row => row.Parts.Length),
                OutputPath = outputPath
            };
        }

        public string Validate(AppSettings settings, string outputPath)
        {
            if (String.IsNullOrWhiteSpace(settings.SqlServer))
                return "Enter the SQL Server address in Intelligent Folder Recognition first.";
            if (String.IsNullOrWhiteSpace(settings.SqlDatabase))
                return "Enter the SQL Server database in Intelligent Folder Recognition first.";
            if (String.IsNullOrWhiteSpace(settings.SqlUsername))
                return "Enter the SQL Server username in Intelligent Folder Recognition first.";
            if (String.IsNullOrWhiteSpace(outputPath))
                return "Enter an Excel output path.";
            if (!Path.GetExtension(outputPath).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                return "The output file must have the .xlsx extension.";
            return null;
        }

        private static void WriteWorkbook(string outputPath, IList<FolderPathRow> rows)
        {
            string fullPath = Path.GetFullPath(outputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
            string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = File.Create(temporary))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
                {
                    AddTextEntry(archive, "[Content_Types].xml", ContentTypesXml());
                    AddTextEntry(archive, "_rels/.rels", RootRelationshipsXml());
                    AddTextEntry(archive, "xl/workbook.xml", WorkbookXml());
                    AddTextEntry(archive, "xl/_rels/workbook.xml.rels", WorkbookRelationshipsXml());
                    AddTextEntry(archive, "xl/styles.xml", StylesXml());
                    AddTextEntry(archive, "xl/worksheets/sheet1.xml", WorksheetXml(rows));
                }

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

        private static void AddTextEntry(ZipArchive archive, string path, string text)
        {
            ZipArchiveEntry entry = archive.CreateEntry(path, CompressionLevel.Optimal);
            using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                writer.Write(text);
        }

        private static string WorksheetXml(IList<FolderPathRow> rows)
        {
            int maxLevels = rows.Count == 0 ? 0 : rows.Max(row => row.Parts.Length);
            int totalColumns = Math.Max(3 + maxLevels, 4);
            StringBuilder xml = new StringBuilder();
            xml.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
            xml.Append("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");
            xml.Append("<dimension ref=\"A1:" + ColumnName(totalColumns) + (rows.Count + 1).ToString(CultureInfo.InvariantCulture) + "\"/>");
            xml.Append("<sheetViews><sheetView workbookViewId=\"0\"/></sheetViews>");
            xml.Append("<sheetFormatPr defaultRowHeight=\"15\"/>");
            xml.Append("<sheetData>");
            WriteRow(xml, 1, BuildHeader(maxLevels), true);
            for (int index = 0; index < rows.Count; index++)
            {
                FolderPathRow row = rows[index];
                List<string> values = new List<string>
                {
                    (index + 1).ToString(CultureInfo.InvariantCulture),
                    row.Parts.Length.ToString(CultureInfo.InvariantCulture),
                    row.FullPath
                };
                values.AddRange(row.Parts);
                WriteRow(xml, index + 2, values, false);
            }
            xml.Append("</sheetData>");
            xml.Append("<autoFilter ref=\"A1:" + ColumnName(totalColumns) + "1\"/>");
            xml.Append("</worksheet>");
            return xml.ToString();
        }

        private static List<string> BuildHeader(int maxLevels)
        {
            List<string> headers = new List<string> { "#", "Levels", "Full Path" };
            for (int level = 1; level <= maxLevels; level++)
                headers.Add(level == 1
                    ? "Root Folder"
                    : "Level " + level.ToString(CultureInfo.InvariantCulture));
            return headers;
        }

        private static void WriteRow(StringBuilder xml, int rowIndex, IList<string> values, bool header)
        {
            xml.Append("<row r=\"" + rowIndex.ToString(CultureInfo.InvariantCulture) + "\">");
            for (int index = 0; index < values.Count; index++)
            {
                string reference = ColumnName(index + 1) + rowIndex.ToString(CultureInfo.InvariantCulture);
                xml.Append("<c r=\"" + reference + "\"" + (header ? " s=\"1\"" : "") + " t=\"inlineStr\"><is><t>");
                xml.Append(Escape(values[index] ?? ""));
                xml.Append("</t></is></c>");
            }
            xml.Append("</row>");
        }

        private static string ColumnName(int number)
        {
            StringBuilder result = new StringBuilder();
            while (number > 0)
            {
                number--;
                result.Insert(0, (char)('A' + number % 26));
                number /= 26;
            }
            return result.ToString();
        }

        private static string Escape(string value)
        {
            return value
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
        }

        private static string ContentTypesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>" +
                "<Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>" +
                "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>" +
                "</Types>";
        }

        private static string RootRelationshipsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>" +
                "</Relationships>";
        }

        private static string WorkbookXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\">" +
                "<sheets><sheet name=\"Folder Paths\" sheetId=\"1\" r:id=\"rId1\"/></sheets>" +
                "</workbook>";
        }

        private static string WorkbookRelationshipsXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/>" +
                "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
                "</Relationships>";
        }

        private static string StylesXml()
        {
            return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
                "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
                "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>" +
                "<fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills>" +
                "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>" +
                "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
                "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyFont=\"1\"/></cellXfs>" +
                "</styleSheet>";
        }

        private sealed class FolderPathRow
        {
            public string[] Parts { get; set; }
            public string FullPath { get { return FolderPathLookup.JoinPath(Parts ?? new string[0]); } }
        }
    }
}
