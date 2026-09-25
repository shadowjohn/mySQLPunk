using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace mySQLPunk.lib
{
    public enum TransferMode
    {
        /// <summary>在目標建立新表（沿用跨 provider 型別對應）後複製資料與索引。</summary>
        CreateNew,

        /// <summary>附加到既有目標表，欄位依對應寫入。</summary>
        Append,

        /// <summary>先刪除目標表所有資料列再寫入（破壞性）。</summary>
        ReplaceData
    }

    public enum TransferItemStatus
    {
        Pending,
        Running,
        Done,
        Failed
    }

    public sealed class TransferColumnMapping
    {
        public string SourceColumn { get; set; }
        /// <summary>null 或空字串代表不寫入這個欄位。</summary>
        public string TargetColumn { get; set; }
    }

    public sealed class TransferItem
    {
        public TransferItem()
        {
            Columns = new List<TransferColumnMapping>();
            Include = true;
        }

        public string SourceTable { get; set; }
        public string TargetTable { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public TransferMode Mode { get; set; }
        public bool Include { get; set; }
        /// <summary>附加／取代模式的欄位對應；空清單代表依名稱自動對應。</summary>
        public List<TransferColumnMapping> Columns { get; set; }
        [JsonConverter(typeof(StringEnumConverter))]
        public TransferItemStatus Status { get; set; }
        public long CopiedRows { get; set; }
        public long SourceRows { get; set; }
        public long TargetRowsBefore { get; set; }
        public long TargetRowsAfter { get; set; }
        public bool? Verified { get; set; }
        public string Error { get; set; }
        public string Warning { get; set; }
        public double Seconds { get; set; }
    }

    public sealed class TransferPlan
    {
        public TransferPlan()
        {
            Id = Guid.NewGuid().ToString("N");
            Items = new List<TransferItem>();
            BatchSize = 1000;
            CreatedUtc = DateTime.UtcNow;
        }

        public string Id { get; set; }
        public string SourceLabel { get; set; }
        public string SourceProvider { get; set; }
        public string SourceDatabase { get; set; }
        public string TargetLabel { get; set; }
        public string TargetProvider { get; set; }
        public string TargetDatabase { get; set; }
        public int BatchSize { get; set; }
        public bool ContinueOnError { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public List<TransferItem> Items { get; set; }

        [JsonIgnore]
        public bool IsFinished
        {
            get { return Items.Where(item => item.Include).All(item => item.Status == TransferItemStatus.Done); }
        }

        public string RouteKey()
        {
            return (SourceLabel + "\u0001" + SourceDatabase + "\u0001" + TargetLabel + "\u0001" + TargetDatabase).ToUpperInvariant();
        }
    }

    public sealed class TransferProgress
    {
        public string Table { get; set; }
        public long Copied { get; set; }
        public long Total { get; set; }
        public int TableIndex { get; set; }
        public int TableCount { get; set; }
    }

    /// <summary>
    /// 整庫資料傳輸：逐表建立或附加／取代資料，每批寫入後記錄檢查點，中斷或失敗後可從上次位置續傳
    /// （有主鍵的表依主鍵排序分頁，可從已複製列數接續；沒有主鍵的表會從頭重來）。每張表完成後以列數驗證。
    /// </summary>
    public static class DataTransferService
    {
        public static TransferPlan BuildPlan(IDatabase source, string sourceDatabase, string sourceLabel, IDatabase target, string targetDatabase, string targetLabel, IEnumerable<string> tables)
        {
            TransferPlan plan = new TransferPlan
            {
                SourceLabel = sourceLabel,
                SourceProvider = source.ProviderName,
                SourceDatabase = sourceDatabase,
                TargetLabel = targetLabel,
                TargetProvider = target.ProviderName,
                TargetDatabase = targetDatabase
            };
            foreach (string table in tables)
            {
                bool exists = SafeTableExists(target, targetDatabase, table);
                plan.Items.Add(new TransferItem { SourceTable = table, TargetTable = table, Mode = exists ? TransferMode.Append : TransferMode.CreateNew });
            }
            return plan;
        }

        /// <summary>依名稱（不分大小寫）自動對應欄位；目標沒有的來源欄位不寫入。</summary>
        public static List<TransferColumnMapping> AutoMap(IEnumerable<string> sourceColumns, IList<string> targetColumns)
        {
            return sourceColumns.Select(column => new TransferColumnMapping
            {
                SourceColumn = column,
                TargetColumn = targetColumns.FirstOrDefault(target => string.Equals(target, column, StringComparison.OrdinalIgnoreCase))
            }).ToList();
        }

        public static List<string> ColumnNames(IDatabase database, string databaseName, string table)
        {
            DataTable columns = database.GetColumns(databaseName, table) ?? new DataTable();
            List<string> names = new List<string>();
            foreach (DataRow row in columns.Rows)
            {
                foreach (string name in new[] { "Field", "COLUMN_NAME", "column_name", "name", "Name" })
                {
                    if (!columns.Columns.Contains(name) || row[name] is DBNull) continue;
                    names.Add(Convert.ToString(row[name], CultureInfo.InvariantCulture));
                    break;
                }
            }
            return names;
        }

        /// <summary>
        /// 執行（或續傳）計畫。每批寫入後呼叫 checkpoint，取消時在批次之間停止並保留進度。
        /// primaryKeyTables 為來源有主鍵的表，決定能否從中途續傳。
        /// </summary>
        public static void Run(
            TransferPlan plan,
            IDatabase source,
            IDatabase target,
            ISet<string> primaryKeyTables,
            Action<TransferProgress> progress,
            Action<TransferPlan> checkpoint,
            CancellationToken cancellationToken)
        {
            List<TransferItem> items = plan.Items.Where(item => item.Include && item.Status != TransferItemStatus.Done).ToList();
            int index = 0;
            foreach (TransferItem item in items)
            {
                index++;
                cancellationToken.ThrowIfCancellationRequested();
                Stopwatch watch = Stopwatch.StartNew();
                double previousSeconds = item.Seconds;
                try
                {
                    RunItem(plan, item, source, target, primaryKeyTables.Contains(item.SourceTable), index, items.Count, progress, checkpoint, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    item.Seconds = previousSeconds + watch.Elapsed.TotalSeconds;
                    Save(plan, checkpoint);
                    throw;
                }
                catch (Exception exception)
                {
                    item.Status = TransferItemStatus.Failed;
                    item.Error = ExceptionMessageService.GetReason(exception);
                    item.Seconds = previousSeconds + watch.Elapsed.TotalSeconds;
                    Save(plan, checkpoint);
                    if (!plan.ContinueOnError) return;
                    continue;
                }
                item.Seconds = previousSeconds + watch.Elapsed.TotalSeconds;
                Save(plan, checkpoint);
            }
        }

        private static void RunItem(
            TransferPlan plan,
            TransferItem item,
            IDatabase source,
            IDatabase target,
            bool resumable,
            int tableIndex,
            int tableCount,
            Action<TransferProgress> progress,
            Action<TransferPlan> checkpoint,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(item.TargetTable)) throw new InvalidOperationException(Localization.Format("Transfer.Error.TargetName", item.SourceTable));
            item.Error = null;
            item.Warning = null;
            item.Verified = null;
            bool resuming = item.CopiedRows > 0;
            if (resuming && !resumable)
            {
                // 沒有主鍵時分頁順序不穩定，無法確定哪些列已寫入：新表或取代模式從頭重來，附加模式拒絕以免重複。
                if (item.Mode == TransferMode.Append) throw new InvalidOperationException(Localization.Format("Transfer.Error.CannotResumeAppend", item.SourceTable));
                if (item.Mode == TransferMode.CreateNew && SafeTableExists(target, plan.TargetDatabase, item.TargetTable))
                {
                    target.DropTableForCopy(plan.TargetDatabase, item.TargetTable);
                }
                item.CopiedRows = 0;
                resuming = false;
                item.Warning = Localization.T("Transfer.Warn.Restarted");
            }

            item.Status = TransferItemStatus.Running;
            item.SourceRows = source.CountRows(plan.SourceDatabase, item.SourceTable);
            DataTable indexes = null;
            if (!resuming)
            {
                switch (item.Mode)
                {
                    case TransferMode.CreateNew:
                        if (SafeTableExists(target, plan.TargetDatabase, item.TargetTable))
                        {
                            throw new InvalidOperationException(Localization.Format("Transfer.Error.TargetExists", item.TargetTable));
                        }
                        DataTable columns = source.GetCopyColumns(plan.SourceDatabase, item.SourceTable);
                        if (columns == null || columns.Rows.Count == 0) throw new InvalidOperationException(Localization.T("Object.CopyNoColumns"));
                        target.CreateTableForCopy(plan.TargetDatabase, item.TargetTable, columns, source.ProviderName);
                        item.TargetRowsBefore = 0;
                        break;
                    case TransferMode.ReplaceData:
                        RequireTarget(target, plan, item);
                        DeleteAllRows(target, plan.TargetDatabase, item.TargetTable);
                        item.TargetRowsBefore = 0;
                        break;
                    default:
                        RequireTarget(target, plan, item);
                        item.TargetRowsBefore = target.CountRows(plan.TargetDatabase, item.TargetTable);
                        break;
                }
                Save(plan, checkpoint);
            }
            if (item.Mode == TransferMode.CreateNew)
            {
                try { indexes = source.GetCopyIndexes(plan.SourceDatabase, item.SourceTable); }
                catch (Exception) { indexes = null; }
            }

            Dictionary<string, string> mapping = item.Mode == TransferMode.CreateNew || item.Columns == null || item.Columns.Count == 0
                ? null
                : item.Columns.Where(column => !string.IsNullOrWhiteSpace(column.TargetColumn))
                    .ToDictionary(column => column.SourceColumn, column => column.TargetColumn, StringComparer.OrdinalIgnoreCase);
            if (mapping == null && item.Mode != TransferMode.CreateNew)
            {
                List<string> targetColumns = ColumnNames(target, plan.TargetDatabase, item.TargetTable);
                mapping = AutoMap(ColumnNames(source, plan.SourceDatabase, item.SourceTable), targetColumns)
                    .Where(column => column.TargetColumn != null)
                    .ToDictionary(column => column.SourceColumn, column => column.TargetColumn, StringComparer.OrdinalIgnoreCase);
            }
            if (mapping != null && mapping.Count == 0) throw new InvalidOperationException(Localization.Format("Transfer.Error.NoMappedColumns", item.SourceTable));

            int batch = Math.Max(1, plan.BatchSize);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DataTable page = source.SelectTablePage(plan.SourceDatabase, item.SourceTable, item.CopiedRows, batch);
                DataSyncService.ThrowIfQueryFailed(page);
                if (page.Rows.Count == 0) break;
                target.InsertTableBatch(plan.TargetDatabase, item.TargetTable, mapping == null ? page : Remap(page, mapping));
                item.CopiedRows += page.Rows.Count;
                Save(plan, checkpoint);
                if (progress != null)
                {
                    progress(new TransferProgress { Table = item.SourceTable, Copied = item.CopiedRows, Total = item.SourceRows, TableIndex = tableIndex, TableCount = tableCount });
                }
                if (page.Rows.Count < batch) break;
            }

            if (item.Mode == TransferMode.CreateNew && indexes != null && indexes.Rows.Count > 0)
            {
                try
                {
                    target.CreateIndexesForCopy(plan.TargetDatabase, item.TargetTable, indexes, source.ProviderName);
                }
                catch (Exception exception)
                {
                    item.Warning = Localization.Format("Transfer.Warn.Indexes", ExceptionMessageService.GetReason(exception));
                }
            }

            item.TargetRowsAfter = target.CountRows(plan.TargetDatabase, item.TargetTable);
            long expected = item.Mode == TransferMode.Append ? item.TargetRowsBefore + item.CopiedRows : item.SourceRows;
            item.Verified = item.TargetRowsAfter == expected && item.CopiedRows == item.SourceRows;
            if (item.Verified == false)
            {
                item.Warning = (item.Warning == null ? string.Empty : item.Warning + " ") +
                               Localization.Format("Transfer.Warn.CountMismatch", item.SourceRows, item.CopiedRows, item.TargetRowsAfter, expected);
            }
            item.Status = TransferItemStatus.Done;
        }

        private static DataTable Remap(DataTable page, Dictionary<string, string> mapping)
        {
            DataTable remapped = new DataTable();
            List<KeyValuePair<int, string>> kept = new List<KeyValuePair<int, string>>();
            for (int index = 0; index < page.Columns.Count; index++)
            {
                string target;
                if (!mapping.TryGetValue(page.Columns[index].ColumnName, out target)) continue;
                remapped.Columns.Add(target, page.Columns[index].DataType);
                kept.Add(new KeyValuePair<int, string>(index, target));
            }
            foreach (DataRow row in page.Rows)
            {
                DataRow copy = remapped.NewRow();
                foreach (KeyValuePair<int, string> column in kept) copy[column.Value] = row[column.Key];
                remapped.Rows.Add(copy);
            }
            return remapped;
        }

        private static void RequireTarget(IDatabase target, TransferPlan plan, TransferItem item)
        {
            if (!SafeTableExists(target, plan.TargetDatabase, item.TargetTable))
            {
                throw new InvalidOperationException(Localization.Format("Transfer.Error.TargetMissing", item.TargetTable));
            }
        }

        private static void DeleteAllRows(IDatabase target, string databaseName, string table)
        {
            string provider = SchemaSyncScriptService.NormalizeProvider(target.ProviderName);
            string name;
            if (provider == "mysql")
            {
                name = QueryBuilderService.Quote("mysql", databaseName) + "." + QueryBuilderService.Quote("mysql", table);
            }
            else if (provider == "mssql")
            {
                string schema, bare;
                DataSyncService.SplitName(provider, table, out schema, out bare);
                name = QueryBuilderService.Quote(provider, databaseName) + "." + QueryBuilderService.Quote(provider, schema) + "." + QueryBuilderService.Quote(provider, bare);
            }
            else
            {
                name = string.Join(".", table.Split('.').Select(part => QueryBuilderService.Quote(provider, part)));
            }
            Dictionary<string, string> result = target.ExecSQL("DELETE FROM " + name);
            string status;
            if (result == null || !result.TryGetValue("status", out status) || !string.Equals(status, "OK", StringComparison.OrdinalIgnoreCase))
            {
                string message;
                throw new InvalidOperationException(result != null && result.TryGetValue("reason", out message) && !string.IsNullOrWhiteSpace(message) ? message : Localization.T("Transfer.Error.DeleteFailed"));
            }
        }

        private static bool SafeTableExists(IDatabase database, string databaseName, string table)
        {
            try
            {
                return database.TableExists(databaseName, table);
            }
            catch (Exception)
            {
                return database.GetTables(databaseName).Any(name => string.Equals(name, table, StringComparison.OrdinalIgnoreCase));
            }
        }

        private static void Save(TransferPlan plan, Action<TransferPlan> checkpoint)
        {
            plan.UpdatedUtc = DateTime.UtcNow;
            if (checkpoint != null) checkpoint(plan);
        }

        // ------------------------------------------------------------ Checkpoints

        public static string DefaultCheckpointDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "mySQLPunk", "transfers"); }
        }

        /// <summary>以暫存檔原子替換寫入檢查點（不含任何密碼，只有連線名稱與資料庫名稱）。</summary>
        public static void SaveCheckpoint(TransferPlan plan, string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, plan.Id + ".json");
            string temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonConvert.SerializeObject(plan, Formatting.Indented), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }

        /// <summary>找出同一來源與目標最近一次尚未完成的檢查點。</summary>
        public static TransferPlan LoadUnfinished(string directory, TransferPlan route)
        {
            if (!Directory.Exists(directory)) return null;
            TransferPlan latest = null;
            foreach (string file in Directory.GetFiles(directory, "*.json"))
            {
                TransferPlan candidate;
                try
                {
                    candidate = JsonConvert.DeserializeObject<TransferPlan>(File.ReadAllText(file, Encoding.UTF8));
                }
                catch (JsonException)
                {
                    continue;
                }
                if (candidate == null || candidate.Items == null || candidate.IsFinished || candidate.RouteKey() != route.RouteKey()) continue;
                if (latest == null || candidate.UpdatedUtc > latest.UpdatedUtc) latest = candidate;
            }
            return latest;
        }

        public static void DeleteCheckpoint(TransferPlan plan, string directory)
        {
            string path = Path.Combine(directory, plan.Id + ".json");
            if (File.Exists(path)) File.Delete(path);
        }

        // ------------------------------------------------------------ Report

        public static string BuildHtmlReport(TransferPlan plan, string generatorVersion)
        {
            Func<string, string> h = value => WebUtility.HtmlEncode(value ?? string.Empty);
            StringBuilder html = new StringBuilder();
            html.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            html.AppendLine("<meta http-equiv=\"Content-Security-Policy\" content=\"default-src 'none'; style-src 'unsafe-inline'\">");
            html.AppendLine("<title>" + h(Localization.T("Transfer.Report.Title")) + "</title><style>");
            html.AppendLine("body{font-family:Segoe UI,Arial,sans-serif;margin:24px;color:#1f2937}table{border-collapse:collapse;width:100%}th,td{border:1px solid #d0d5dd;padding:6px 8px;text-align:left;font-size:13px}th{background:#f2f4f7}.ok{color:#067647}.bad{color:#b42318}.warn{color:#b54708}");
            html.AppendLine("</style></head><body>");
            html.AppendLine("<h1>" + h(Localization.T("Transfer.Report.Title")) + "</h1>");
            html.AppendLine("<p>" + h(Localization.Format("Transfer.Report.Route", plan.SourceLabel, plan.SourceDatabase, plan.SourceProvider, plan.TargetLabel, plan.TargetDatabase, plan.TargetProvider)) + "</p>");
            html.AppendLine("<p>" + h(Localization.Format("Transfer.Report.Generated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), generatorVersion)) + "</p>");
            List<TransferItem> included = plan.Items.Where(item => item.Include).ToList();
            html.AppendLine("<p>" + h(Localization.Format("Transfer.Report.Summary",
                included.Count(item => item.Status == TransferItemStatus.Done && item.Verified == true),
                included.Count(item => item.Status == TransferItemStatus.Failed || item.Verified == false),
                included.Count(item => item.Status == TransferItemStatus.Pending || item.Status == TransferItemStatus.Running),
                included.Sum(item => item.CopiedRows))) + "</p>");
            html.AppendLine("<table><tr>");
            foreach (string header in new[] { "Transfer.Column.Source", "Transfer.Column.Target", "Transfer.Column.Mode", "Transfer.Column.SourceRows", "Transfer.Column.Copied", "Transfer.Column.TargetRows", "Transfer.Column.Result", "Transfer.Column.Seconds", "Transfer.Column.Notes" })
            {
                html.Append("<th>" + h(Localization.T(header)) + "</th>");
            }
            html.AppendLine("</tr>");
            foreach (TransferItem item in included)
            {
                string css = item.Status == TransferItemStatus.Failed || item.Verified == false ? "bad" : item.Status == TransferItemStatus.Done ? "ok" : "warn";
                html.Append("<tr><td>" + h(item.SourceTable) + "</td><td>" + h(item.TargetTable) + "</td><td>" + h(ModeText(item.Mode)) + "</td>");
                html.Append("<td>" + item.SourceRows.ToString("N0", CultureInfo.InvariantCulture) + "</td><td>" + item.CopiedRows.ToString("N0", CultureInfo.InvariantCulture) + "</td>");
                html.Append("<td>" + item.TargetRowsAfter.ToString("N0", CultureInfo.InvariantCulture) + "</td><td class=\"" + css + "\">" + h(ResultText(item)) + "</td>");
                html.Append("<td>" + item.Seconds.ToString("0.0", CultureInfo.InvariantCulture) + "</td><td>" + h(string.Join(" ", new[] { item.Error, item.Warning }.Where(text => !string.IsNullOrEmpty(text)))) + "</td></tr>");
                html.AppendLine();
            }
            html.AppendLine("</table></body></html>");
            return html.ToString();
        }

        public static string ModeText(TransferMode mode)
        {
            return Localization.T("Transfer.Mode." + mode);
        }

        public static string ResultText(TransferItem item)
        {
            switch (item.Status)
            {
                case TransferItemStatus.Done:
                    return item.Verified == true ? Localization.T("Transfer.Result.Verified") : Localization.T("Transfer.Result.Mismatch");
                case TransferItemStatus.Failed:
                    return Localization.T("Transfer.Result.Failed");
                case TransferItemStatus.Running:
                    return Localization.Format("Transfer.Result.Partial", item.CopiedRows);
                default:
                    return Localization.T("Transfer.Result.Pending");
            }
        }
    }
}
