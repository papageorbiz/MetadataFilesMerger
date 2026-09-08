using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MetadataFilesMerger
{
    internal static class DateLikeExpression
    {
        private static readonly string[] DateFormats =
        {
            "d/M/yyyy", "dd/MM/yyyy", "d/MM/yyyy", "dd/M/yyyy",
            "M/d/yyyy", "MM/dd/yyyy", "M/dd/yyyy", "MM/d/yyyy",
            "yyyy/M/d", "yyyy/MM/dd", "yyyy/M/dd", "yyyy/MM/d",
            "d/M/yy", "dd/MM/yy", "d/MM/yy", "dd/M/yy",
            "M/d/yy", "MM/dd/yy", "M/dd/yy", "MM/d/yy",
            "yy/M/d", "yy/MM/dd", "yy/M/dd", "yy/MM/d",
            "d-M-yyyy", "dd-MM-yyyy", "d-MM-yyyy", "dd-M-yyyy",
            "M-d-yyyy", "MM-dd-yyyy", "M-dd-yyyy", "MM-d-yyyy",
            "yyyy-M-d", "yyyy-MM-dd", "yyyy-M-dd", "yyyy-MM-d",
            "d-M-yy", "dd-MM-yy", "d-MM-yy", "dd-M-yy",
            "M-d-yy", "MM-dd-yy", "M-dd-yy", "MM-d-yy",
            "yy-M-d", "yy-MM-dd", "yy-M-dd", "yy-MM-d",
            "d.M.yyyy", "dd.MM.yyyy", "d.MM.yyyy", "dd.M.yyyy",
            "d.M.yy", "dd.MM.yy", "d.MM.yy", "dd.M.yy",
            "yy.M.d", "yy.MM.dd", "yy.M.dd", "yy.MM.d",
            "yyyy.M.d", "yyyy.MM.dd", "yyyy.M.dd", "yyyy.MM.d"
        };

        private static readonly Regex NumericDateExpression = new Regex(
            @"(?<!\d)\S*?\d{1,8}(?:[/-]\d{1,8}){1,3}\S*?(?!\d)",
            RegexOptions.Compiled);

        private static readonly Regex SlashDateExpression = new Regex(
            @"(?<![\p{L}\d])\d{1,4}/\d{1,2}/\d{1,4}(?![\p{L}\d])",
            RegexOptions.Compiled);

        public static bool IsDateExpression(string value)
        {
            if (String.IsNullOrWhiteSpace(value))
                return false;

            string candidate = value.Trim();
            if (!candidate.Any(Char.IsDigit))
                return false;

            if (ContainsNumericDateExpression(candidate))
                return true;

            DateTime ignored;
            return DateTime.TryParseExact(
                candidate,
                DateFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out ignored);
        }

        public static bool IsSlashSeparatedDateExpression(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.IndexOf('/') < 0)
                return false;

            return ContainsNumericDateExpression(value.Trim());
        }

        public static bool StartsSlashSeparatedDateExpression(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || value.IndexOf('/') < 0)
                return false;

            string candidate = value.Trim();
            int firstSlash = candidate.IndexOf('/');
            foreach (Match match in SlashDateExpression.Matches(candidate))
            {
                // Every slash in this candidate must belong to the date itself.
                // Otherwise a valid date could consume unrelated parent/child folders.
                if (match.Index > firstSlash ||
                    match.Index + match.Length <= candidate.LastIndexOf('/'))
                    continue;

                DateTime ignored;
                if (DateTime.TryParseExact(match.Value, DateFormats,
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out ignored))
                    return true;
            }
            return false;
        }

        private static bool ContainsNumericDateExpression(string value)
        {
            foreach (Match match in NumericDateExpression.Matches(value))
            {
                string expression = match.Value;
                int slashCount = expression.Count(character => character == '/');
                int dashCount = expression.Count(character => character == '-');
                if (slashCount + dashCount >= 1 &&
                    HasNumericSeparatedRun(expression, '/', '-') &&
                    !HasWhitespaceInsideMatch(value, match))
                    return true;
            }
            return false;
        }

        private static bool HasNumericSeparatedRun(string value, params char[] separators)
        {
            string[] parts = value.Split(separators);
            int numericRunLength = 0;
            foreach (string part in parts)
            {
                string digits = new string(part.Where(Char.IsDigit).ToArray());
                if (digits.Length > 0 && digits.Length <= 8)
                    numericRunLength++;
                else
                    numericRunLength = 0;

                if (numericRunLength >= 2)
                    return true;
            }
            return false;
        }

        private static int CountPrefixSlashesBeforeFirstDigit(string value)
        {
            int count = 0;
            foreach (char character in value)
            {
                if (Char.IsDigit(character))
                    break;
                if (character == '/')
                    count++;
            }
            return count;
        }

        private static bool HasWhitespaceInsideMatch(string value, Match match)
        {
            return match.Value.Any(Char.IsWhiteSpace);
        }
    }
}
