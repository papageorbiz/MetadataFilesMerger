using System;
using System.Configuration;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MetadataFilesMerger
{
    internal sealed class AppSettings
    {
        public const string DefaultTrainingSql = "SELECT [Name] FROM [sys_Folder]";

        public string PrimaryFolder { get; set; }
        public string SecondaryFolder { get; set; }
        public string OutputFolder { get; set; }
        public string LogFolder { get; set; }
        public List<string> FolderStartPatterns { get; set; }
        public bool MergeOnlyTemplateMatchingPaths { get; set; }
        public int WorkerCount { get; set; }
        public bool FolderNameTrainingEnabled { get; set; }
        public string TrainingSource { get; set; }
        public string SqlServer { get; set; }
        public int SqlPort { get; set; }
        public string SqlDatabase { get; set; }
        public string SqlUsername { get; set; }
        public string SqlPassword { get; set; }
        public string TrainingSql { get; set; }
        public string TrainingSpreadsheetPath { get; set; }
        public List<string> AdHocFolderNames { get; set; }
        public string FolderNameModelPath { get; set; }

        public static AppSettings Load()
        {
            int workers;
            if (!int.TryParse(ConfigurationManager.AppSettings["WorkerCount"], out workers) || workers <= 0)
                workers = Math.Max(2, Environment.ProcessorCount);

            return new AppSettings
            {
                PrimaryFolder = ConfigurationManager.AppSettings["PrimaryFolder"] ?? "",
                SecondaryFolder = ConfigurationManager.AppSettings["SecondaryFolder"] ?? "",
                OutputFolder = ConfigurationManager.AppSettings["OutputFolder"] ?? "",
                LogFolder = ConfigurationManager.AppSettings["LogFolder"] ?? "",
                FolderStartPatterns = ParsePatterns(ConfigurationManager.AppSettings["FolderStartPatterns"]),
                MergeOnlyTemplateMatchingPaths = ParseBoolean(ConfigurationManager.AppSettings["MergeOnlyTemplateMatchingPaths"]),
                WorkerCount = workers,
                FolderNameTrainingEnabled = ParseBoolean(ConfigurationManager.AppSettings["FolderNameTrainingEnabled"]),
                TrainingSource = ParseTrainingSource(ConfigurationManager.AppSettings["TrainingSource"]),
                SqlServer = ConfigurationManager.AppSettings["TrainingSqlServer"] ?? "",
                SqlPort = ParsePort(ConfigurationManager.AppSettings["TrainingSqlPort"]),
                SqlDatabase = ConfigurationManager.AppSettings["TrainingSqlDatabase"] ?? "",
                SqlUsername = ConfigurationManager.AppSettings["TrainingSqlUsername"] ?? "",
                SqlPassword = Unprotect(ConfigurationManager.AppSettings["TrainingSqlPassword"]),
                TrainingSql = String.IsNullOrWhiteSpace(ConfigurationManager.AppSettings["TrainingSql"])
                    ? DefaultTrainingSql
                    : ConfigurationManager.AppSettings["TrainingSql"],
                TrainingSpreadsheetPath = ConfigurationManager.AppSettings["TrainingSpreadsheetPath"] ?? "",
                AdHocFolderNames = ParseEncodedLines(ConfigurationManager.AppSettings["AdHocFolderNames"]),
                FolderNameModelPath = String.IsNullOrWhiteSpace(ConfigurationManager.AppSettings["FolderNameModelPath"])
                    ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "FolderNameModel.txt")
                    : ConfigurationManager.AppSettings["FolderNameModelPath"]
            };
        }

        private static bool ParseBoolean(string configured)
        {
            bool value;
            return Boolean.TryParse(configured, out value) && value;
        }

        private static List<string> ParsePatterns(string configured)
        {
            if (String.IsNullOrWhiteSpace(configured))
                configured = "Smart folder|*-Private";
            return configured.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string ParseTrainingSource(string configured)
        {
            if (String.Equals(configured, "Database", StringComparison.OrdinalIgnoreCase))
                return "Database";
            if (String.Equals(configured, "Spreadsheet", StringComparison.OrdinalIgnoreCase))
                return "Spreadsheet";
            return "AdHoc";
        }

        private static int ParsePort(string configured)
        {
            int port;
            return Int32.TryParse(configured, out port) && port >= 0 && port <= 65535 ? port : 0;
        }

        private static List<string> ParseEncodedLines(string configured)
        {
            if (String.IsNullOrWhiteSpace(configured))
                return new List<string>();
            try
            {
                string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(configured));
                return decoded.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (FormatException)
            {
                return new List<string>();
            }
        }

        public void SavePatterns()
        {
            Configuration configuration = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            KeyValueConfigurationCollection settings = configuration.AppSettings.Settings;
            string value = String.Join("|", FolderStartPatterns);
            if (settings["FolderStartPatterns"] == null)
                settings.Add("FolderStartPatterns", value);
            else
                settings["FolderStartPatterns"].Value = value;
            configuration.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }

        public void SaveMergeOptions()
        {
            Configuration configuration = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            KeyValueConfigurationCollection settings = configuration.AppSettings.Settings;
            string value = MergeOnlyTemplateMatchingPaths.ToString();
            if (settings["MergeOnlyTemplateMatchingPaths"] == null)
                settings.Add("MergeOnlyTemplateMatchingPaths", value);
            else
                settings["MergeOnlyTemplateMatchingPaths"].Value = value;
            configuration.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }

        public void SaveTrainingSettings()
        {
            Configuration configuration = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
            KeyValueConfigurationCollection settings = configuration.AppSettings.Settings;
            Set(settings, "FolderNameTrainingEnabled", FolderNameTrainingEnabled.ToString());
            Set(settings, "TrainingSource", TrainingSource);
            Set(settings, "TrainingSqlServer", SqlServer);
            Set(settings, "TrainingSqlPort", SqlPort.ToString());
            Set(settings, "TrainingSqlDatabase", SqlDatabase);
            Set(settings, "TrainingSqlUsername", SqlUsername);
            Set(settings, "TrainingSqlPassword", Protect(SqlPassword));
            Set(settings, "TrainingSql", TrainingSql);
            Set(settings, "TrainingSpreadsheetPath", TrainingSpreadsheetPath);
            Set(settings, "AdHocFolderNames", Convert.ToBase64String(
                Encoding.UTF8.GetBytes(String.Join("\n", AdHocFolderNames))));
            Set(settings, "FolderNameModelPath", FolderNameModelPath);
            configuration.Save(ConfigurationSaveMode.Modified);
            ConfigurationManager.RefreshSection("appSettings");
        }

        private static void Set(KeyValueConfigurationCollection settings, string key, string value)
        {
            if (settings[key] == null)
                settings.Add(key, value ?? "");
            else
                settings[key].Value = value ?? "";
        }

        private static string Protect(string value)
        {
            if (String.IsNullOrEmpty(value))
                return "";
            byte[] clear = Encoding.UTF8.GetBytes(value);
            byte[] protectedValue = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(protectedValue);
        }

        private static string Unprotect(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return "";
            if (!value.StartsWith("dpapi:", StringComparison.OrdinalIgnoreCase))
                return value;
            try
            {
                byte[] protectedValue = Convert.FromBase64String(value.Substring(6));
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(protectedValue, null, DataProtectionScope.CurrentUser));
            }
            catch (CryptographicException)
            {
                return "";
            }
            catch (FormatException)
            {
                return "";
            }
        }

        public string EffectiveLogFolder
        {
            get
            {
                return String.IsNullOrWhiteSpace(LogFolder)
                    ? Path.Combine(OutputFolder ?? "", "_logs")
                    : LogFolder;
            }
        }

        public string Validate()
        {
            if (String.IsNullOrWhiteSpace(PrimaryFolder) || !Directory.Exists(PrimaryFolder))
                return "The primary folder does not exist.";
            if (String.IsNullOrWhiteSpace(SecondaryFolder) || !Directory.Exists(SecondaryFolder))
                return "The secondary folder does not exist.";
            if (String.IsNullOrWhiteSpace(OutputFolder))
                return "Please select an output folder.";

            string primary = Normalize(PrimaryFolder);
            string secondary = Normalize(SecondaryFolder);
            string output = Normalize(OutputFolder);
            if (primary == secondary || primary == output || secondary == output)
                return "Primary, secondary, and output folders must be different.";
            if (output.StartsWith(primary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "The output folder cannot be inside the primary folder.";
            if (output.StartsWith(secondary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return "The output folder cannot be inside the secondary folder.";
            if (FolderNameTrainingEnabled && !File.Exists(FolderNameModelPath))
                return "Folder-name training is enabled, but no trained model exists. Run training from the main menu.";
            return null;
        }

        private static string Normalize(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }
}
