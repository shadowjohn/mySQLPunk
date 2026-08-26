using System;
using System.Collections.Generic;
using System.Text;

namespace mySQLPunk.lib
{
    public sealed class ObjectUriTarget
    {
        public string ConnectionName { get; set; }
        public string DatabaseName { get; set; }
        public string ObjectKind { get; set; }
        public string ObjectName { get; set; }
        public string SecondaryName { get; set; }
    }

    public enum ObjectUriParseError
    {
        None,
        Empty,
        TooLong,
        InvalidFormat,
        InvalidScheme,
        InvalidEndpoint,
        InvalidParameter,
        MissingParameter,
        UnsupportedObjectKind
    }

    public sealed class ObjectUriParseResult
    {
        public ObjectUriTarget Target { get; set; }
        public ObjectUriParseError Error { get; set; }
        public string ParameterName { get; set; }

        public bool Success
        {
            get { return Error == ObjectUriParseError.None && Target != null; }
        }
    }

    /// <summary>
    /// 建立及解析不含帳號、密碼或主機資訊的 mySQLPunk 物件 URI。
    /// 連線只以目前設定檔中的顯示名稱識別，實際連線仍由 mySQLPunk 開啟。
    /// </summary>
    public static class ObjectUriService
    {
        public const string Scheme = "mysqlpunk";
        public const string Endpoint = "object";
        public const int MaxUriLength = 4096;
        public const int MaxValueLength = 512;

