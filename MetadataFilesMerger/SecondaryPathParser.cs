using System;
using System.Collections.Generic;

namespace MetadataFilesMerger
{
    internal sealed class SecondaryPathParser
    {
        private const string Prefix = "FiledInFolders:";
        private readonly AppSettings _settings;

        public SecondaryPathParser(AppSettings settings)
        {
            _settings = settings;
        }

        public IEnumerable<string> Parse(string value)
        {
            if (value == null)
                yield break;

            string text = value.Trim();
            if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                int colon = text.IndexOf(':');
                text = colon >= 0 ? text.Substring(colon + 1) : "";
            }

            string currentPath = null;
            foreach (CommaFragment commaFragment in SplitCommaFragments(text))
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
                    currentPath += commaFragment.Separator + fragment;
                }
            }

            if (!String.IsNullOrEmpty(currentPath))
                yield return currentPath;
        }

        private bool IsSecondaryPathStart(string fragment)
        {
            if (IsCabinetPath(fragment))
                return true;

            if (IsFolderStartTemplate(fragment))
                return true;

            return false;
        }

        private bool IsFolderStartTemplate(string value)
        {
            string firstSegment = GetFirstPathSegment(value);
            foreach (string pattern in _settings.FolderStartPatterns)
                if (WildcardMatch(value, pattern) || WildcardMatch(firstSegment, pattern))
                    return true;
            return false;
        }

        private static bool IsCabinetPath(string path)
        {
            return String.Equals(GetFirstPathSegment(path), "Cabinet", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetFirstPathSegment(string path)
        {
            string normalized = NormalizePath(path);
            int slash = normalized.IndexOf('/');
            return slash >= 0 ? normalized.Substring(0, slash).Trim() : normalized;
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
    }
}
