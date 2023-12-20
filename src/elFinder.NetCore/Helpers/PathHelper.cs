using System.IO;

namespace elFinder.NetCore.Helpers
{
    public class PathHelper
    {
        private static readonly char[] SeparatorChars = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        public static string GetFullPath(string path)
        {
            return Path.GetFullPath(path);
        }

        public static string GetFullPathNormalized(string path)
        {
            return Path.GetFullPath(path).TrimEnd(SeparatorChars);
        }

        public static string NormalizePath(string fullPath)
        {
            return fullPath.TrimEnd(SeparatorChars);
        }

        public static string SafelyCombine(params string[] paths)
        {
            return GetFullPath(Path.Combine(paths))
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        }
    }
}