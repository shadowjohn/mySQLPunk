using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace mySQLPunk.lib
{
    public enum ConnectionUriError
    {
        None,
        Empty,
        TooLong,
        InvalidCharacters,
        InvalidEscape,
        InvalidFormat,
        UnsupportedScheme,
        MissingHost,
        InvalidPort,
        FragmentNotAllowed,
        InvalidQuery,
        DuplicateParameter,
        UnsupportedParameter,
        ConflictingParameter,
        MissingDatabaseOrPath
    }

    public sealed class ConnectionUriParseResult
    {
        private ConnectionUriParseResult(bool success, ConnectionUriError error, string detail, Dictionary<string, object> connection)
        {
            Success = success;
            Error = error;
            Detail = detail ?? string.Empty;
            Connection = connection;
        }

        public bool Success { get; private set; }
        public ConnectionUriError Error { get; private set; }
        public string Detail { get; private set; }
        public Dictionary<string, object> Connection { get; private set; }

        internal static ConnectionUriParseResult Ok(Dictionary<string, object> connection)
        {
            return new ConnectionUriParseResult(true, ConnectionUriError.None, string.Empty, connection);
        }

        internal static ConnectionUriParseResult Fail(ConnectionUriError error, string detail)
        {
            return new ConnectionUriParseResult(false, error, detail, null);
        }
    }

    public static class ConnectionUriImportService
    {
        private const int MaxUriLength = 4096;

        public static ConnectionUriParseResult Parse(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return Fail(ConnectionUriError.Empty);
            if (value.Length > MaxUriLength) return Fail(ConnectionUriError.TooLong);
            if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
                return Fail(ConnectionUriError.InvalidCharacters);
            if (!HasValidPercentEncoding(value)) return Fail(ConnectionUriError.InvalidEscape);

            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || string.IsNullOrWhiteSpace(uri.Scheme))
                return Fail(ConnectionUriError.InvalidFormat);
            if (!string.IsNullOrEmpty(uri.Fragment)) return Fail(ConnectionUriError.FragmentNotAllowed);

            string provider = NormalizeScheme(uri.Scheme);
            if (string.IsNullOrEmpty(provider)) return Fail(ConnectionUriError.UnsupportedScheme, uri.Scheme);

            Dictionary<string, string> query;
            ConnectionUriParseResult queryError = ParseQuery(uri.Query, out query);
            if (queryError != null) return queryError;

            HashSet<string> allowed = GetAllowedParameters(provider);
            string unsupported = query.Keys.FirstOrDefault(key => !allowed.Contains(key));
            if (unsupported != null) return Fail(ConnectionUriError.UnsupportedParameter, unsupported);

            Dictionary<string, object> connection;
            ConnectionUriParseResult parseError = provider == "sqlite"
                ? ParseSqlite(uri, query, out connection)
                : ParseNetwork(uri, provider, query, out connection);
            if (parseError != null) return parseError;
            if (connection.Values.Any(item => item != null && item.ToString().Any(char.IsControl)))
                return Fail(ConnectionUriError.InvalidCharacters);

            ConnectionSecuritySettingsService.Normalize(connection);
            return ConnectionUriParseResult.Ok(connection);
        }

        private static ConnectionUriParseResult ParseNetwork(
            Uri uri,
            string provider,
            Dictionary<string, string> query,
            out Dictionary<string, object> connection)
        {
            connection = null;
            if (string.IsNullOrWhiteSpace(uri.Host)) return Fail(ConnectionUriError.MissingHost);
            if (uri.Port == 0 || uri.Port > 65535) return Fail(ConnectionUriError.InvalidPort);

            string path = DecodePathSegment(uri.AbsolutePath);
            if (path == null) return Fail(ConnectionUriError.InvalidFormat);

            string username = string.Empty;
            string password = string.Empty;
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                int separator = uri.UserInfo.IndexOf(':');
                username = Decode(separator < 0 ? uri.UserInfo : uri.UserInfo.Substring(0, separator));
                password = separator < 0 ? string.Empty : Decode(uri.UserInfo.Substring(separator + 1));
            }

            string database = path;
            string port = (uri.IsDefaultPort || uri.Port < 0) ? DefaultPort(provider) : uri.Port.ToString();
            connection = new Dictionary<string, object>
            {
                { "conn_name", GetConnectionName(query, uri.Host, database) },
                { "host", uri.Host },
                { "port", port },
                { "initial_database", GetDefaultDatabase(provider, database) },
                { "db_kind", provider },
                { "username", username },
                { "pwd", password },
                { "isConnect", "F" }
            };

            if (provider == "sqlserver")
            {
                ConnectionUriParseResult error = ApplySqlServerOptions(query, connection);
                if (error != null) return error;
            }
            else if (provider == "oracle")
            {
                ConnectionUriParseResult error = ApplyOracleOptions(query, database, connection);
                if (error != null) return error;
            }

            if (provider != "sqlserver")
            {
                string sslMode;
                if (query.TryGetValue("sslmode", out sslMode))
                {
                    string normalized = NormalizeTlsMode(provider, sslMode);
                    if (normalized == null) return Fail(ConnectionUriError.InvalidQuery, "sslmode");
                    connection["tls_mode"] = normalized;
                }
            }

            return null;
        }

        private static ConnectionUriParseResult ParseSqlite(
            Uri uri,
            Dictionary<string, string> query,
            out Dictionary<string, object> connection)
        {
            connection = null;
            if (!string.IsNullOrEmpty(uri.UserInfo) ||
                (!string.IsNullOrEmpty(uri.Host) && !string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)))
                return Fail(ConnectionUriError.InvalidFormat);

            string path = Uri.UnescapeDataString(uri.LocalPath ?? string.Empty);
            if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':') path = path.Substring(1);
            path = path.Replace('/', Path.DirectorySeparatorChar);
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path))
                return Fail(ConnectionUriError.MissingDatabaseOrPath);

            string name;
            if (!query.TryGetValue("name", out name) || string.IsNullOrWhiteSpace(name))
                name = Path.GetFileNameWithoutExtension(path);

            connection = new Dictionary<string, object>
            {
                { "conn_name", name.Trim() },
                { "host", string.Empty },
                { "port", string.Empty },
                { "initial_database", "main" },
                { "db_kind", "sqlite" },
                { "username", string.Empty },
                { "pwd", string.Empty },
                { "path", path },
                { "init_geospatial", "F" },
                { "isConnect", "F" }
            };
            return null;
        }

        private static ConnectionUriParseResult ApplySqlServerOptions(
            Dictionary<string, string> query,
            Dictionary<string, object> connection)
        {
            bool integrated = false;
            string integratedText;
            string trustedText;
            bool hasIntegrated = query.TryGetValue("integratedsecurity", out integratedText);
            bool hasTrusted = query.TryGetValue("trusted_connection", out trustedText);
            if (hasIntegrated && hasTrusted) return Fail(ConnectionUriError.ConflictingParameter, "integratedsecurity");
            if (hasIntegrated && !TryParseBoolean(integratedText, out integrated))
                return Fail(ConnectionUriError.InvalidQuery, "integratedsecurity");
            if (hasTrusted && !TryParseBoolean(trustedText, out integrated))
                return Fail(ConnectionUriError.InvalidQuery, "trusted_connection");
            connection["trusted_connection"] = integrated ? "T" : "F";

            bool hasSslMode = query.ContainsKey("sslmode");
            bool hasEncrypt = query.ContainsKey("encrypt");
            bool hasTrustCertificate = query.ContainsKey("trustservercertificate");
            if (hasSslMode && (hasEncrypt || hasTrustCertificate))
                return Fail(ConnectionUriError.ConflictingParameter, "sslmode");

            if (hasSslMode)
            {
                string normalized = NormalizeTlsMode("sqlserver", query["sslmode"]);
                if (normalized == null) return Fail(ConnectionUriError.InvalidQuery, "sslmode");
                connection["tls_mode"] = normalized;
                return null;
            }

            bool encrypt = false;
            bool trustCertificate = true;
            if (hasEncrypt && !TryParseBoolean(query["encrypt"], out encrypt))
                return Fail(ConnectionUriError.InvalidQuery, "encrypt");
            if (hasTrustCertificate && !TryParseBoolean(query["trustservercertificate"], out trustCertificate))
                return Fail(ConnectionUriError.InvalidQuery, "trustservercertificate");
            if (!encrypt && hasTrustCertificate && !trustCertificate)
                return Fail(ConnectionUriError.ConflictingParameter, "trustservercertificate");
            connection["tls_mode"] = encrypt ? (trustCertificate ? "Required" : "VerifyFull") : "Disabled";
            return null;
        }

        private static ConnectionUriParseResult ApplyOracleOptions(
            Dictionary<string, string> query,
            string pathIdentifier,
            Dictionary<string, object> connection)
        {
            string sid;
            string serviceName;
            bool hasSid = query.TryGetValue("sid", out sid);
            bool hasService = query.TryGetValue("service_name", out serviceName);
            int identifierCount = (string.IsNullOrWhiteSpace(pathIdentifier) ? 0 : 1) + (hasSid ? 1 : 0) + (hasService ? 1 : 0);
            if (identifierCount == 0) return Fail(ConnectionUriError.MissingDatabaseOrPath);
            if (identifierCount > 1) return Fail(ConnectionUriError.ConflictingParameter, "sid/service_name");

            bool useSid = hasSid;
            string identifier = hasSid ? sid : (hasService ? serviceName : pathIdentifier);
            if (string.IsNullOrWhiteSpace(identifier)) return Fail(ConnectionUriError.MissingDatabaseOrPath);
            connection["connection_type"] = "Basic";
            connection["service_name"] = identifier;
            connection["sid"] = identifier;
            connection["oracle_identifier_type"] = useSid ? "sid" : "service_name";
            connection["initial_database"] = identifier;
            connection["tns_name"] = string.Empty;
            return null;
        }

        private static ConnectionUriParseResult ParseQuery(string rawQuery, out Dictionary<string, string> values)
        {
            values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(rawQuery)) return null;

            string query = rawQuery[0] == '?' ? rawQuery.Substring(1) : rawQuery;
            foreach (string part in query.Split('&'))
            {
                if (string.IsNullOrEmpty(part)) return Fail(ConnectionUriError.InvalidQuery);
                int equals = part.IndexOf('=');
                string key = Decode((equals < 0 ? part : part.Substring(0, equals)).Replace("+", " ")).Trim().ToLowerInvariant();
                string value = Decode((equals < 0 ? string.Empty : part.Substring(equals + 1)).Replace("+", " "));
                if (string.IsNullOrWhiteSpace(key)) return Fail(ConnectionUriError.InvalidQuery);
                if (key.Any(char.IsControl) || value.Any(char.IsControl)) return Fail(ConnectionUriError.InvalidCharacters);
                if (values.ContainsKey(key)) return Fail(ConnectionUriError.DuplicateParameter, key);
                values.Add(key, value);
            }
            return null;
        }

        private static HashSet<string> GetAllowedParameters(string provider)
        {
            HashSet<string> values = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" };
            if (provider != "sqlite") values.Add("sslmode");
            if (provider == "sqlserver")
            {
                values.Add("integratedsecurity");
                values.Add("trusted_connection");
                values.Add("encrypt");
                values.Add("trustservercertificate");
            }
            if (provider == "oracle")
            {
                values.Add("sid");
                values.Add("service_name");
            }
            return values;
        }

        private static string NormalizeScheme(string scheme)
        {
            switch ((scheme ?? string.Empty).ToLowerInvariant())
            {
                case "mysql":
                case "mariadb": return "mysql";
                case "postgres":
                case "postgresql": return "postgresql";
                case "mssql":
                case "sqlserver": return "sqlserver";
                case "oracle": return "oracle";
                case "sqlite": return "sqlite";
                default: return string.Empty;
            }
        }

        private static string DefaultPort(string provider)
        {
            switch (provider)
            {
                case "mysql": return "3306";
                case "postgresql": return "5432";
                case "sqlserver": return "1433";
                case "oracle": return "1521";
                default: return string.Empty;
            }
        }

        private static string GetDefaultDatabase(string provider, string database)
        {
            if (!string.IsNullOrWhiteSpace(database)) return database;
            if (provider == "postgresql") return "postgres";
            if (provider == "sqlserver") return "master";
            return string.Empty;
        }

        private static string GetConnectionName(Dictionary<string, string> query, string host, string database)
        {
            string name;
            if (query.TryGetValue("name", out name) && !string.IsNullOrWhiteSpace(name)) return name.Trim();
            return string.IsNullOrWhiteSpace(database) ? host : host + "/" + database;
        }

        private static string DecodePathSegment(string absolutePath)
        {
            string path = (absolutePath ?? string.Empty).Trim('/');
            if (path.IndexOf('/') >= 0) return null;
            return Decode(path);
        }

        private static string NormalizeTlsMode(string provider, string value)
        {
            string mode = (value ?? string.Empty).Trim().Replace("-", string.Empty).Replace("_", string.Empty).ToLowerInvariant();
            if (mode == "disable" || mode == "disabled" || mode == "none") return "Disabled";
            if (provider == "mysql")
            {
                if (mode == "prefer" || mode == "preferred") return "Preferred";
                if (mode == "require" || mode == "required") return "Required";
                if (mode == "verifyca") return "VerifyCA";
                if (mode == "verifyfull") return "VerifyFull";
            }
            else if (provider == "postgresql")
            {
                if (mode == "prefer" || mode == "preferred") return "Prefer";
                if (mode == "require" || mode == "required") return "Require";
                if (mode == "verifyca") return "VerifyCA";
                if (mode == "verifyfull") return "VerifyFull";
            }
            else
            {
                if (mode == "require" || mode == "required" || mode == "prefer" || mode == "preferred") return "Required";
                if (mode == "verifyfull") return "VerifyFull";
            }
            return null;
        }

        private static bool TryParseBoolean(string value, out bool result)
        {
            string normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
            if (normalized == "true" || normalized == "t" || normalized == "1" || normalized == "yes")
            {
                result = true;
                return true;
            }
            if (normalized == "false" || normalized == "f" || normalized == "0" || normalized == "no")
            {
                result = false;
                return true;
            }
            result = false;
            return false;
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
            return (value >= '0' && value <= '9') ||
                   (value >= 'a' && value <= 'f') ||
                   (value >= 'A' && value <= 'F');
        }

        private static string Decode(string value)
        {
            return Uri.UnescapeDataString(value ?? string.Empty);
        }

        private static ConnectionUriParseResult Fail(ConnectionUriError error, string detail = null)
        {
            return ConnectionUriParseResult.Fail(error, detail);
        }
    }
}
