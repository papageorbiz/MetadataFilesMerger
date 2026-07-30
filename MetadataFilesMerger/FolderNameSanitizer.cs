using System;

namespace MetadataFilesMerger
{
    internal static class FolderNameSanitizer
    {
        public static bool RequiresDashReplacement(string folderName)
        {
            return !String.IsNullOrEmpty(folderName) &&
                (folderName.IndexOf('_') >= 0 ||
                folderName.IndexOf('/') >= 0 ||
                folderName.IndexOf('\\') >= 0);
        }

        public static string Sanitize(string value)
        {
            return (value ?? String.Empty)
                .Replace('_', '-')
                .Replace('/', '-')
                .Replace('\\', '-');
        }
    }
}