        private static readonly Dictionary<string, string> KindToGroup = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "table", "Tables" },
            { "view", "Views" },
            { "function", "Functions" },
            { "user", "Users" },
            { "event", "Events" },
            { "model", "Models" },
            { "bi", "BI" },
            { "tool", "Other" },
            { "report", "Reports" }
        };

        private static readonly Dictionary<string, string> GroupToKind = BuildGroupToKind();

        public static string Build(ObjectUriTarget target)
        {
            ObjectUriParseResult validation = ValidateTarget(target);
            if (!validation.Success)
            {
                throw new ArgumentException(GetValidationMessage(validation), "target");
            }

            ObjectUriTarget normalized = validation.Target;
            StringBuilder uri = new StringBuilder();
            uri.Append(Scheme).Append("://").Append(Endpoint)
                .Append("?connection=").Append(Uri.EscapeDataString(normalized.ConnectionName))
                .Append("&database=").Append(Uri.EscapeDataString(normalized.DatabaseName))
                .Append("&type=").Append(Uri.EscapeDataString(normalized.ObjectKind));
            if (!string.Equals(normalized.ObjectKind, "database", StringComparison.OrdinalIgnoreCase))
            {
                uri.Append("&name=").Append(Uri.EscapeDataString(normalized.ObjectName));
                if (!string.IsNullOrWhiteSpace(normalized.SecondaryName))
                {
                    uri.Append("&secondary=").Append(Uri.EscapeDataString(normalized.SecondaryName));
                }
            }
            string result = uri.ToString();
            if (result.Length > MaxUriLength)
            {
                throw new ArgumentException(Localization.T("ObjectUri.TooLong"), "target");
            }
            return result;
        }

        public static ObjectUriParseResult Parse(string value)
        {
            string input = (value ?? string.Empty).Trim();
            if (input.Length == 0) return Error(ObjectUriParseError.Empty, "URI");
            if (input.Length > MaxUriLength) return Error(ObjectUriParseError.TooLong, "URI");
            // System.Uri 會把像 %ZZ 這類非法 escape 自動改寫成 %25ZZ；必須在解析前
            // 檢查原始字串，否則畸形參數會在正規化後看起來像合法輸入。
            if (!HasValidPercentEncoding(input)) return Error(ObjectUriParseError.InvalidParameter, "URI");

            Uri uri;
            if (!Uri.TryCreate(input, UriKind.Absolute, out uri))
            {
                return Error(ObjectUriParseError.InvalidFormat, "URI");
            }
            if (!string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return Error(ObjectUriParseError.InvalidScheme, "scheme");
            }
            if (!string.Equals(uri.Host, Endpoint, StringComparison.OrdinalIgnoreCase) || uri.Port != -1 ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) ||
                !(string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/"))
            {
                return Error(ObjectUriParseError.InvalidEndpoint, "endpoint");
            }

            string rawQuery = uri.GetComponents(UriComponents.Query, UriFormat.UriEscaped);
            if (string.IsNullOrWhiteSpace(rawQuery)) return Error(ObjectUriParseError.MissingParameter, "connection");
            if (!HasValidPercentEncoding(rawQuery)) return Error(ObjectUriParseError.InvalidParameter, "query");

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] pairs = rawQuery.Split('&');
            foreach (string pair in pairs)
            {
                if (pair.Length == 0) return Error(ObjectUriParseError.InvalidParameter, "query");
                int equalsIndex = pair.IndexOf('=');
                if (equalsIndex <= 0) return Error(ObjectUriParseError.InvalidParameter, "query");

                string key;
                string decoded;
                try
                {
                    key = Uri.UnescapeDataString(pair.Substring(0, equalsIndex));
                    decoded = Uri.UnescapeDataString(pair.Substring(equalsIndex + 1));
                }
                catch
                {
                    return Error(ObjectUriParseError.InvalidParameter, "query");
                }

                if (!IsKnownParameter(key)) return Error(ObjectUriParseError.InvalidParameter, key);
                if (values.ContainsKey(key)) return Error(ObjectUriParseError.InvalidParameter, key);
                values[key] = decoded;
            }

            string connection;
            string database;
            string kind;
            if (!values.TryGetValue("connection", out connection)) return Error(ObjectUriParseError.MissingParameter, "connection");
            if (!values.TryGetValue("database", out database)) return Error(ObjectUriParseError.MissingParameter, "database");
            if (!values.TryGetValue("type", out kind)) return Error(ObjectUriParseError.MissingParameter, "type");

            string name;
            string secondary;
            values.TryGetValue("name", out name);
            values.TryGetValue("secondary", out secondary);
            return ValidateTarget(new ObjectUriTarget
            {
                ConnectionName = connection,
                DatabaseName = database,
                ObjectKind = kind,
                ObjectName = name,
                SecondaryName = secondary
            });
        }

        public static string GetGroupKey(string objectKind)
        {
            if (string.IsNullOrWhiteSpace(objectKind)) return string.Empty;
            string group;
            return KindToGroup.TryGetValue(objectKind.Trim(), out group) ? group : string.Empty;
        }

        public static bool TryGetObjectKind(string groupKey, out string objectKind)
        {
            objectKind = string.Empty;
            if (string.IsNullOrWhiteSpace(groupKey)) return false;
            return GroupToKind.TryGetValue(groupKey.Trim(), out objectKind);
        }

        public static string GetValidationMessage(ObjectUriParseResult result)
        {
            if (result == null) return Localization.T("ObjectUri.InvalidFormat");
            switch (result.Error)
            {
                case ObjectUriParseError.InvalidScheme:
                    return Localization.T("ObjectUri.InvalidScheme");
                case ObjectUriParseError.InvalidEndpoint:
                    return Localization.T("ObjectUri.InvalidEndpoint");
                case ObjectUriParseError.TooLong:
                    return Localization.T("ObjectUri.TooLong");
                case ObjectUriParseError.MissingParameter:
                    return Localization.Format("ObjectUri.MissingParameter", result.ParameterName ?? string.Empty);
                case ObjectUriParseError.InvalidParameter:
                    return Localization.Format("ObjectUri.InvalidParameter", result.ParameterName ?? string.Empty);
                case ObjectUriParseError.UnsupportedObjectKind:
                    return Localization.Format("ObjectUri.UnsupportedKind", result.ParameterName ?? string.Empty);
                case ObjectUriParseError.None:
                    return string.Empty;
                default:
                    return Localization.T("ObjectUri.InvalidFormat");
            }
        }

        private static ObjectUriParseResult ValidateTarget(ObjectUriTarget target)
        {
            if (target == null) return Error(ObjectUriParseError.InvalidParameter, "target");

            string connection = NormalizeValue(target.ConnectionName);
            string database = NormalizeValue(target.DatabaseName);
            string kind = NormalizeValue(target.ObjectKind).ToLowerInvariant();
            string name = NormalizeValue(target.ObjectName);
            string secondary = NormalizeValue(target.SecondaryName);

            if (!IsValidValue(connection, true)) return ErrorForValue(connection, "connection");
            if (!IsValidValue(database, true)) return ErrorForValue(database, "database");
            if (!IsValidValue(kind, true)) return ErrorForValue(kind, "type");
            if (!string.Equals(kind, "database", StringComparison.OrdinalIgnoreCase) && !KindToGroup.ContainsKey(kind))
            {
                return Error(ObjectUriParseError.UnsupportedObjectKind, kind);
            }

            if (string.Equals(kind, "database", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(name)) return Error(ObjectUriParseError.InvalidParameter, "name");
                if (!string.IsNullOrEmpty(secondary)) return Error(ObjectUriParseError.InvalidParameter, "secondary");
            }
            else
            {
                if (!IsValidValue(name, true)) return ErrorForValue(name, "name");
                if (!IsValidValue(secondary, false)) return ErrorForValue(secondary, "secondary");
                if (!string.Equals(kind, "user", StringComparison.OrdinalIgnoreCase) && secondary.Length > 0)
                {
                    return Error(ObjectUriParseError.InvalidParameter, "secondary");
                }
            }

            return new ObjectUriParseResult
            {
                Target = new ObjectUriTarget
                {
                    ConnectionName = connection,
                    DatabaseName = database,
                    ObjectKind = kind,
                    ObjectName = name,
                    SecondaryName = secondary
                },
                Error = ObjectUriParseError.None
            };
        }

        private static ObjectUriParseResult ErrorForValue(string value, string parameterName)
        {
            return string.IsNullOrWhiteSpace(value)
                ? Error(ObjectUriParseError.MissingParameter, parameterName)
                : Error(ObjectUriParseError.InvalidParameter, parameterName);
        }

        private static bool IsValidValue(string value, bool required)
        {
            if (string.IsNullOrEmpty(value)) return !required;
            if (value.Length > MaxValueLength) return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (char.IsControl(value[i])) return false;
            }
            return true;
        }

        private static string NormalizeValue(string value)
        {
            return (value ?? string.Empty).Trim();
        }

        private static bool IsKnownParameter(string key)
        {
            return string.Equals(key, "connection", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "database", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "type", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(key, "secondary", StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasValidPercentEncoding(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] != '%') continue;
                if (i + 2 >= value.Length || !IsHex(value[i + 1]) || !IsHex(value[i + 2])) return false;
                i += 2;
            }
            return true;
        }

        private static bool IsHex(char value)
        {
            return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F');
        }

        private static ObjectUriParseResult Error(ObjectUriParseError error, string parameterName)
        {
            return new ObjectUriParseResult { Error = error, ParameterName = parameterName ?? string.Empty };
        }

        private static Dictionary<string, string> BuildGroupToKind()
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> pair in KindToGroup)
            {
                result[pair.Value] = pair.Key;
            }
            return result;
        }
    }
}
