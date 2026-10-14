using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace mySQLPunk.lib
{
    public sealed class SqlServerMigrationColumn
    {
        public string Name { get; set; } = string.Empty;
        public string TypeSql { get; set; } = string.Empty;
        public bool Nullable { get; set; }
    }

    public sealed class SqlServerMigrationTable
    {
        public string Name { get; set; } = string.Empty;
        public List<SqlServerMigrationColumn> Columns { get; set; } = new List<SqlServerMigrationColumn>();
        public long RowCount { get; set; }
    }

    public sealed class SqlServerMigrationCatalog
    {
        public string Identity { get; set; } = string.Empty;
        public string DatabaseName { get; set; } = string.Empty;
        public int MajorVersion { get; set; }
        public string Collation { get; set; } = string.Empty;
        public List<string> Blockers { get; set; } = new List<string>();
        public List<SqlServerMigrationTable> Tables { get; set; } = new List<SqlServerMigrationTable>();
        public long ObjectCount { get; set; }
    }

    public sealed class SqlServerMigrationPreview
    {
        public SqlServerMigrationCatalog Source { get; set; } = new SqlServerMigrationCatalog();
        public SqlServerMigrationCatalog Target { get; set; } = new SqlServerMigrationCatalog();
        public int SelectedTargetMajor { get; set; }
        public List<string> Blockers { get; set; } = new List<string>();
        public List<string> Statements { get; set; } = new List<string>();
        public bool CanStart { get { return Blockers.Count == 0; } }
        public string Fingerprint { get; set; } = string.Empty;
    }

    public interface ISqlServerMigrationSession : IDisposable
    {
        SqlServerMigrationCatalog ReadCatalog();
        void Begin(bool source);
        IEnumerable<DataTable> ReadBatches(SqlServerMigrationTable table, int batchSize, CancellationToken cancellationToken);
        void Execute(string sql);
        void Insert(SqlServerMigrationTable table, DataTable rows);
        void Commit();
        void Rollback();
    }

    public sealed class SqlServerMigrationResult
    {
        public bool Succeeded { get; set; }
        public bool CleanupConfirmed { get; set; }
        public long Rows { get; set; }
        public string Report { get; set; } = string.Empty;
    }

    public static class SqlServerCompatibilityMigration
    {
        public static readonly int[] SupportedTargetMajors = { 13, 14, 15, 16, 17 };
        public const int MaximumRows = 100000;

        public static string Quote(string name) { return "[" + name.Replace("]", "]]") + "]"; }

        public static SqlServerMigrationPreview Preview(SqlServerMigrationCatalog source, SqlServerMigrationCatalog target, int selectedTargetMajor)
        {
            var preview = new SqlServerMigrationPreview { Source = source, Target = target, SelectedTargetMajor = selectedTargetMajor };
            preview.Blockers.AddRange(source.Blockers);
            preview.Blockers.AddRange(target.Blockers);
            if (!SupportedTargetMajors.Contains(target.MajorVersion) || !SupportedTargetMajors.Contains(source.MajorVersion))
                preview.Blockers.Add("僅規劃支援 SQL Server 2016–2025；未知或更早引擎禁止執行。");
            if (target.MajorVersion != selectedTargetMajor) preview.Blockers.Add("選取的最低目標版本與實際引擎版本不符。");
            if (source.MajorVersion <= target.MajorVersion) preview.Blockers.Add("此工具只接受新版來源搬至舊版目標。");
            if (string.IsNullOrWhiteSpace(source.Identity) || string.IsNullOrWhiteSpace(target.Identity) || source.Identity == target.Identity)
                preview.Blockers.Add("無法確認來源／目標識別，或兩者為同一資料庫。");
            if (target.ObjectCount != 0 || target.Tables.Count != 0) preview.Blockers.Add("目標必須是使用者另建的空資料庫，不允許覆蓋。");
            if (string.IsNullOrWhiteSpace(source.Collation) || source.Collation != target.Collation)
                preview.Blockers.Add("來源與目標定序必須完全相同，禁止隱性轉換。");
            if (source.Tables.Count == 0) preview.Blockers.Add("來源沒有可搬移資料表。");
            if (source.Tables.Sum(table => table.RowCount) > MaximumRows || source.Tables.Any(table => table.RowCount < 0))
                preview.Blockers.Add("來源列數超過 100,000 或無法確認，禁止執行。");
            foreach (var table in source.Tables)
            {
                if (table.Columns.Count == 0) preview.Blockers.Add("資料表沒有可見欄位。");
                foreach (var column in table.Columns)
                    if (!IsSupportedType(column.TypeSql)) preview.Blockers.Add("不支援欄位型別：" + table.Name + "." + column.Name);
                preview.Statements.Add("CREATE TABLE [dbo]." + Quote(table.Name) + " (" + string.Join(",", table.Columns.Select(column =>
                    Quote(column.Name) + " " + column.TypeSql + (column.Nullable ? " NULL" : " NOT NULL"))) + ");");
            }
            preview.Fingerprint = Hash(source.Identity + "|" + target.Identity + "|" + source.DatabaseName + "|" + target.DatabaseName + "|" + source.MajorVersion + "|" + target.MajorVersion + "|" + source.Collation + "|" +
                string.Join("\n", preview.Statements) + "|" + string.Join(",", source.Tables.Select(table => table.RowCount)) + "|" + string.Join("\n", preview.Blockers));
            return preview;
        }

        public static bool IsSupportedType(string type)
        {
            if (type == null) return false;
            if (!System.Text.RegularExpressions.Regex.IsMatch(type,
                @"\A(?:bigint|int|smallint|tinyint|bit|date|datetime|smalldatetime|money|smallmoney|uniqueidentifier|(?:datetime2|time)\([0-7]\)|(?:decimal|numeric)\((?:[1-9]|[12][0-9]|3[0-8]),(?:[0-9]|[12][0-9]|3[0-8])\)|(?:char|varchar|nchar|nvarchar|binary|varbinary)\([1-9][0-9]{0,3}\))\z")
                ) return false;
            int start = type.IndexOf('(');
            if (start < 0) return true;
            string[] args = type.Substring(start + 1, type.Length - start - 2).Split(',');
            int first = int.Parse(args[0], CultureInfo.InvariantCulture);
            if (args.Length == 2) return int.Parse(args[1], CultureInfo.InvariantCulture) <= first;
            if (type.StartsWith("nchar(") || type.StartsWith("nvarchar(")) return first <= 4000;
            return first <= 8000;
        }

        public static SqlServerMigrationResult Run(SqlServerMigrationPreview approved, string confirmation,
            ISqlServerMigrationSession source, ISqlServerMigrationSession target, CancellationToken cancellationToken)
        {
            var result = new SqlServerMigrationResult();
            bool sourceStarted = false, targetStarted = false;
            bool commitAttempted = false;
            string stage = "確認與預檢";
            try
            {
                if (!approved.CanStart || !string.Equals(confirmation, approved.Target.DatabaseName, StringComparison.Ordinal))
                    throw new InvalidOperationException("必須通過預檢並輸入完整目標資料庫名稱確認。");
                cancellationToken.ThrowIfCancellationRequested();
                sourceStarted = true; source.Begin(true);
                targetStarted = true; target.Begin(false);
                var current = Preview(source.ReadCatalog(), target.ReadCatalog(), approved.SelectedTargetMajor);
                if (!current.CanStart || current.Fingerprint != approved.Fingerprint)
                    throw new InvalidOperationException("連線識別、版本、結構或空目標已改變，請重新預檢。");
                var report = new StringBuilder();
                foreach (var table in current.Source.Tables)
                {
                    stage = "建立與搬移資料表 " + Quote(table.Name);
                    cancellationToken.ThrowIfCancellationRequested();
                    target.Execute(current.Statements[current.Source.Tables.IndexOf(table)]);
                    var sourceHashes = new List<string>();
                    foreach (var batch in source.ReadBatches(table, 500, cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        foreach (DataRow row in batch.Rows)
                        {
                            if (++result.Rows > MaximumRows) throw new InvalidOperationException("超過本工具每次 100,000 列上限，請交由 DBA 規劃。");
                            sourceHashes.Add(RowHash(row));
                        }
                        target.Insert(table, batch);
                    }
                    var targetHashes = new List<string>();
                    stage = "驗證資料表 " + Quote(table.Name);
                    foreach (var batch in target.ReadBatches(table, 500, cancellationToken))
                        foreach (DataRow row in batch.Rows)
                        {
                            if (targetHashes.Count >= sourceHashes.Count) throw new InvalidOperationException("內容驗證出現額外資料列。");
                            targetHashes.Add(RowHash(row));
                        }
                    sourceHashes.Sort(StringComparer.Ordinal); targetHashes.Sort(StringComparer.Ordinal);
                    if (sourceHashes.Count != table.RowCount || !sourceHashes.SequenceEqual(targetHashes)) throw new InvalidOperationException("內容驗證失敗，未提交目標。");
                    report.AppendLine(table.Name + ": " + sourceHashes.Count + " 列；逐欄型別／NULL／內容 SHA-256 多重集合相符。");
                }
                var expectedTarget = Preview(current.Source, current.Target, current.SelectedTargetMajor);
                var written = target.ReadCatalog();
                var actualStatements = Preview(written, current.Target, current.SelectedTargetMajor).Statements;
                if (!expectedTarget.Statements.SequenceEqual(actualStatements) || written.Blockers.Count != 0)
                    throw new InvalidOperationException("目標結構驗證失敗，未提交。");
                if (Preview(source.ReadCatalog(), current.Target, current.SelectedTargetMajor).Fingerprint != current.Fingerprint)
                    throw new InvalidOperationException("來源結構在搬移期間改變，未提交。");
                cancellationToken.ThrowIfCancellationRequested();
                source.Rollback(); sourceStarted = false;
                stage = "提交目標交易";
                commitAttempted = true;
                target.Commit(); targetStarted = false;
                result.Succeeded = true; result.CleanupConfirmed = true;
                result.Report = report.ToString();
            }
            catch (Exception exception)
            {
                bool clean = !commitAttempted;
                if (targetStarted) try { target.Rollback(); } catch { clean = false; }
                if (sourceStarted) try { source.Rollback(); } catch { clean = false; }
                result.CleanupConfirmed = clean;
                result.Report = (exception is OperationCanceledException ? "已取消。" : "相容搬移失敗；請重新預檢。") +
                    (clean ? "目標交易已回滾；沒有自動重試。" : "交易結果不確定；禁止重試，請由 DBA 核對目標。") +
                    " 階段：" + stage + "；錯誤類型：" + exception.GetType().Name;
            }
            return result;
        }

        private static string Hash(string value)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(value)));
        }

        private static string RowHash(DataRow row)
        {
            var text = new StringBuilder();
            foreach (object value in row.ItemArray)
            {
                string encoded;
                if (value is DBNull) encoded = "NULL";
                else if (value is byte[]) encoded = "bytes:" + Convert.ToBase64String((byte[])value);
                else if (value is DateTime) encoded = "date:" + ((DateTime)value).Ticks.ToString(CultureInfo.InvariantCulture);
                else encoded = value.GetType().FullName + ":" + (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                if (encoded.Length > 1048576) throw new InvalidOperationException("單一欄位超過 1 MiB 驗證上限。");
                text.Append(encoded.Length).Append(':').Append(encoded);
            }
            return Hash(text.ToString());
        }
    }
}
