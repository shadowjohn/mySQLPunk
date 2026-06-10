using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace mySQLPunk.lib
{
    public sealed class DatabaseRestoreColumnSnapshot
    {
        public string TableName { get; set; }
        public string Name { get; set; }
        public string DataType { get; set; }
        public string IsNullable { get; set; }
        public string DefaultValue { get; set; }
        public string Comment { get; set; }
        public int OrdinalPosition { get; set; }
    }

    public sealed class DatabaseRestoreSnapshot
    {
        public string DatabaseName { get; set; }
        public string ProviderName { get; set; }
        public int TableCount { get; set; }
        public int ViewCount { get; set; }
        public int FunctionCount { get; set; }
        public int EventCount { get; set; }
        public List<string> Tables { get; private set; }
        public List<string> Views { get; private set; }
        public List<string> Functions { get; private set; }
        public List<string> Events { get; private set; }
        public Dictionary<string, long> TableRowCounts { get; private set; }
        public Dictionary<string, List<DatabaseRestoreColumnSnapshot>> TableColumns { get; private set; }
        public Dictionary<string, DatabaseRestoreTableContentSnapshot> TableContentFingerprints { get; private set; }

        public DatabaseRestoreSnapshot()
        {
            Tables = new List<string>();
            Views = new List<string>();
            Functions = new List<string>();
            Events = new List<string>();
            TableRowCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            TableColumns = new Dictionary<string, List<DatabaseRestoreColumnSnapshot>>(StringComparer.OrdinalIgnoreCase);
            TableContentFingerprints = new Dictionary<string, DatabaseRestoreTableContentSnapshot>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public sealed class DatabaseRestoreTableContentSnapshot
    {
        public string TableName { get; set; }
        public long RowCount { get; set; }
        public int SampledRows { get; set; }
        public bool IsPartial { get; set; }
        public string Fingerprint { get; set; }
    }

    public sealed class DatabaseRestoreContentScanReport
    {
        public DateTime GeneratedAtUtc { get; set; }
        public string DatabaseName { get; set; }
        public string ProviderName { get; set; }
        public DatabaseRestoreContentScanSummary Summary { get; set; }
        public List<DatabaseRestoreContentScanTableReport> Tables { get; private set; }

        public DatabaseRestoreContentScanReport()
        {
            Tables = new List<DatabaseRestoreContentScanTableReport>();
            Summary = new DatabaseRestoreContentScanSummary();
        }
    }

    public sealed class DatabaseRestoreContentScanSummary
    {
        public int TotalTables { get; set; }
        public int ChangedTables { get; set; }
        public int UnchangedTables { get; set; }
        public int AddedTables { get; set; }
        public int RemovedTables { get; set; }
        public int MissingSnapshotTables { get; set; }
        public int PartialTables { get; set; }
        public int FullyScannedTables { get; set; }
        public long BeforeRows { get; set; }
        public long AfterRows { get; set; }
        public int BeforeSampledRows { get; set; }
        public int AfterSampledRows { get; set; }
    }

    public sealed class DatabaseRestoreContentScanTableReport
    {
        public string TableName { get; set; }
        public bool ExistsBefore { get; set; }
        public bool ExistsAfter { get; set; }
        public bool HasBeforeSnapshot { get; set; }
        public bool HasAfterSnapshot { get; set; }
        public long BeforeRowCount { get; set; }
        public long AfterRowCount { get; set; }
        public int BeforeSampledRows { get; set; }
        public int AfterSampledRows { get; set; }
        public bool BeforePartial { get; set; }
        public bool AfterPartial { get; set; }
        public bool IsChanged { get; set; }
        public string BeforeFingerprint { get; set; }
        public string AfterFingerprint { get; set; }
        public string BeforeFingerprintShort { get; set; }
        public string AfterFingerprintShort { get; set; }
    }

    public static class BackupRestoreDiffService
    {
        public const int MaxContentSnapshotRows = 10000;
        public const int ContentSnapshotPageSize = 1000;
        public const int MaxConfigurableContentSnapshotRows = 1000000;

        public static int ResolveMaxContentSnapshotRows(int configuredRows)
        {
            if (configuredRows <= 0) return MaxContentSnapshotRows;
            return Math.Min(configuredRows, MaxConfigurableContentSnapshotRows);
        }

        public static DatabaseRestoreSnapshot CreateSnapshot(
            string databaseName,
            string providerName,
            int tableCount,
            int viewCount,
            int functionCount,
            int eventCount)
        {
            return new DatabaseRestoreSnapshot
            {
                DatabaseName = databaseName ?? string.Empty,
                ProviderName = providerName ?? string.Empty,
                TableCount = Math.Max(0, tableCount),
                ViewCount = Math.Max(0, viewCount),
                FunctionCount = Math.Max(0, functionCount),
                EventCount = Math.Max(0, eventCount)
            };
        }

        public static DatabaseRestoreSnapshot CreateSnapshot(
            string databaseName,
            string providerName,
            IEnumerable<string> tables,
            IEnumerable<string> views,
            IEnumerable<string> functions,
            IEnumerable<string> events)
        {
            DatabaseRestoreSnapshot snapshot = new DatabaseRestoreSnapshot
            {
                DatabaseName = databaseName ?? string.Empty,
                ProviderName = providerName ?? string.Empty
            };

            snapshot.Tables.AddRange(NormalizeNames(tables));
            snapshot.Views.AddRange(NormalizeNames(views));
            snapshot.Functions.AddRange(NormalizeNames(functions));
            snapshot.Events.AddRange(NormalizeNames(events));
            snapshot.TableCount = snapshot.Tables.Count;
            snapshot.ViewCount = snapshot.Views.Count;
            snapshot.FunctionCount = snapshot.Functions.Count;
            snapshot.EventCount = snapshot.Events.Count;
            return snapshot;
        }

        public static void AddTableColumns(
            DatabaseRestoreSnapshot snapshot,
            string tableName,
            IEnumerable<DatabaseRestoreColumnSnapshot> columns)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(tableName)) return;

            List<DatabaseRestoreColumnSnapshot> normalized = NormalizeColumns(tableName, columns);
            if (normalized.Count == 0)
            {
                snapshot.TableColumns.Remove(tableName.Trim());
                return;
            }

            snapshot.TableColumns[tableName.Trim()] = normalized;
        }

        public static void SetTableRowCount(DatabaseRestoreSnapshot snapshot, string tableName, long rowCount)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(tableName)) return;
            snapshot.TableRowCounts[tableName.Trim()] = Math.Max(0, rowCount);
        }

        public static void SetTableContentFingerprint(
            DatabaseRestoreSnapshot snapshot,
            string tableName,
            long rowCount,
            DataTable rows)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(tableName) || rows == null) return;

            snapshot.TableContentFingerprints[tableName.Trim()] = new DatabaseRestoreTableContentSnapshot
            {
                TableName = tableName.Trim(),
                RowCount = Math.Max(0, rowCount),
                SampledRows = rows.Rows.Count,
                IsPartial = false,
                Fingerprint = BuildTableContentFingerprint(rows)
            };
        }

        public static void SetTableContentFingerprint(
            DatabaseRestoreSnapshot snapshot,
            string tableName,
            DatabaseRestoreTableContentSnapshot content)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(tableName) || content == null) return;
            content.TableName = string.IsNullOrWhiteSpace(content.TableName) ? tableName.Trim() : content.TableName.Trim();
            snapshot.TableContentFingerprints[tableName.Trim()] = content;
        }

        public static DatabaseRestoreTableContentSnapshot CreateTableContentFingerprint(
            string tableName,
            long rowCount,
            Func<long, int, DataTable> pageLoader,
            int pageSize = ContentSnapshotPageSize,
            int maxRows = MaxContentSnapshotRows)
        {
            if (pageLoader == null) throw new ArgumentNullException(nameof(pageLoader));
            if (rowCount <= 0)
            {
                return new DatabaseRestoreTableContentSnapshot
                {
                    TableName = tableName ?? string.Empty,
                    RowCount = Math.Max(0, rowCount),
                    SampledRows = 0,
                    IsPartial = false,
                    Fingerprint = BuildPagedContentFingerprint(string.Empty, new List<string>(), Math.Max(0, rowCount), 0, false)
                };
            }

            int safePageSize = Math.Max(1, pageSize);
            int safeMaxRows = Math.Max(1, ResolveMaxContentSnapshotRows(maxRows));
            int targetRows = (int)Math.Min(rowCount, safeMaxRows);
            List<string> rowHashes = new List<string>();
            string header = string.Empty;
            long offset = 0;
            int sampled = 0;

            while (sampled < targetRows)
            {
                int take = Math.Min(safePageSize, targetRows - sampled);
                DataTable page = pageLoader(offset, take);
                if (page == null || page.Rows.Count == 0) break;

                if (string.IsNullOrEmpty(header)) header = BuildContentHeader(page.Columns);

                int processedFromPage = 0;
                foreach (DataRow row in page.Rows)
                {
                    rowHashes.Add(HashText(BuildContentRowText(row, page.Columns)));
                    sampled++;
                    processedFromPage++;
                    if (sampled >= targetRows) break;
                }

                offset += processedFromPage;
                if (processedFromPage == 0 || page.Rows.Count < take) break;
            }

            bool isPartial = rowCount > sampled;
            return new DatabaseRestoreTableContentSnapshot
            {
                TableName = tableName ?? string.Empty,
                RowCount = Math.Max(0, rowCount),
                SampledRows = sampled,
                IsPartial = isPartial,
                Fingerprint = BuildPagedContentFingerprint(header, rowHashes, rowCount, sampled, isPartial)
            };
        }

        public static List<DatabaseRestoreColumnSnapshot> CreateColumnSnapshots(string tableName, DataTable columns)
        {
            List<DatabaseRestoreColumnSnapshot> output = new List<DatabaseRestoreColumnSnapshot>();
            if (columns == null) return output;

            foreach (DataRow row in columns.Rows)
            {
                string name = FirstColumnValue(row, "Name", "name", "COLUMN_NAME", "column_name", "Field");
                if (string.IsNullOrWhiteSpace(name)) continue;

                output.Add(new DatabaseRestoreColumnSnapshot
                {
                    TableName = tableName ?? string.Empty,
                    Name = name,
                    DataType = FirstColumnValue(row, "DataType", "Type", "type", "DATA_TYPE", "data_type"),
                    IsNullable = FirstColumnValue(row, "IsNullable", "Nullable", "IS_NULLABLE", "nullable", "NotNull", "not_null"),
                    DefaultValue = FirstColumnValue(row, "Default", "default", "DefaultValue", "default_value", "COLUMN_DEFAULT", "column_default", "dflt_value"),
                    Comment = FirstColumnValue(row, "Comment", "comment", "description", "remarks"),
                    OrdinalPosition = ParseInt(FirstColumnValue(row, "OrdinalPosition", "ORDINAL_POSITION", "ordinal_position", "cid", "Seq"))
                });
            }

            return NormalizeColumns(tableName, output);
        }

        public static string BuildSummary(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            if (before == null || after == null) return string.Empty;

            List<string> lines = new List<string>
            {
                BuildLine(Localization.T("Backup.RestoreDiffTables"), before.TableCount, after.TableCount, before.Tables, after.Tables),
                BuildLine(Localization.T("Backup.RestoreDiffViews"), before.ViewCount, after.ViewCount, before.Views, after.Views),
                BuildLine(Localization.T("Backup.RestoreDiffRoutines"), before.FunctionCount, after.FunctionCount, before.Functions, after.Functions),
                BuildLine(Localization.T("Backup.RestoreDiffEvents"), before.EventCount, after.EventCount, before.Events, after.Events)
            };
            string rowCountDiff = BuildRowCountDiffSummary(before, after);
            if (!string.IsNullOrWhiteSpace(rowCountDiff))
            {
                lines.Add(Localization.Format("Backup.RestoreDiffRowCount", rowCountDiff));
            }

            string columnDiff = BuildColumnDiffSummary(before, after);
            if (!string.IsNullOrWhiteSpace(columnDiff))
            {
                lines.Add(Localization.Format("Backup.RestoreDiffColumn", columnDiff));
            }

            string contentDiff = BuildContentDiffSummary(before, after);
            if (!string.IsNullOrWhiteSpace(contentDiff))
            {
                lines.Add(Localization.Format("Backup.RestoreDiffContent", contentDiff));
            }

            return string.Join(Environment.NewLine, lines);
        }

        public static DatabaseRestoreContentScanReport BuildContentScanReport(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            return BuildContentScanReport(before, after, DateTime.UtcNow);
        }

        public static DatabaseRestoreContentScanReport BuildContentScanReport(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after, DateTime generatedAtUtc)
        {
            DatabaseRestoreContentScanReport report = new DatabaseRestoreContentScanReport
            {
                GeneratedAtUtc = generatedAtUtc,
                DatabaseName = FirstNonEmpty(after == null ? null : after.DatabaseName, before == null ? null : before.DatabaseName),
                ProviderName = FirstNonEmpty(after == null ? null : after.ProviderName, before == null ? null : before.ProviderName)
            };

            List<string> tableNames = new List<string>();
            AddTableNames(tableNames, before);
            AddTableNames(tableNames, after);

            foreach (string tableName in NormalizeNames(tableNames))
            {
                bool existsBefore = ContainsName(before == null ? null : before.Tables, tableName);
                bool existsAfter = ContainsName(after == null ? null : after.Tables, tableName);
                DatabaseRestoreTableContentSnapshot beforeContent = GetContentSnapshot(before, tableName);
                DatabaseRestoreTableContentSnapshot afterContent = GetContentSnapshot(after, tableName);
                bool hasBeforeSnapshot = beforeContent != null;
                bool hasAfterSnapshot = afterContent != null;
                bool comparable = hasBeforeSnapshot && hasAfterSnapshot;
                bool changed = comparable &&
                    !string.Equals(beforeContent.Fingerprint, afterContent.Fingerprint, StringComparison.OrdinalIgnoreCase);

                DatabaseRestoreContentScanTableReport tableReport = new DatabaseRestoreContentScanTableReport
                {
                    TableName = tableName,
                    ExistsBefore = existsBefore,
                    ExistsAfter = existsAfter,
                    HasBeforeSnapshot = hasBeforeSnapshot,
                    HasAfterSnapshot = hasAfterSnapshot,
                    BeforeRowCount = hasBeforeSnapshot ? beforeContent.RowCount : GetRowCount(before, tableName),
                    AfterRowCount = hasAfterSnapshot ? afterContent.RowCount : GetRowCount(after, tableName),
                    BeforeSampledRows = hasBeforeSnapshot ? beforeContent.SampledRows : 0,
                    AfterSampledRows = hasAfterSnapshot ? afterContent.SampledRows : 0,
                    BeforePartial = hasBeforeSnapshot && beforeContent.IsPartial,
                    AfterPartial = hasAfterSnapshot && afterContent.IsPartial,
                    IsChanged = changed,
                    BeforeFingerprint = hasBeforeSnapshot ? beforeContent.Fingerprint : string.Empty,
                    AfterFingerprint = hasAfterSnapshot ? afterContent.Fingerprint : string.Empty,
                    BeforeFingerprintShort = hasBeforeSnapshot ? ShortFingerprint(beforeContent.Fingerprint) : string.Empty,
                    AfterFingerprintShort = hasAfterSnapshot ? ShortFingerprint(afterContent.Fingerprint) : string.Empty
                };

                report.Tables.Add(tableReport);
                ApplyContentScanSummary(report.Summary, tableReport, comparable);
            }

            report.Summary.TotalTables = report.Tables.Count;
            return report;
        }

        public static string WriteContentScanReport(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after, string reportDirectory)
        {
            if (string.IsNullOrWhiteSpace(reportDirectory)) throw new ArgumentException(Localization.T("Backup.RestoreContentScanReportDirectoryRequired"), nameof(reportDirectory));

            DatabaseRestoreContentScanReport report = BuildContentScanReport(before, after);
            Directory.CreateDirectory(reportDirectory);
            string fileName = "restore-content-scan_" + report.GeneratedAtUtc.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture) + ".json";
            string path = Path.Combine(reportDirectory, fileName);
            File.WriteAllText(path, JsonConvert.SerializeObject(report, Formatting.Indented), Encoding.UTF8);
            return path;
        }

        public static string BuildTableContentFingerprint(DataTable rows)
        {
            if (rows == null) return string.Empty;

            string header = BuildContentHeader(rows.Columns);
            List<string> rowHashes = new List<string>();
            foreach (DataRow row in rows.Rows)
            {
                rowHashes.Add(HashText(BuildContentRowText(row, rows.Columns)));
            }

            return BuildPagedContentFingerprint(header, rowHashes, rows.Rows.Count, rows.Rows.Count, false);
        }

        private static void AddTableNames(List<string> tableNames, DatabaseRestoreSnapshot snapshot)
        {
            if (tableNames == null || snapshot == null) return;
            if (snapshot.Tables != null) tableNames.AddRange(snapshot.Tables);
            if (snapshot.TableRowCounts != null) tableNames.AddRange(snapshot.TableRowCounts.Keys);
            if (snapshot.TableColumns != null) tableNames.AddRange(snapshot.TableColumns.Keys);
            if (snapshot.TableContentFingerprints != null) tableNames.AddRange(snapshot.TableContentFingerprints.Keys);
        }

        private static void ApplyContentScanSummary(DatabaseRestoreContentScanSummary summary, DatabaseRestoreContentScanTableReport tableReport, bool comparable)
        {
            if (summary == null || tableReport == null) return;

            if (tableReport.ExistsAfter && !tableReport.ExistsBefore) summary.AddedTables++;
            if (tableReport.ExistsBefore && !tableReport.ExistsAfter) summary.RemovedTables++;
            if (!tableReport.HasBeforeSnapshot || !tableReport.HasAfterSnapshot) summary.MissingSnapshotTables++;
            if (tableReport.BeforePartial || tableReport.AfterPartial) summary.PartialTables++;
            if (tableReport.HasBeforeSnapshot && tableReport.HasAfterSnapshot && !tableReport.BeforePartial && !tableReport.AfterPartial) summary.FullyScannedTables++;
            if (comparable && tableReport.IsChanged) summary.ChangedTables++;
            if (comparable && !tableReport.IsChanged) summary.UnchangedTables++;

            summary.BeforeRows += Math.Max(0, tableReport.BeforeRowCount);
            summary.AfterRows += Math.Max(0, tableReport.AfterRowCount);
            summary.BeforeSampledRows += Math.Max(0, tableReport.BeforeSampledRows);
            summary.AfterSampledRows += Math.Max(0, tableReport.AfterSampledRows);
        }

        private static DatabaseRestoreTableContentSnapshot GetContentSnapshot(DatabaseRestoreSnapshot snapshot, string tableName)
        {
            DatabaseRestoreTableContentSnapshot content;
            if (snapshot == null || snapshot.TableContentFingerprints == null || string.IsNullOrWhiteSpace(tableName)) return null;
            return snapshot.TableContentFingerprints.TryGetValue(tableName, out content) ? content : null;
        }

        private static long GetRowCount(DatabaseRestoreSnapshot snapshot, string tableName)
        {
            long rowCount;
            if (snapshot == null || snapshot.TableRowCounts == null || string.IsNullOrWhiteSpace(tableName)) return 0;
            return snapshot.TableRowCounts.TryGetValue(tableName, out rowCount) ? Math.Max(0, rowCount) : 0;
        }

        private static bool ContainsName(IEnumerable<string> names, string targetName)
        {
            if (names == null || string.IsNullOrWhiteSpace(targetName)) return false;
            return names.Any(name => string.Equals((name ?? string.Empty).Trim(), targetName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        private static string FirstNonEmpty(params string[] values)
        {
            if (values == null) return string.Empty;
            foreach (string value in values)
            {
                if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
            }
            return string.Empty;
        }

        private static string BuildPagedContentFingerprint(string header, List<string> rowHashes, long rowCount, int sampledRows, bool isPartial)
        {
            rowHashes = rowHashes ?? new List<string>();
            rowHashes.Sort(StringComparer.Ordinal);
            StringBuilder payload = new StringBuilder();
            payload.AppendLine(header ?? string.Empty);
            payload.AppendLine("rowCount:" + Math.Max(0, rowCount).ToString(CultureInfo.InvariantCulture));
            payload.AppendLine("sampledRows:" + Math.Max(0, sampledRows).ToString(CultureInfo.InvariantCulture));
            payload.AppendLine("partial:" + (isPartial ? "1" : "0"));
            foreach (string rowHash in rowHashes)
            {
                payload.AppendLine(rowHash);
            }

            return HashText(payload.ToString());
        }

        private static string BuildLine(string label, int before, int after, IEnumerable<string> beforeNames, IEnumerable<string> afterNames)
        {
            int delta = after - before;
            string deltaText = delta == 0 ? "0" : (delta > 0 ? "+" : "") + delta.ToString();
            string line = Localization.Format("Backup.RestoreDiffLine", label, before, after, deltaText);
            string detail = BuildNameDiffDetail(beforeNames, afterNames);
            return string.IsNullOrWhiteSpace(detail) ? line : line + Localization.T("Backup.RestoreDiffLineDetailSeparator") + detail;
        }

        private static string BuildNameDiffDetail(IEnumerable<string> beforeNames, IEnumerable<string> afterNames)
        {
            List<string> beforeList = NormalizeNames(beforeNames);
            List<string> afterList = NormalizeNames(afterNames);
            if (beforeList.Count == 0 && afterList.Count == 0) return string.Empty;

            List<string> added = afterList.Except(beforeList, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> removed = beforeList.Except(afterList, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
            List<string> parts = new List<string>();
            if (added.Count > 0) parts.Add(Localization.Format("Backup.RestoreDiffAdded", FormatNameList(added)));
            if (removed.Count > 0) parts.Add(Localization.Format("Backup.RestoreDiffRemoved", FormatNameList(removed)));
            return string.Join(Localization.T("Backup.RestoreDiffPartSeparator"), parts);
        }

        private static string BuildRowCountDiffSummary(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            if ((before.TableRowCounts == null || before.TableRowCounts.Count == 0) &&
                (after.TableRowCounts == null || after.TableRowCounts.Count == 0))
            {
                return string.Empty;
            }

            List<string> tableNames = new List<string>();
            if (before.TableRowCounts != null) tableNames.AddRange(before.TableRowCounts.Keys);
            if (after.TableRowCounts != null) tableNames.AddRange(after.TableRowCounts.Keys);

            List<string> details = new List<string>();
            foreach (string tableName in NormalizeNames(tableNames))
            {
                bool hasBefore = before.TableRowCounts != null && before.TableRowCounts.ContainsKey(tableName);
                bool hasAfter = after.TableRowCounts != null && after.TableRowCounts.ContainsKey(tableName);
                long beforeCount = hasBefore ? before.TableRowCounts[tableName] : 0;
                long afterCount = hasAfter ? after.TableRowCounts[tableName] : 0;
                if (hasBefore && hasAfter && beforeCount == afterCount) continue;

                details.Add(Localization.Format(
                    "Backup.RestoreDiffTableValueLine",
                    tableName,
                    FormatRowCountValue(hasBefore, beforeCount),
                    FormatRowCountValue(hasAfter, afterCount),
                    FormatRowCountDelta(hasBefore, hasAfter, beforeCount, afterCount)));
            }

            return details.Count == 0 ? string.Empty : FormatNameList(details);
        }

        private static string FormatRowCountValue(bool hasValue, long value)
        {
            return hasValue ? value.ToString() : Localization.T("Backup.RestoreDiffValueMissing");
        }

        private static string FormatRowCountDelta(bool hasBefore, bool hasAfter, long before, long after)
        {
            if (!hasBefore || !hasAfter) return string.Empty;
            long delta = after - before;
            string deltaText = delta == 0 ? "0" : (delta > 0 ? "+" : "") + delta.ToString();
            return " (" + deltaText + ")";
        }

        private static string BuildColumnDiffSummary(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            if ((before.TableColumns == null || before.TableColumns.Count == 0) &&
                (after.TableColumns == null || after.TableColumns.Count == 0))
            {
                return string.Empty;
            }

            List<string> details = new List<string>();
            List<string> tableNames = new List<string>();
            if (before.TableColumns != null) tableNames.AddRange(before.TableColumns.Keys);
            if (after.TableColumns != null) tableNames.AddRange(after.TableColumns.Keys);

            foreach (string tableName in NormalizeNames(tableNames))
            {
                List<DatabaseRestoreColumnSnapshot> beforeColumns = GetColumnsForTable(before, tableName);
                List<DatabaseRestoreColumnSnapshot> afterColumns = GetColumnsForTable(after, tableName);
                Dictionary<string, DatabaseRestoreColumnSnapshot> beforeMap = beforeColumns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);
                Dictionary<string, DatabaseRestoreColumnSnapshot> afterMap = afterColumns.ToDictionary(c => c.Name, c => c, StringComparer.OrdinalIgnoreCase);

                List<string> added = afterMap.Keys.Except(beforeMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                List<string> removed = beforeMap.Keys.Except(afterMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string column in added) details.Add(Localization.Format("Backup.RestoreDiffColumnAdded", tableName, column));
                foreach (string column in removed) details.Add(Localization.Format("Backup.RestoreDiffColumnRemoved", tableName, column));

                List<string> common = beforeMap.Keys.Intersect(afterMap.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
                foreach (string column in common)
                {
                    string change = BuildColumnChangeDetail(beforeMap[column], afterMap[column]);
                    if (!string.IsNullOrWhiteSpace(change)) details.Add(Localization.Format("Backup.RestoreDiffColumnChanged", tableName, column, change));
                }
            }

            if (details.Count == 0) return string.Empty;
            return FormatNameList(details);
        }

        private static string BuildContentDiffSummary(DatabaseRestoreSnapshot before, DatabaseRestoreSnapshot after)
        {
            if ((before.TableContentFingerprints == null || before.TableContentFingerprints.Count == 0) &&
                (after.TableContentFingerprints == null || after.TableContentFingerprints.Count == 0))
            {
                return string.Empty;
            }

            List<string> tableNames = new List<string>();
            if (before.TableContentFingerprints != null) tableNames.AddRange(before.TableContentFingerprints.Keys);
            if (after.TableContentFingerprints != null) tableNames.AddRange(after.TableContentFingerprints.Keys);

            List<string> details = new List<string>();
            foreach (string tableName in NormalizeNames(tableNames))
            {
                DatabaseRestoreTableContentSnapshot beforeContent = null;
                DatabaseRestoreTableContentSnapshot afterContent = null;
                bool hasBefore = before.TableContentFingerprints != null && before.TableContentFingerprints.TryGetValue(tableName, out beforeContent);
                bool hasAfter = after.TableContentFingerprints != null && after.TableContentFingerprints.TryGetValue(tableName, out afterContent);
                if (!hasBefore || !hasAfter) continue;
                if (string.Equals(beforeContent.Fingerprint, afterContent.Fingerprint, StringComparison.OrdinalIgnoreCase)) continue;

                details.Add(Localization.Format(
                    "Backup.RestoreDiffContentFingerprintChanged",
                    tableName,
                    ShortFingerprint(beforeContent.Fingerprint),
                    ShortFingerprint(afterContent.Fingerprint),
                    BuildContentCoverageText(beforeContent, afterContent),
                    beforeContent.RowCount,
                    afterContent.RowCount));
            }

            return details.Count == 0 ? string.Empty : FormatNameList(details);
        }

        private static string BuildContentCoverageText(DatabaseRestoreTableContentSnapshot before, DatabaseRestoreTableContentSnapshot after)
        {
            int sampled = Math.Min(before == null ? 0 : before.SampledRows, after == null ? 0 : after.SampledRows);
            long total = Math.Max(before == null ? 0 : before.RowCount, after == null ? 0 : after.RowCount);
            bool partial = (before != null && before.IsPartial) || (after != null && after.IsPartial);
            string prefix = partial ? Localization.T("Backup.RestoreDiffCoveragePartial") : Localization.T("Backup.RestoreDiffCoverageFull");
            return Localization.Format(
                "Backup.RestoreDiffCoverage",
                prefix,
                sampled.ToString(CultureInfo.InvariantCulture),
                Math.Max(0, total).ToString(CultureInfo.InvariantCulture));
        }

        private static string BuildColumnChangeDetail(DatabaseRestoreColumnSnapshot before, DatabaseRestoreColumnSnapshot after)
        {
            List<string> parts = new List<string>();
            AddChangedPart(parts, Localization.T("Backup.RestoreDiffColumnType"), before.DataType, after.DataType);
            AddChangedPart(parts, "NULL", before.IsNullable, after.IsNullable);
            AddChangedPart(parts, Localization.T("Backup.RestoreDiffColumnDefault"), before.DefaultValue, after.DefaultValue);
            AddChangedPart(parts, Localization.T("Backup.RestoreDiffColumnComment"), before.Comment, after.Comment);
            return string.Join(Localization.T("Backup.RestoreDiffPartSeparator"), parts);
        }

        private static void AddChangedPart(List<string> parts, string label, string before, string after)
        {
            string oldValue = NormalizeText(before);
            string newValue = NormalizeText(after);
            if (string.Equals(oldValue, newValue, StringComparison.OrdinalIgnoreCase)) return;
            parts.Add(Localization.Format("Backup.RestoreDiffColumnChangePart", label, FormatValue(oldValue), FormatValue(newValue)));
        }

        private static List<DatabaseRestoreColumnSnapshot> GetColumnsForTable(DatabaseRestoreSnapshot snapshot, string tableName)
        {
            List<DatabaseRestoreColumnSnapshot> columns;
            if (snapshot == null || snapshot.TableColumns == null || string.IsNullOrWhiteSpace(tableName)) return new List<DatabaseRestoreColumnSnapshot>();
            return snapshot.TableColumns.TryGetValue(tableName, out columns) ? columns : new List<DatabaseRestoreColumnSnapshot>();
        }

        private static List<string> NormalizeNames(IEnumerable<string> names)
        {
            if (names == null) return new List<string>();
            return names
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static List<DatabaseRestoreColumnSnapshot> NormalizeColumns(string tableName, IEnumerable<DatabaseRestoreColumnSnapshot> columns)
        {
            if (columns == null) return new List<DatabaseRestoreColumnSnapshot>();

            return columns
                .Where(column => column != null && !string.IsNullOrWhiteSpace(column.Name))
                .GroupBy(column => column.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group =>
                {
                    DatabaseRestoreColumnSnapshot column = group.OrderBy(c => c.OrdinalPosition <= 0 ? int.MaxValue : c.OrdinalPosition).First();
                    return new DatabaseRestoreColumnSnapshot
                    {
                        TableName = string.IsNullOrWhiteSpace(column.TableName) ? (tableName ?? string.Empty).Trim() : column.TableName.Trim(),
                        Name = column.Name.Trim(),
                        DataType = NormalizeText(column.DataType),
                        IsNullable = NormalizeText(column.IsNullable),
                        DefaultValue = NormalizeText(column.DefaultValue),
                        Comment = NormalizeText(column.Comment),
                        OrdinalPosition = column.OrdinalPosition
                    };
                })
                .OrderBy(column => column.OrdinalPosition <= 0 ? int.MaxValue : column.OrdinalPosition)
                .ThenBy(column => column.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string FirstColumnValue(DataRow row, params string[] names)
        {
            if (row == null || row.Table == null) return string.Empty;
            foreach (string name in names)
            {
                if (row.Table.Columns.Contains(name) && row[name] != DBNull.Value)
                {
                    return Convert.ToString(row[name]);
                }
            }

            return string.Empty;
        }

        private static int ParseInt(string value)
        {
            int output;
            return int.TryParse((value ?? string.Empty).Trim(), out output) ? output : 0;
        }

        private static string NormalizeText(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string FormatValue(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? Localization.T("Backup.RestoreDiffBlankValue") : value;
        }

        private static void AppendLengthPrefixed(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            builder.Append(':');
            builder.Append(value);
            builder.Append('|');
        }

        private static string BuildContentHeader(DataColumnCollection columns)
        {
            StringBuilder header = new StringBuilder();
            header.Append("columns");
            if (columns == null) return header.ToString();
            foreach (DataColumn column in columns)
            {
                AppendLengthPrefixed(header, column.ColumnName);
                AppendLengthPrefixed(header, column.DataType == null ? string.Empty : column.DataType.FullName);
            }
            return header.ToString();
        }

        private static string BuildContentRowText(DataRow row, DataColumnCollection columns)
        {
            StringBuilder rowText = new StringBuilder();
            if (row == null || columns == null) return rowText.ToString();
            foreach (DataColumn column in columns)
            {
                AppendLengthPrefixed(rowText, FormatCellValue(row[column]));
            }
            return rowText.ToString();
        }

        private static string HashText(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
                return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
            }
        }

        private static string FormatCellValue(object value)
        {
            if (value == null || value == DBNull.Value) return "<NULL>";

            byte[] bytes = value as byte[];
            if (bytes != null) return "0x" + BitConverter.ToString(bytes).Replace("-", "");

            if (value is DateTime)
            {
                return ((DateTime)value).ToString("o", CultureInfo.InvariantCulture);
            }

            IFormattable formattable = value as IFormattable;
            if (formattable != null) return formattable.ToString(null, CultureInfo.InvariantCulture);

            return value.ToString();
        }

        private static string ShortFingerprint(string fingerprint)
        {
            if (string.IsNullOrWhiteSpace(fingerprint)) return Localization.T("Backup.RestoreDiffValueMissing");
            string value = fingerprint.Trim();
            return value.Length <= 12 ? value : value.Substring(0, 12);
        }

        private static string FormatNameList(List<string> names)
        {
            const int maxNames = 5;
            if (names.Count <= maxNames) return string.Join(", ", names);
            return Localization.Format("Backup.RestoreDiffNameListOverflow", string.Join(", ", names.Take(maxNames)), names.Count);
        }
    }
}
