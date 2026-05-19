using System;
using System.IO;

namespace mySQLPunk.lib
{
    public static class BackupRemoteMirrorService
    {
        public static string MirrorBackup(string sourcePath, string destinationDirectory)
        {
            if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("sourcePath");
            if (!File.Exists(sourcePath)) throw new FileNotFoundException("Backup file not found.", sourcePath);
            if (string.IsNullOrWhiteSpace(destinationDirectory)) return string.Empty;

            string sourceFullPath = Path.GetFullPath(sourcePath);
            string destinationFullDirectory = Path.GetFullPath(destinationDirectory);
            string sourceDirectory = Path.GetDirectoryName(sourceFullPath);
            if (string.Equals(sourceDirectory, destinationFullDirectory, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            Directory.CreateDirectory(destinationFullDirectory);
            string targetPath = BuildUniqueMirrorPath(destinationFullDirectory, Path.GetFileName(sourceFullPath));
            File.Copy(sourceFullPath, targetPath, false);
            return targetPath;
        }

        private static string BuildUniqueMirrorPath(string directory, string fileName)
        {
            string candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate)) return candidate;

            string name = Path.GetFileNameWithoutExtension(fileName);
            string extension = Path.GetExtension(fileName);
            int suffix = 2;
            do
            {
                candidate = Path.Combine(directory, name + "_" + suffix + extension);
                suffix++;
            }
            while (File.Exists(candidate));

            return candidate;
        }
    }
}
