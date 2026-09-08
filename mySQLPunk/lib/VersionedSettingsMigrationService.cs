using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace mySQLPunk.lib
{
    internal static class VersionedSettingsMigrationService
    {
        internal const int MaximumPreferenceBytes = 128;

        internal static bool TryMigratePreviousVersion(string currentSettingsPath, string fileName, int maximumBytes, Action<byte[]> validate)
        {
            if (string.IsNullOrWhiteSpace(currentSettingsPath)) throw new ArgumentException("A settings path is required.", nameof(currentSettingsPath));
            string destination = Path.GetFullPath(currentSettingsPath);
            if (!string.Equals(Path.GetFileName(destination), fileName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The settings filename does not match the requested migration.", nameof(currentSettingsPath));
            if (File.Exists(destination) || Directory.Exists(destination)) return false;

            DirectoryInfo currentDirectory = new DirectoryInfo(Path.GetDirectoryName(destination));
            Version currentVersion;
            if (!Version.TryParse(currentDirectory.Name, out currentVersion) || currentDirectory.Parent == null) return false;
            DirectoryInfo productDirectory = currentDirectory.Parent;
            if (!productDirectory.Exists) return false;
            EnsureNoReparsePoints(currentDirectory.FullName);

            List<KeyValuePair<Version, string>> candidates = new List<KeyValuePair<Version, string>>();
            foreach (DirectoryInfo directory in productDirectory.EnumerateDirectories())
            {
                Version version;
                if (!Version.TryParse(directory.Name, out version) || Normalize(version) >= Normalize(currentVersion)) continue;
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                string source = Path.Combine(directory.FullName, fileName);
                if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                candidates.Add(new KeyValuePair<Version, string>(Normalize(version), source));
            }
            if (candidates.Count == 0) return false;

            // 最新舊版若損毀，保留原檔供修復，不悄悄退回更早的偏好。
            string sourcePath = candidates.OrderByDescending(pair => pair.Key)
                .ThenBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase).First().Value;
            byte[] bytes = ReadBytes(sourcePath, maximumBytes);
            validate(bytes);
            EnsureNoReparsePoints(currentDirectory.FullName);
            Directory.CreateDirectory(currentDirectory.FullName);
            string stagingPath = destination + ".migration-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream staging = new FileStream(stagingPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    staging.Write(bytes, 0, bytes.Length);
                    staging.Flush(true);
                }
                EnsureNoReparsePoints(currentDirectory.FullName);
                try
                {
                    // 同目錄 Move 不覆寫；保留另一個實例先完成的設定。
                    File.Move(stagingPath, destination);
                    return true;
                }
                catch (IOException)
                {
                    if (File.Exists(destination) || Directory.Exists(destination)) return false;
                    throw;
                }
            }
            finally
            {
                if (File.Exists(stagingPath)) File.Delete(stagingPath);
            }
        }

        internal static void MigrateTextPreference(string path, string fileName, params string[] allowedValues)
        {
            TryMigratePreviousVersion(path, fileName, MaximumPreferenceBytes, bytes => DecodePreference(bytes, allowedValues));
        }

        internal static string ReadTextPreference(string path, params string[] allowedValues)
        {
            return DecodePreference(ReadBytes(path, MaximumPreferenceBytes), allowedValues);
        }

        private static string DecodePreference(byte[] bytes, string[] allowedValues)
        {
            string value;
            try
            {
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (StreamReader reader = new StreamReader(stream, new UTF8Encoding(false, true), true))
                    value = reader.ReadToEnd().Trim();
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("The preference file has invalid text encoding.");
            }
            if (!allowedValues.Contains(value, StringComparer.Ordinal))
                throw new InvalidDataException("The preference file contains an unsupported value.");
            return value;
        }

        private static byte[] ReadBytes(string path, int maximumBytes)
        {
            EnsureNoReparsePoints(path);
            using (FileStream source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length == 0 || source.Length > maximumBytes)
                    throw new InvalidDataException("The settings file is empty or too large.");
                byte[] bytes = new byte[(int)source.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = source.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new EndOfStreamException("The settings file could not be read completely.");
                    offset += count;
                }
                return bytes;
            }
        }

        private static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        }

        private static void EnsureNoReparsePoints(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Settings cannot be migrated through a reparse point.");
                current = Path.GetDirectoryName(current);
            }
        }
    }
}
