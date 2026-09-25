using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace mySQLPunk.lib
{
    /// <summary>原生工具需要的連線資訊；密碼只透過環境變數或暫存設定檔交給工具，不出現在命令列。</summary>
    public sealed class NativeBackupEndpoint
    {
        public string Provider { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string User { get; set; }
        public string Password { get; set; }
        public string Database { get; set; }
        public bool UseTls { get; set; }
        public string AuthDatabase { get; set; }
        /// <summary>PostgreSQL 的 PGSSLMODE（disable／prefer／require／verify-ca／verify-full）；空白時依 UseTls。</summary>
        public string PgSslMode { get; set; }
    }

    public sealed class NativeBackupResult
    {
        public bool Succeeded { get; set; }
        public string Message { get; set; }
        public string Log { get; set; }
        public long Bytes { get; set; }
    }

    /// <summary>
    /// 原生備份／還原：SQL Server 以 BACKUP／RESTORE 在伺服器端執行（路徑位於資料庫伺服器），PostgreSQL 呼叫
    /// pg_dump／pg_restore（custom 格式），MongoDB 呼叫 mongodump／mongorestore（gzip archive）。
    /// 還原一律寫入新的資料庫名稱，已存在時拒絕，不覆蓋既有資料。
    /// </summary>
    public static class NativeBackupService
    {
        private static readonly Regex SafeDatabaseName = new Regex(@"^[A-Za-z0-9_][A-Za-z0-9_\-$]{0,62}$");

        public static bool Supports(string providerName)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(providerName);
            return provider == "mssql" || provider == "postgresql" || provider == "mongodb";
        }

        /// <summary>把連線設定的 TLS 模式（Npgsql 名稱）換成 libpq 的 PGSSLMODE。</summary>
        public static string ToPgSslMode(string tlsMode)
        {
            switch ((tlsMode ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "disable": case "disabled": return "disable";
                case "allow": return "allow";
                case "require": case "required": return "require";
                case "verifyca": case "verify-ca": return "verify-ca";
                case "verifyfull": case "verify-full": return "verify-full";
                default: return "prefer";
            }
        }

        public static void ValidateNewDatabaseName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !SafeDatabaseName.IsMatch(name)) throw new InvalidOperationException(Localization.T("NativeBackup.Error.DatabaseName"));
        }

        // ------------------------------------------------------------ SQL Server

        public static string BuildSqlServerBackupSql(string database, string serverPath, bool compression)
        {
            ValidateServerPath(serverPath);
            return "BACKUP DATABASE " + BracketQuote(database) + " TO DISK = N'" + serverPath.Replace("'", "''") + "' WITH COPY_ONLY, CHECKSUM, INIT, FORMAT" +
                   (compression ? ", COMPRESSION" : string.Empty) + ", STATS = 10";
        }

        public static string BuildSqlServerVerifySql(string serverPath)
        {
            ValidateServerPath(serverPath);
            return "RESTORE VERIFYONLY FROM DISK = N'" + serverPath.Replace("'", "''") + "' WITH CHECKSUM";
        }

        /// <summary>依 FILELISTONLY 的邏輯檔名把檔案搬到預設資料／記錄目錄，檔名加上新資料庫名稱避免衝突。</summary>
        public static string BuildSqlServerRestoreSql(string newDatabase, string serverPath, DataTable fileList, string dataDirectory, string logDirectory)
        {
            ValidateNewDatabaseName(newDatabase);
            ValidateServerPath(serverPath);
            if (fileList == null || fileList.Rows.Count == 0) throw new InvalidOperationException(Localization.T("NativeBackup.Error.EmptyFileList"));
            List<string> moves = new List<string>();
            int index = 0;
            foreach (DataRow row in fileList.Rows)
            {
                string logical = Convert.ToString(row["LogicalName"], CultureInfo.InvariantCulture);
                string type = Convert.ToString(row["Type"], CultureInfo.InvariantCulture);
                string physical = Convert.ToString(row["PhysicalName"], CultureInfo.InvariantCulture);
                string extension = GetExtension(physical, type == "L" ? ".ldf" : index == 0 ? ".mdf" : ".ndf");
                string directory = type == "L" ? logDirectory : dataDirectory;
                string target = JoinServerPath(directory, newDatabase + (index == 0 || type == "L" ? string.Empty : "_" + index.ToString(CultureInfo.InvariantCulture)) + (type == "L" ? "_log" : string.Empty) + extension);
                moves.Add("MOVE N'" + logical.Replace("'", "''") + "' TO N'" + target.Replace("'", "''") + "'");
                index++;
            }
            return "RESTORE DATABASE " + BracketQuote(newDatabase) + " FROM DISK = N'" + serverPath.Replace("'", "''") + "' WITH " + string.Join(", ", moves) + ", CHECKSUM, RECOVERY, STATS = 10";
        }

        public static NativeBackupResult BackupSqlServer(IDatabase database, string databaseName, string serverPath, bool compression, bool verify)
        {
            StringBuilder log = new StringBuilder();
            try
            {
                ExecLong(database, BuildSqlServerBackupSql(databaseName, serverPath, compression), log);
                if (verify) ExecLong(database, BuildSqlServerVerifySql(serverPath), log);
                return new NativeBackupResult { Succeeded = true, Message = Localization.Format(verify ? "NativeBackup.BackedUpVerified" : "NativeBackup.BackedUp", serverPath), Log = log.ToString() };
            }
            catch (Exception exception)
            {
                return Failure(exception, log);
            }
        }

        public static NativeBackupResult RestoreSqlServer(IDatabase database, string serverPath, string newDatabase)
        {
            StringBuilder log = new StringBuilder();
            try
            {
                ValidateNewDatabaseName(newDatabase);
                DataTable exists = database.SelectSQL("SELECT DB_ID(@name) AS id", new Dictionary<string, object> { { "name", newDatabase } });
                DataSyncService.ThrowIfQueryFailed(exists);
                if (exists.Rows.Count > 0 && !(exists.Rows[0][0] is DBNull)) throw new InvalidOperationException(Localization.Format("NativeBackup.Error.DatabaseExists", newDatabase));
                DataTable files = database.SelectSQL("RESTORE FILELISTONLY FROM DISK = N'" + serverPath.Replace("'", "''") + "'");
                DataSyncService.ThrowIfQueryFailed(files);
                DataTable paths = database.SelectSQL("SELECT CAST(SERVERPROPERTY('InstanceDefaultDataPath') AS nvarchar(4000)) AS data_path, CAST(SERVERPROPERTY('InstanceDefaultLogPath') AS nvarchar(4000)) AS log_path");
                DataSyncService.ThrowIfQueryFailed(paths);
                string dataPath = Convert.ToString(paths.Rows[0]["data_path"], CultureInfo.InvariantCulture);
                string logPath = Convert.ToString(paths.Rows[0]["log_path"], CultureInfo.InvariantCulture);
                if (string.IsNullOrWhiteSpace(dataPath) || string.IsNullOrWhiteSpace(logPath)) throw new InvalidOperationException(Localization.T("NativeBackup.Error.DefaultPaths"));
                ExecLong(database, BuildSqlServerRestoreSql(newDatabase, serverPath, files, dataPath, logPath), log);
                return new NativeBackupResult { Succeeded = true, Message = Localization.Format("NativeBackup.Restored", newDatabase), Log = log.ToString() };
            }
            catch (Exception exception)
            {
                return Failure(exception, log);
            }
        }

        // ------------------------------------------------------------ PostgreSQL

        public static List<string> BuildPgDumpArguments(NativeBackupEndpoint endpoint, string outputFile)
        {
            RequireEndpoint(endpoint);
            return new List<string> { "--host", endpoint.Host, "--port", endpoint.Port.ToString(CultureInfo.InvariantCulture), "--username", endpoint.User ?? string.Empty,
                "--no-password", "--format=custom", "--file", outputFile, "--dbname", endpoint.Database };
        }

        public static List<string> BuildPgRestoreArguments(NativeBackupEndpoint endpoint, string newDatabase, string inputFile)
        {
            RequireEndpoint(endpoint);
            ValidateNewDatabaseName(newDatabase);
            return new List<string> { "--host", endpoint.Host, "--port", endpoint.Port.ToString(CultureInfo.InvariantCulture), "--username", endpoint.User ?? string.Empty,
                "--no-password", "--no-owner", "--exit-on-error", "--dbname", newDatabase, inputFile };
        }

        public static NativeBackupResult BackupPostgreSql(string pgDump, NativeBackupEndpoint endpoint, string outputFile)
        {
            return RunTool(pgDump, BuildPgDumpArguments(endpoint, outputFile), PgEnvironment(endpoint), null, outputFile,
                Localization.Format("NativeBackup.BackedUp", outputFile));
        }

        /// <summary>先用一般連線建立空的新資料庫，再以 pg_restore 還原；失敗時刪除這個新資料庫。</summary>
        public static NativeBackupResult RestorePostgreSql(string pgRestore, IDatabase database, NativeBackupEndpoint endpoint, string newDatabase, string inputFile)
        {
            StringBuilder log = new StringBuilder();
            try
            {
                ValidateNewDatabaseName(newDatabase);
                if (!File.Exists(inputFile)) throw new FileNotFoundException(Localization.Format("NativeBackup.Error.FileMissing", inputFile), inputFile);
                DataTable exists = database.SelectSQL("SELECT 1 FROM pg_database WHERE datname = :name", new Dictionary<string, object> { { "name", newDatabase } });
                DataSyncService.ThrowIfQueryFailed(exists);
                if (exists.Rows.Count > 0) throw new InvalidOperationException(Localization.Format("NativeBackup.Error.DatabaseExists", newDatabase));
                Exec(database, "CREATE DATABASE \"" + newDatabase.Replace("\"", "\"\"") + "\"", log);
            }
            catch (Exception exception)
            {
                return Failure(exception, log);
            }

            NativeBackupResult result = RunTool(pgRestore, BuildPgRestoreArguments(endpoint, newDatabase, inputFile), PgEnvironment(endpoint), null, null,
                Localization.Format("NativeBackup.Restored", newDatabase));
            if (!result.Succeeded)
            {
                Dictionary<string, string> dropped = database.ExecSQL("DROP DATABASE IF EXISTS \"" + newDatabase.Replace("\"", "\"\"") + "\"");
                string status;
                result.Log += Environment.NewLine + (dropped != null && dropped.TryGetValue("status", out status) && status == "OK"
                    ? Localization.Format("NativeBackup.Cleanup", newDatabase)
                    : Localization.Format("NativeBackup.CleanupFailed", newDatabase));
            }
            return result;
        }

        private static Dictionary<string, string> PgEnvironment(NativeBackupEndpoint endpoint)
        {
            return new Dictionary<string, string>
            {
                { "PGPASSWORD", endpoint.Password ?? string.Empty },
                { "PGSSLMODE", string.IsNullOrWhiteSpace(endpoint.PgSslMode) ? (endpoint.UseTls ? "require" : "prefer") : endpoint.PgSslMode },
                { "PGCONNECT_TIMEOUT", "15" }
            };
        }

        // ------------------------------------------------------------ MongoDB

        /// <summary>mongodump／mongorestore 的連線 URI 與密碼放進暫存 YAML（--config），避免出現在命令列。</summary>
        public static string BuildMongoConfig(NativeBackupEndpoint endpoint)
        {
            RequireEndpoint(endpoint);
            StringBuilder uri = new StringBuilder("mongodb://");
            if (!string.IsNullOrEmpty(endpoint.User))
            {
                uri.Append(Uri.EscapeDataString(endpoint.User)).Append(':').Append(Uri.EscapeDataString(endpoint.Password ?? string.Empty)).Append('@');
            }
            uri.Append(endpoint.Host.Contains(":") && !endpoint.Host.StartsWith("[", StringComparison.Ordinal) ? "[" + endpoint.Host + "]" : endpoint.Host)
               .Append(':').Append(endpoint.Port.ToString(CultureInfo.InvariantCulture)).Append("/?directConnection=true");
            if (!string.IsNullOrEmpty(endpoint.User)) uri.Append("&authSource=").Append(Uri.EscapeDataString(string.IsNullOrWhiteSpace(endpoint.AuthDatabase) ? "admin" : endpoint.AuthDatabase));
            if (endpoint.UseTls) uri.Append("&tls=true");
            return "uri: \"" + uri.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"\n";
        }

        public static List<string> BuildMongoDumpArguments(NativeBackupEndpoint endpoint, string configFile, string outputFile)
        {
            RequireEndpoint(endpoint);
            return new List<string> { "--config=" + configFile, "--db=" + endpoint.Database, "--archive=" + outputFile, "--gzip" };
        }

        public static List<string> BuildMongoRestoreArguments(NativeBackupEndpoint endpoint, string configFile, string newDatabase, string inputFile)
        {
            RequireEndpoint(endpoint);
            ValidateNewDatabaseName(newDatabase);
            return new List<string> { "--config=" + configFile, "--archive=" + inputFile, "--gzip", "--nsFrom=" + endpoint.Database + ".*", "--nsTo=" + newDatabase + ".*" };
        }

        public static NativeBackupResult BackupMongo(string mongodump, NativeBackupEndpoint endpoint, string outputFile)
        {
            return WithMongoConfig(endpoint, config => RunTool(mongodump, BuildMongoDumpArguments(endpoint, config, outputFile), null, endpoint.Password, outputFile,
                Localization.Format("NativeBackup.BackedUp", outputFile)));
        }

        /// <summary>還原到新的資料庫名稱；目標已有任何 collection 時拒絕，避免與既有資料混在一起。</summary>
        public static NativeBackupResult RestoreMongo(string mongorestore, IDatabase database, NativeBackupEndpoint endpoint, string newDatabase, string inputFile)
        {
            try
            {
                ValidateNewDatabaseName(newDatabase);
                if (!File.Exists(inputFile)) throw new FileNotFoundException(Localization.Format("NativeBackup.Error.FileMissing", inputFile), inputFile);
                if (database != null && database.GetTables(newDatabase).Count > 0) throw new InvalidOperationException(Localization.Format("NativeBackup.Error.DatabaseExists", newDatabase));
            }
            catch (Exception exception)
            {
                return Failure(exception, new StringBuilder());
            }
            return WithMongoConfig(endpoint, config => RunTool(mongorestore, BuildMongoRestoreArguments(endpoint, config, newDatabase, inputFile), null, endpoint.Password, null,
                Localization.Format("NativeBackup.Restored", newDatabase)));
        }

        private static NativeBackupResult WithMongoConfig(NativeBackupEndpoint endpoint, Func<string, NativeBackupResult> action)
        {
            string config = Path.Combine(Path.GetTempPath(), "mysqlpunk-mongo-" + Guid.NewGuid().ToString("N") + ".yaml");
            try
            {
                File.WriteAllText(config, BuildMongoConfig(endpoint), new UTF8Encoding(false));
                RestrictToCurrentUser(config);
                return action(config);
            }
            finally
            {
                try { File.Delete(config); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        // ------------------------------------------------------------ Tools

        /// <summary>找工具：先看使用者指定的路徑，再找 PATH 與常見安裝目錄；找不到回傳 null。</summary>
        public static string FindTool(string name, string preferred)
        {
            if (!string.IsNullOrWhiteSpace(preferred)) return File.Exists(preferred) ? Path.GetFullPath(preferred) : null;
            bool windows = Path.DirectorySeparatorChar == '\\';
            string file = windows ? name + ".exe" : name;
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
            {
                try
                {
                    string candidate = Path.Combine(directory.Trim('"'), file);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException)
                {
                }
            }
            if (!windows) return null;
            List<string> roots = new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
            };
            foreach (string root in roots.Where(item => !string.IsNullOrEmpty(item)))
            {
                foreach (string pattern in new[] { Path.Combine(root, "PostgreSQL"), Path.Combine(root, "MongoDB", "Tools"), Path.Combine(root, "MongoDB", "Server") })
                {
                    if (!Directory.Exists(pattern)) continue;
                    string found = Directory.GetDirectories(pattern).OrderByDescending(item => item, StringComparer.OrdinalIgnoreCase)
                        .Select(version => Path.Combine(version, "bin", file)).FirstOrDefault(File.Exists);
                    if (found != null) return found;
                }
            }
            return null;
        }

        /// <summary>執行工具並收集輸出；記錄中的密碼一律遮蔽。</summary>
        public static NativeBackupResult RunTool(string tool, IList<string> arguments, IDictionary<string, string> environment, string secret, string outputFile, string successMessage)
        {
            StringBuilder log = new StringBuilder();
            if (string.IsNullOrWhiteSpace(tool) || !File.Exists(tool))
            {
                return new NativeBackupResult { Succeeded = false, Message = Localization.T("NativeBackup.Error.ToolMissing"), Log = string.Empty };
            }
            ProcessStartInfo start = new ProcessStartInfo(tool)
            {
                Arguments = string.Join(" ", arguments.Select(QuoteArgument)),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            if (environment != null) foreach (KeyValuePair<string, string> pair in environment) start.EnvironmentVariables[pair.Key] = pair.Value;
            log.AppendLine("> " + Path.GetFileName(tool) + " " + start.Arguments);
            try
            {
                using (Process process = Process.Start(start))
                {
                    process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (log) log.AppendLine(args.Data); };
                    process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (log) log.AppendLine(args.Data); };
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(6 * 60 * 60 * 1000))
                    {
                        try { process.Kill(); } catch (InvalidOperationException) { }
                        throw new TimeoutException(Localization.T("NativeBackup.Error.Timeout"));
                    }
                    process.WaitForExit();
                    string text = Mask(log.ToString(), secret);
                    if (process.ExitCode != 0)
                    {
                        return new NativeBackupResult { Succeeded = false, Message = Localization.Format("NativeBackup.Error.ExitCode", process.ExitCode), Log = text };
                    }
                    long bytes = !string.IsNullOrEmpty(outputFile) && File.Exists(outputFile) ? new FileInfo(outputFile).Length : 0;
                    if (!string.IsNullOrEmpty(outputFile) && bytes == 0)
                    {
                        return new NativeBackupResult { Succeeded = false, Message = Localization.T("NativeBackup.Error.EmptyOutput"), Log = text };
                    }
                    return new NativeBackupResult { Succeeded = true, Message = successMessage, Log = text, Bytes = bytes };
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception || exception is InvalidOperationException || exception is TimeoutException)
            {
                return new NativeBackupResult { Succeeded = false, Message = ExceptionMessageService.GetReason(exception), Log = Mask(log.ToString(), secret) };
            }
        }

        public static string QuoteArgument(string value)
        {
            string text = value ?? string.Empty;
            if (text.Length > 0 && text.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return text;
            StringBuilder quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in text)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1).Append('"');
                }
                else
                {
                    quoted.Append('\\', backslashes).Append(c);
                }
                backslashes = 0;
            }
            quoted.Append('\\', backslashes * 2).Append('"');
            return quoted.ToString();
        }

        private static string Mask(string text, string secret)
        {
            if (string.IsNullOrEmpty(secret) || secret.Length < 3) return text;
            return text.Replace(secret, "****").Replace(Uri.EscapeDataString(secret), "****");
        }

        private static void RestrictToCurrentUser(string path)
        {
            if (Path.DirectorySeparatorChar != '\\') return;
            try
            {
                System.Security.AccessControl.FileSecurity security = new System.Security.AccessControl.FileSecurity();
                security.SetAccessRuleProtection(true, false);
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
                File.SetAccessControl(path, security);
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException || exception is PlatformNotSupportedException || exception is IOException)
            {
                // 暫存檔位於使用者自己的暫存目錄，無法收緊 ACL 時仍在執行後立即刪除。
            }
        }

        /// <summary>BACKUP／RESTORE 可能跑很久：SQL Server 連線上不設逾時，並把 STATS 進度訊息寫進記錄。</summary>
        private static void ExecLong(IDatabase database, string sql, StringBuilder log)
        {
            my_mssql sqlServer = database as my_mssql;
            if (sqlServer == null)
            {
                Exec(database, sql, log);
                return;
            }
            log.AppendLine("> " + sql);
            if (sqlServer.MCT.State != ConnectionState.Open) sqlServer.MCT.Open();
            System.Data.SqlClient.SqlInfoMessageEventHandler handler = (sender, args) => { lock (log) log.AppendLine(args.Message); };
            sqlServer.MCT.InfoMessage += handler;
            try
            {
                using (System.Data.SqlClient.SqlCommand command = new System.Data.SqlClient.SqlCommand(sql, sqlServer.MCT) { CommandTimeout = 0 })
                {
                    command.ExecuteNonQuery();
                }
            }
            finally
            {
                sqlServer.MCT.InfoMessage -= handler;
            }
        }

        private static void Exec(IDatabase database, string sql, StringBuilder log)
        {
            log.AppendLine("> " + sql);
            Dictionary<string, string> result = database.ExecSQL(sql);
            string status, reason;
            if (result == null || !result.TryGetValue("status", out status) || !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(result != null && result.TryGetValue("reason", out reason) && !string.IsNullOrWhiteSpace(reason) ? reason : Localization.T("NativeBackup.Error.Sql"));
            }
        }

        private static NativeBackupResult Failure(Exception exception, StringBuilder log)
        {
            return new NativeBackupResult { Succeeded = false, Message = ExceptionMessageService.GetReason(exception), Log = log.ToString() };
        }

        private static void RequireEndpoint(NativeBackupEndpoint endpoint)
        {
            if (endpoint == null || string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port <= 0 || string.IsNullOrWhiteSpace(endpoint.Database))
            {
                throw new InvalidOperationException(Localization.T("NativeBackup.Error.Endpoint"));
            }
        }

        private static void ValidateServerPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0) throw new InvalidOperationException(Localization.T("NativeBackup.Error.ServerPath"));
        }

        private static string BracketQuote(string name)
        {
            return "[" + (name ?? string.Empty).Replace("]", "]]") + "]";
        }

        private static string GetExtension(string physical, string fallback)
        {
            int dot = (physical ?? string.Empty).LastIndexOf('.');
            int slash = Math.Max((physical ?? string.Empty).LastIndexOf('\\'), (physical ?? string.Empty).LastIndexOf('/'));
            return dot > slash && dot >= 0 ? physical.Substring(dot) : fallback;
        }

        /// <summary>伺服器可能是 Linux（/var/opt/mssql/data/）或 Windows（C:\…\DATA\）；沿用目錄本身的分隔符。</summary>
        private static string JoinServerPath(string directory, string file)
        {
            string separator = directory.Contains("/") && !directory.Contains("\\") ? "/" : "\\";
            return directory.EndsWith(separator, StringComparison.Ordinal) ? directory + file : directory + separator + file;
        }
    }
}
