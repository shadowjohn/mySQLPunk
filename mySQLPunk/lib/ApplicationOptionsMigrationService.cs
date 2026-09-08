using System;
using System.Collections.Generic;
using System.IO;
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
            return VersionedSettingsMigrationService.TryMigratePreviousVersion(
                currentSettingsPath, SettingsFileName, MaximumFileBytes, ValidateOptions);
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
