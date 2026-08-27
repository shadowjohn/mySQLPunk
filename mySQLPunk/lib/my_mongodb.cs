using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Driver;

namespace mySQLPunk.lib
{
    /// <summary>
    /// MongoDB 第一階段 provider：提供連線、metadata、schema 推斷、索引與唯讀文件查詢。
    /// 關聯式 DDL/資料複製方法刻意不假裝相容，避免 UI 誤送 SQL 寫入命令。
    /// </summary>
    public sealed class my_mongodb : IDatabase
    {
        private const int SchemaSampleSize = 100;
        private const int DefaultQueryLimit = 100;
        private const int MaxQueryLimit = 10000;
        private string connectionString = string.Empty;
        private string initialDatabase = string.Empty;
        private MongoClient client;
        private bool open;

        public string ProviderName => "mongodb";
        public ConnectionState State => open ? ConnectionState.Open : ConnectionState.Closed;

        public void SetConn(string value)
        {
            connectionString = value ?? string.Empty;
            MongoUrl url = string.IsNullOrWhiteSpace(connectionString) ? null : MongoUrl.Create(connectionString);
            initialDatabase = url == null ? string.Empty : (url.DatabaseName ?? string.Empty);
        }

        public void Open()
        {
            if (string.IsNullOrWhiteSpace(connectionString))
                throw new InvalidOperationException(Localization.T("MongoDB.ConnectionStringRequired"));

            MongoClientSettings settings = MongoClientSettings.FromConnectionString(connectionString);
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(8);
            settings.ConnectTimeout = TimeSpan.FromSeconds(8);
            MongoClient candidate = new MongoClient(settings);
            string pingDatabase = string.IsNullOrWhiteSpace(initialDatabase) ? "admin" : initialDatabase;
            candidate.GetDatabase(pingDatabase).RunCommand<BsonDocument>(new BsonDocument("ping", 1));
            client = candidate;
            open = true;
        }

        public void Close()
        {
            client = null;
            open = false;
        }

        public void Dispose()
        {
            Close();
        }

