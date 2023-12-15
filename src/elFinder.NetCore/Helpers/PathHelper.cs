using System;
using System.IO;

namespace elFinder.NetCore.Helpers
{
    public class PathHelper
    {
        private static readonly char[] SeparatorChars = new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

        public static string GetFullPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            return fullPath;
        }

        public static string GetFullPathNormalized(string path)
        {
            var fullPath = Path.GetFullPath(path).TrimEnd(SeparatorChars);
            return fullPath;
        }

        public static string NormalizePath(string fullPath)
        {
            return fullPath.TrimEnd(SeparatorChars);
        }

        public static string SafelyCombine(string fromParent, params string[] paths)
        {
            var finalPath = Path.GetFullPath(Path.Combine(paths))
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);

            if (!finalPath.StartsWith(fromParent.TrimEnd(SeparatorChars) + Path.DirectorySeparatorChar))
                throw new ArgumentException("Path must be inside parent");

            return finalPath;
        }
    }
}