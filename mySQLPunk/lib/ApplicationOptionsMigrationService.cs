using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    public static class ApplicationOptionsMigrationService
    {
        public const int MaximumFileBytes = 4 * 1024 * 1024;
        private const string SettingsFileName = "application-options.json";

        /// <summary>
        /// 只在本版設定不存在時複製最近舊版的選項。原檔保留供回復，
        /// 不合併既有設定，也不從較新版或其他產品目錄取資料。
        /// </summary>
        public static bool TryMigratePreviousVersion(string currentSettingsPath)
        {
            if (string.IsNullOrWhiteSpace(currentSettingsPath)) throw new ArgumentException("A settings path is required.", nameof(currentSettingsPath));
            string destination = Path.GetFullPath(currentSettingsPath);
            if (!string.Equals(Path.GetFileName(destination), SettingsFileName, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Only application options can be migrated.", nameof(currentSettingsPath));
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
                string source = Path.Combine(directory.FullName, SettingsFileName);
                if (!File.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) continue;
                candidates.Add(new KeyValuePair<Version, string>(Normalize(version), source));
            }
            if (candidates.Count == 0) return false;

            // 最新舊版若損毀，交由使用者處理；不能悄悄退回更早的偏好。
            string sourcePath = candidates.OrderByDescending(pair => pair.Key)
                .ThenBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase).First().Value;
            EnsureNoReparsePoints(sourcePath);
            byte[] bytes;
            using (FileStream source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (source.Length == 0 || source.Length > MaximumFileBytes)
                    throw new InvalidDataException("The previous application options file is empty or too large.");
                bytes = new byte[(int)source.Length];
                int offset = 0;
                while (offset < bytes.Length)
                {
                    int count = source.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new EndOfStreamException("The previous application options file could not be read completely.");
                    offset += count;
                }
            }
            ValidateOptions(bytes);

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
                    // 同一目錄內的 Move 不覆寫目標，兩個新實例同時啟動也保留先寫入的設定。
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
                    throw new IOException("Application options cannot be migrated through a reparse point.");
                current = Path.GetDirectoryName(current);
            }
        }

        private static void ValidateOptions(byte[] bytes)
        {
            JObject options;
            try
            {
                using (MemoryStream stream = new MemoryStream(bytes, false))
                using (StreamReader text = new StreamReader(stream, new UTF8Encoding(false, true), true))
                using (JsonTextReader reader = new JsonTextReader(text) { MaxDepth = 32, DateParseHandling = DateParseHandling.None })
                {
                    options = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
                    if (reader.Read()) throw new InvalidDataException("The previous application options file contains trailing data.");
                }
            }
            catch (JsonException)
            {
                // 例外訊息不帶 JSON 內容；選項可能包含代理伺服器密碼。
                throw new InvalidDataException("The previous application options file is not valid JSON.");
            }
            catch (DecoderFallbackException)
            {
                throw new InvalidDataException("The previous application options file has invalid text encoding.");
            }

            int sections = 0;
            HashSet<string> sectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (JProperty property in options.Properties())
            {
                if (string.Equals(property.Name, "BoolValues", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "IntValues", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(property.Name, "StringValues", StringComparison.OrdinalIgnoreCase))
                {
                    if (!sectionNames.Add(property.Name)) throw new InvalidDataException("The previous application options file has duplicate settings sections.");
                }
            }
            foreach (string name in new[] { "BoolValues", "IntValues", "StringValues" })
            {
                JToken token;
                if (!options.TryGetValue(name, StringComparison.OrdinalIgnoreCase, out token)) continue;
                JObject values = token as JObject;
                if (values == null) throw new InvalidDataException("The previous application options file has an invalid settings section.");
                sections++;
                foreach (JProperty property in values.Properties())
                {
                    JToken value = property.Value;
                    bool valid = name == "BoolValues" ? value.Type == JTokenType.Boolean
                        : name == "StringValues" ? value.Type == JTokenType.String || value.Type == JTokenType.Null
                        : value.Type == JTokenType.Integer && IsInt32(value);
                    if (!valid) throw new InvalidDataException("The previous application options file has an invalid option value.");
                }
            }
            if (sections == 0) throw new InvalidDataException("The previous application options file contains no settings sections.");
        }

        private static bool IsInt32(JToken value)
        {
            int parsed;
            return int.TryParse(value.ToString(Formatting.None), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out parsed);
        }
    }
}