        public List<string> GetDatabases()
        {
            EnsureOpen();
            try
            {
                return client.ListDatabaseNames().ToList().OrderBy(name => name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (MongoCommandException ex)
            {
                // 部分受限帳號沒有 listDatabases 權限，但仍能使用 URI 指定的資料庫。
                if (string.IsNullOrWhiteSpace(initialDatabase) || ex.Code != 13) throw;
                return new List<string> { initialDatabase };
            }
        }

        public List<string> GetTables(string databaseName)
        {
            return GetCollectionInfos(databaseName)
                .Where(info => !string.Equals(GetString(info, "type"), "view", StringComparison.OrdinalIgnoreCase))
                .Select(info => GetString(info, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public List<string> GetViews(string databaseName)
        {
            return GetCollectionInfos(databaseName)
                .Where(info => string.Equals(GetString(info, "type"), "view", StringComparison.OrdinalIgnoreCase))
                .Select(info => GetString(info, "name"))
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public DataTable GetColumns(string databaseName, string tableName)
        {
            EnsureOpen();
            List<BsonDocument> documents = GetCollection(databaseName, tableName)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Limit(SchemaSampleSize)
                .ToList();

            DataTable result = CreateColumnMetadataTable();
            Dictionary<string, FieldObservation> observations = new Dictionary<string, FieldObservation>(StringComparer.Ordinal);
            foreach (BsonDocument document in documents)
            {
                foreach (BsonElement element in document)
                {
                    FieldObservation observation;
                    if (!observations.TryGetValue(element.Name, out observation))
                    {
                        observation = new FieldObservation { Name = element.Name };
                        observations.Add(element.Name, observation);
                    }
                    observation.PresentCount++;
                    observation.Types.Add(element.Value == null ? "Null" : element.Value.BsonType.ToString());
                    if (element.Value == null || element.Value.IsBsonNull) observation.HasNull = true;
                }
            }

            foreach (FieldObservation observation in observations.Values.OrderBy(item => item.Name == "_id" ? 0 : 1).ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            {
                DataRow row = result.NewRow();
                row["Field"] = observation.Name;
                row["Type"] = string.Join(" | ", observation.Types.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray());
                row["Null"] = observation.HasNull || observation.PresentCount < documents.Count ? "YES" : "NO";
                row["Key"] = observation.Name == "_id" ? "PRI" : string.Empty;
                row["Default"] = string.Empty;
                row["Extra"] = Localization.Format("MongoDB.SchemaSample", documents.Count);
                row["Comment"] = string.Empty;
                result.Rows.Add(row);
            }
            return result;
        }

        public DataTable GetIndexes(string databaseName, string tableName)
        {
            EnsureOpen();
            DataTable result = new DataTable();
            result.Columns.Add("Key_name");
            result.Columns.Add("Column_name");
            result.Columns.Add("Non_unique", typeof(int));
            result.Columns.Add("Seq_in_index", typeof(int));
            result.Columns.Add("Index_type");

            foreach (BsonDocument index in GetCollection(databaseName, tableName).Indexes.List().ToList())
            {
                string name = GetString(index, "name");
                bool unique = index.Contains("unique") && index["unique"].ToBoolean();
                BsonDocument keys = index.Contains("key") && index["key"].IsBsonDocument
                    ? index["key"].AsBsonDocument
                    : new BsonDocument();
                int sequence = 1;
                foreach (BsonElement key in keys)
                {
                    DataRow row = result.NewRow();
                    row["Key_name"] = name;
                    row["Column_name"] = key.Name;
                    row["Non_unique"] = unique ? 0 : 1;
                    row["Seq_in_index"] = sequence++;
                    row["Index_type"] = GetMongoIndexType(key.Value);
                    result.Rows.Add(row);
                }
            }
            return result;
        }

        public DataTable GetTableStatus(string databaseName)
        {
            EnsureOpen();
            DataTable result = new DataTable();
            result.Columns.Add("Name");
            result.Columns.Add("Rows", typeof(long));
            result.Columns.Add("Data_length", typeof(long));
            result.Columns.Add("Index_length", typeof(long));
            result.Columns.Add("Engine");
            result.Columns.Add("Update_time");
            result.Columns.Add("Comment");

            IMongoDatabase database = GetDatabase(databaseName);
            foreach (string collectionName in GetTables(databaseName))
            {
                DataRow row = result.NewRow();
                row["Name"] = collectionName;
                row["Engine"] = "MongoDB";
                row["Update_time"] = string.Empty;
                row["Comment"] = Localization.T("MongoDB.Collection");
                try
                {
                    BsonDocument stats = database.RunCommand<BsonDocument>(new BsonDocument { { "collStats", collectionName }, { "scale", 1 } });
                    row["Rows"] = GetInt64(stats, "count");
                    row["Data_length"] = GetInt64(stats, "size");
                    row["Index_length"] = GetInt64(stats, "totalIndexSize");
                }
                catch (MongoCommandException)
                {
                    row["Rows"] = CountRows(databaseName, collectionName);
                    row["Data_length"] = 0L;
                    row["Index_length"] = 0L;
                }
                result.Rows.Add(row);
            }
            return result;
        }

        public Dictionary<string, string> GetDatabaseInfo(string databaseName)
        {
            EnsureOpen();
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Provider", "MongoDB" },
                { "Database", databaseName ?? string.Empty }
            };
            BsonDocument stats = GetDatabase(databaseName).RunCommand<BsonDocument>(new BsonDocument("dbStats", 1));
            foreach (string name in new[] { "collections", "views", "objects", "dataSize", "storageSize", "indexes", "indexSize" })
            {
                if (stats.Contains(name)) result[name] = ValueToDisplayString(stats[name]);
            }
            return result;
        }

        public string GetTableCreateStatement(string databaseName, string tableName)
        {
            BsonDocument info = GetCollectionInfos(databaseName)
                .FirstOrDefault(item => string.Equals(GetString(item, "name"), tableName, StringComparison.Ordinal));
            return info == null ? string.Empty : info.ToJson(new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.RelaxedExtendedJson });
        }

        public bool TableExists(string databaseName, string tableName)
        {
            return GetTables(databaseName).Contains(tableName, StringComparer.Ordinal);
        }

        public bool ViewExists(string databaseName, string viewName)
        {
            return GetViews(databaseName).Contains(viewName, StringComparer.Ordinal);
        }

        public long CountRows(string databaseName, string tableName)
        {
            EnsureOpen();
            return GetCollection(databaseName, tableName).CountDocuments(FilterDefinition<BsonDocument>.Empty);
        }

        public DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit)
        {
            EnsureOpen();
            int safeOffset = offset <= 0 ? 0 : (offset > int.MaxValue ? int.MaxValue : (int)offset);
            int safeLimit = NormalizeLimit(limit);
            List<BsonDocument> documents = GetCollection(databaseName, tableName)
                .Find(FilterDefinition<BsonDocument>.Empty)
                .Skip(safeOffset)
                .Limit(safeLimit)
                .ToList();
            return ConvertDocumentsToDataTable(documents);
        }

        /// <summary>執行目前 MongoDB 查詢分頁中的唯讀 JSON find 規格。</summary>
        public DataTable SelectJsonQuery(string databaseName, string query)
        {
            EnsureOpen();
            MongoReadQuery request = MongoReadQuery.Parse(query);
            string effectiveDatabase = string.IsNullOrWhiteSpace(databaseName) ? initialDatabase : databaseName;
            if (string.IsNullOrWhiteSpace(effectiveDatabase))
                throw new InvalidOperationException(Localization.T("MongoDB.DatabaseRequired"));

            IFindFluent<BsonDocument, BsonDocument> find = GetCollection(effectiveDatabase, request.Collection)
                .Find(request.Filter);
            if (request.Projection != null && request.Projection.ElementCount > 0) find = find.Project<BsonDocument>(request.Projection);
            if (request.Sort != null && request.Sort.ElementCount > 0) find = find.Sort(request.Sort);
            if (request.Skip > 0) find = find.Skip(request.Skip);
            find = find.Limit(request.Limit);
            return ConvertDocumentsToDataTable(find.ToList());
        }

        /// <summary>以 _id 過濾器重新讀取完整文件；文件不存在時回傳 null。</summary>
        public string FindDocumentJson(string databaseName, string collectionName, string idFilterJson)
        {
            EnsureOpen();
            BsonDocument filter = BsonDocument.Parse(idFilterJson);
            BsonDocument document = GetCollection(databaseName, collectionName).Find(filter).Limit(1).FirstOrDefault();
            // 直接輸出 Canonical Extended JSON；經過 relaxed 表示法再轉會讓 Int64 這類型別變質。
            return document == null
                ? null
                : document.ToJson(new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.CanonicalExtendedJson });
        }

        /// <summary>
        /// 安全寫回單一文件：先驗證 _id 未變，再以「編輯前的完整文件」當過濾器做樂觀並行比對；
        /// 文件在編輯期間被別人改過或刪除時，這裡不會寫入，而是丟出衝突錯誤。
        /// 頂層值可能被查詢引擎解讀成運算子的文件，改用 _id 過濾＋寫入前重新讀取比對。
        /// </summary>
        public void ReplaceDocumentChecked(string databaseName, string collectionName, string originalDocumentJson, string editedDocumentJson)
        {
            EnsureOpen();
            MongoDocumentEditValidation validation = MongoDocumentEditService.ValidateEdit(originalDocumentJson, editedDocumentJson);
            if (!validation.Success) throw new InvalidOperationException(validation.Error);
            if (!validation.HasChanges) return;

            BsonDocument original = BsonDocument.Parse(originalDocumentJson);
            BsonDocument replacement = BsonDocument.Parse(validation.NormalizedJson);
            IMongoCollection<BsonDocument> collection = GetCollection(databaseName, collectionName);
            FilterDefinition<BsonDocument> filter = BuildCheckedWriteFilter(collection, original, originalDocumentJson);

            ReplaceOneResult result = collection.ReplaceOne(filter, replacement);
            if (result.MatchedCount == 0)
                throw new InvalidOperationException(Localization.T("MongoDB.DocumentConcurrencyConflict"));
        }

        /// <summary>新增單一文件；未提供 _id 時由驅動產生 ObjectId，回傳含 _id 的完整文件 JSON。</summary>
        public string InsertDocumentChecked(string databaseName, string collectionName, string documentJson)
        {
            EnsureOpen();
            MongoDocumentEditValidation validation = MongoDocumentEditService.ValidateInsert(documentJson);
            if (!validation.Success) throw new InvalidOperationException(validation.Error);
            BsonDocument document = BsonDocument.Parse(validation.NormalizedJson);
            GetCollection(databaseName, collectionName).InsertOne(document);
            return document.ToJson(new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.CanonicalExtendedJson });
        }

        /// <summary>安全刪除單一文件：與寫回共用重讀比對＋完整文件過濾；被別人改過或已刪除時回報衝突。</summary>
        public void DeleteDocumentChecked(string databaseName, string collectionName, string originalDocumentJson)
        {
            EnsureOpen();
            BsonDocument original = BsonDocument.Parse(originalDocumentJson);
            if (!original.Contains("_id") || original["_id"].IsBsonNull)
                throw new InvalidOperationException(Localization.T("MongoDB.DocumentIdRequired"));
            IMongoCollection<BsonDocument> collection = GetCollection(databaseName, collectionName);
            FilterDefinition<BsonDocument> filter = BuildCheckedWriteFilter(collection, original, originalDocumentJson);

            DeleteResult result = collection.DeleteOne(filter);
            if (result.DeletedCount == 0)
                throw new InvalidOperationException(Localization.T("MongoDB.DocumentConcurrencyConflict"));
        }

        /// <summary>
        /// 寫入前的樂觀並行防線：先重讀完整比對（含欄位新增），
        /// 再回傳原子寫入用的過濾器堵住重讀之後的競態。
        /// </summary>
        private FilterDefinition<BsonDocument> BuildCheckedWriteFilter(
            IMongoCollection<BsonDocument> collection, BsonDocument original, string originalDocumentJson)
        {
            BsonDocument idFilter = new BsonDocument("_id", original["_id"]);
            BsonDocument current = collection.Find(idFilter).Limit(1).FirstOrDefault();
            if (current == null || !current.Equals(original))
                throw new InvalidOperationException(Localization.T("MongoDB.DocumentConcurrencyConflict"));

            // 頂層值可能被查詢引擎解讀成運算子的文件，退回 _id 過濾（重讀比對已完成大部分防護）。
            if (!MongoDocumentEditService.CanUseFullDocumentFilter(originalDocumentJson)) return idFilter;

            // 全文件等值只驗證既有欄位；補上頂層欄位數量檢查，別人在競態期間「新增」欄位也會落空。
            BsonDocument fieldCountGuard = new BsonDocument("$expr", new BsonDocument("$eq", new BsonArray
            {
                new BsonDocument("$size", new BsonDocument("$objectToArray", "$$ROOT")),
                original.ElementCount
            }));
            return new BsonDocument("$and", new BsonArray { original, fieldCountGuard });
        }

        /// <summary>從查詢分頁目前的內容取出目標 collection 名稱；解析失敗時回傳 false。</summary>
        public static bool TryGetQueryCollection(string query, out string collectionName)
        {
            collectionName = string.Empty;
            try
            {
                MongoReadQuery request = MongoReadQuery.Parse(query);
                collectionName = request.Collection;
                return !string.IsNullOrWhiteSpace(collectionName);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return SelectJsonQuery(initialDatabase, sql);
        }

        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.Run(() => SelectSQL(sql, parameters));
        }

        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null)
        {
            return new Dictionary<string, string>
            {
                { "status", "ERROR" },
                { "reason", Localization.T("MongoDB.ReadOnlyFirstPhase") }
            };
        }

        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null)
        {
            return Task.FromResult(ExecSQL(sql, parameters));
        }

        public DataTable GetCopyColumns(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public DataTable GetCopyIndexes(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider) { throw UnsupportedWrite(); }
        public void DropTableForCopy(string databaseName, string tableName) { throw UnsupportedWrite(); }
        public void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider) { throw UnsupportedWrite(); }
        public void InsertTableBatch(string databaseName, string tableName, DataTable rows) { throw UnsupportedWrite(); }
        public void RenameTable(string databaseName, string oldTableName, string newTableName) { throw UnsupportedWrite(); }
        public void RenameView(string databaseName, string oldViewName, string newViewName) { throw UnsupportedWrite(); }
        public string GetViewCreateStatement(string databaseName, string viewName) { return GetTableCreateStatement(databaseName, viewName); }
        public void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql) { throw UnsupportedWrite(); }

        public static string BuildQueryTemplate(string collectionName)
        {
            BsonDocument template = new BsonDocument
            {
                { "collection", collectionName ?? string.Empty },
                { "filter", new BsonDocument() },
                { "projection", new BsonDocument() },
                { "sort", new BsonDocument("_id", 1) },
                { "limit", DefaultQueryLimit }
            };
            return template.ToJson(new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.RelaxedExtendedJson });
        }

        /// <summary>把單一文件 JSON 轉成與查詢結果相同格式的單列 DataTable，供文件儲存後回寫網格顯示。</summary>
        public static DataTable ConvertJsonDocumentToDataTable(string documentJson)
        {
            return ConvertDocumentsToDataTable(new[] { BsonDocument.Parse(documentJson) });
        }

        internal static DataTable ConvertDocumentsToDataTable(IEnumerable<BsonDocument> source)
        {
            List<BsonDocument> documents = (source ?? Enumerable.Empty<BsonDocument>()).ToList();
            DataTable result = new DataTable();
            Dictionary<string, string> columnNames = new Dictionary<string, string>(StringComparer.Ordinal);
            IEnumerable<string> fields = documents.SelectMany(document => document.Names)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name == "_id" ? 0 : 1)
                .ThenBy(name => name, StringComparer.OrdinalIgnoreCase);
            foreach (string field in fields)
            {
                string columnName = MakeUniqueColumnName(result, field);
                result.Columns.Add(columnName, typeof(string));
                columnNames[field] = columnName;
            }
            string jsonColumn = MakeUniqueColumnName(result, "_json");
            result.Columns.Add(jsonColumn, typeof(string));

            JsonWriterSettings jsonSettings = new JsonWriterSettings { Indent = false, OutputMode = JsonOutputMode.RelaxedExtendedJson };
            foreach (BsonDocument document in documents)
            {
                DataRow row = result.NewRow();
                foreach (BsonElement element in document)
                {
                    row[columnNames[element.Name]] = element.Value == null || element.Value.IsBsonNull
                        ? (object)DBNull.Value
                        : ValueToDisplayString(element.Value);
                }
                row[jsonColumn] = document.ToJson(jsonSettings);
                result.Rows.Add(row);
            }
            return result;
        }

        private static DataTable CreateColumnMetadataTable()
        {
            DataTable result = new DataTable();
            result.Columns.Add("Field");
            result.Columns.Add("Type");
            result.Columns.Add("Null");
            result.Columns.Add("Key");
            result.Columns.Add("Default");
            result.Columns.Add("Extra");
            result.Columns.Add("Comment");
            return result;
        }

        private List<BsonDocument> GetCollectionInfos(string databaseName)
        {
            EnsureOpen();
            return GetDatabase(databaseName).ListCollections().ToList();
        }

        private IMongoDatabase GetDatabase(string databaseName)
        {
            EnsureOpen();
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException(Localization.T("MongoDB.DatabaseRequired"), "databaseName");
            return client.GetDatabase(databaseName);
        }

        private IMongoCollection<BsonDocument> GetCollection(string databaseName, string collectionName)
        {
            if (string.IsNullOrWhiteSpace(collectionName)) throw new ArgumentException(Localization.T("MongoDB.CollectionRequired"), "collectionName");
            return GetDatabase(databaseName).GetCollection<BsonDocument>(collectionName);
        }

        private void EnsureOpen()
        {
            if (!open || client == null) throw new InvalidOperationException(Localization.T("MongoDB.ConnectionNotOpen"));
        }

        private static Exception UnsupportedWrite()
        {
            return new NotSupportedException(Localization.T("MongoDB.ReadOnlyFirstPhase"));
        }

        private static string GetString(BsonDocument document, string name)
        {
            return document != null && document.Contains(name) && !document[name].IsBsonNull
                ? document[name].ToString()
                : string.Empty;
        }

        private static long GetInt64(BsonDocument document, string name)
        {
            if (document == null || !document.Contains(name) || document[name].IsBsonNull) return 0L;
            BsonValue value = document[name];
            if (value.IsInt64) return value.AsInt64;
            if (value.IsInt32) return value.AsInt32;
            if (value.IsDouble) return Convert.ToInt64(value.AsDouble);
            long parsed;
            return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0L;
        }

        private static string GetMongoIndexType(BsonValue value)
        {
            if (value == null) return string.Empty;
            if (value.IsString) return value.AsString;
            if (value.IsInt32 || value.IsInt64 || value.IsDouble) return value.ToString() == "-1" ? "DESC" : "ASC";
            return value.ToString();
        }

        private static string ValueToDisplayString(BsonValue value)
        {
            if (value == null || value.IsBsonNull) return string.Empty;
            if (value.IsString) return value.AsString;
            if (value.IsObjectId) return value.AsObjectId.ToString();
            if (value.IsBoolean) return value.AsBoolean ? "true" : "false";
            if (value.IsBsonDateTime) return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            if (value.IsBsonDocument || value.IsBsonArray)
                return value.ToJson(new JsonWriterSettings { Indent = false, OutputMode = JsonOutputMode.RelaxedExtendedJson });
            return value.ToString();
        }

        private static int NormalizeLimit(int limit)
        {
            if (limit <= 0) return DefaultQueryLimit;
            return Math.Min(limit, MaxQueryLimit);
        }

        private static string MakeUniqueColumnName(DataTable table, string requested)
        {
            string baseName = string.IsNullOrWhiteSpace(requested) ? "field" : requested;
            string candidate = baseName;
            int suffix = 2;
            while (table.Columns.Contains(candidate)) candidate = baseName + "_" + suffix++;
            return candidate;
        }

        private sealed class FieldObservation
        {
            public string Name;
            public int PresentCount;
            public bool HasNull;
            public HashSet<string> Types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        private sealed class MongoReadQuery
        {
            public string Collection;
            public BsonDocument Filter;
            public BsonDocument Projection;
            public BsonDocument Sort;
            public int Skip;
            public int Limit;

            public static MongoReadQuery Parse(string query)
            {
                if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException(Localization.T("MongoDB.QueryRequired"), "query");
                string trimmed = query.Trim();
                if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
                {
                    Match match = Regex.Match(trimmed, @"^SELECT\s+(?:\*|.+?)\s+FROM\s+[`\""\[]?(?<name>[^`\""\]\s;\.]+)[`\""\]]?\s*;?$", RegexOptions.IgnoreCase);
                    if (!match.Success) throw new FormatException(Localization.T("MongoDB.QueryFormatHelp"));
                    return new MongoReadQuery
                    {
                        Collection = match.Groups["name"].Value,
                        Filter = new BsonDocument(),
                        Projection = new BsonDocument(),
                        Sort = new BsonDocument(),
                        Limit = DefaultQueryLimit
                    };
                }

                BsonDocument document;
                try { document = BsonDocument.Parse(trimmed); }
                catch (Exception ex) { throw new FormatException(Localization.Format("MongoDB.InvalidJsonQuery", ex.Message), ex); }

                HashSet<string> allowed = new HashSet<string>(new[] { "collection", "filter", "projection", "sort", "skip", "limit" }, StringComparer.OrdinalIgnoreCase);
                string unsupported = document.Names.FirstOrDefault(name => !allowed.Contains(name));
                if (!string.IsNullOrWhiteSpace(unsupported))
                    throw new FormatException(Localization.Format("MongoDB.UnsupportedQueryField", unsupported));

                string collection = document.Contains("collection") && document["collection"].IsString ? document["collection"].AsString.Trim() : string.Empty;
                if (string.IsNullOrWhiteSpace(collection)) throw new FormatException(Localization.T("MongoDB.CollectionRequired"));

                return new MongoReadQuery
                {
                    Collection = collection,
                    Filter = ReadDocument(document, "filter"),
                    Projection = ReadDocument(document, "projection"),
                    Sort = ReadDocument(document, "sort"),
                    Skip = ReadNonNegativeInt(document, "skip", 0),
                    Limit = NormalizeLimit(ReadNonNegativeInt(document, "limit", DefaultQueryLimit))
                };
            }

            private static BsonDocument ReadDocument(BsonDocument source, string name)
            {
                if (!source.Contains(name) || source[name].IsBsonNull) return new BsonDocument();
                if (!source[name].IsBsonDocument) throw new FormatException(Localization.Format("MongoDB.QueryFieldMustBeDocument", name));
                return source[name].AsBsonDocument;
            }

            private static int ReadNonNegativeInt(BsonDocument source, string name, int fallback)
            {
                if (!source.Contains(name) || source[name].IsBsonNull) return fallback;
                int value;
                try { value = source[name].ToInt32(); }
                catch (Exception) { throw new FormatException(Localization.Format("MongoDB.QueryFieldMustBeInteger", name)); }
                if (value < 0) throw new FormatException(Localization.Format("MongoDB.QueryFieldNonNegative", name));
                return value;
            }
        }
    }
}
