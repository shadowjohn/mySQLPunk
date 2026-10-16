using System;
using System.Collections.Generic;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using mySQLPunk.lib;
using mySQLPunk.lib.MySqlExport;
using mySQLPunk.lib.MySqlImport;

public static partial class SmokeTests
{
    private static readonly byte[] GeometryPointWkb = new byte[] { 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 240, 63, 0, 0, 0, 0, 0, 0, 240, 191 };

    private static void MySqlExport_Geometry_MetadataPreflightAndFreshAttempt()
    {
        GeometryWithDirectory((directory, path) =>
        {
            var db = new GeometryRecordingDatabase();
            var table = db.AddTable("shapes");
            foreach (string type in new[] { "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon", "geometrycollection" })
                table.Columns.Add(new GeometryTestColumn(type, type.ToUpperInvariant()));
            table.Columns.Add(new GeometryTestColumn("ordinary_enum", "enum('point','geometry')"));
            var service = new MySqlSqlExportService(db, "geo_source");
            GeometryValidateMetadata(service, new[] { "shapes" }, CancellationToken.None);
            Assert(db.MetadataReads == 1 && db.Queries.Count == 0 && db.Executed.Count == 0 && db.CountReads == 0, "Metadata preflight must not query rows, counts, status or open a session transaction");
            Assert(Directory.GetFiles(directory).Length == 0, "Metadata preflight creates no output");
            table.Columns[7].Type = "vector(3)";
            File.WriteAllText(path, "previous complete backup");
            var result = service.ExportAsync(GeometryOptions(path, "shapes"), null, CancellationToken.None).Result;
            Assert(!result.Success && result.ErrorMessage.Contains("shapes.geometrycollection") && result.ErrorMessage.Contains("vector"), "Each attempt must re-read metadata and name unsupported type/column");
            Assert(db.MetadataReads == 2 && db.PageReads == 0 && db.BoundReads == 0 && db.CountReads == 0, "Fresh unsupported metadata fails before row scans and pages");
            AssertEquals("previous complete backup", File.ReadAllText(path), "Fresh incompatibility preserves backup");
            Assert(Directory.GetFiles(directory, "*.staging.*.tmp").Length == 0, "Fresh incompatibility creates no staging file");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                int before = db.MetadataReads;
                bool cancelled = false;
                try { GeometryValidateMetadata(service, new[] { "shapes" }, cancel.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && db.MetadataReads == before, "Cancelled metadata preflight performs no provider call");
            }
        });
    }

    private static void MySqlExport_Geometry_ValuesAndImportExpressions()
    {
        GeometryWithDirectory((directory, path) =>
        {
            var db = new GeometryRecordingDatabase();
            var table = db.AddTable("shapes");
            table.Columns.Add(new GeometryTestColumn("id", "int"));
            foreach (string type in new[] { "geometry", "point", "linestring", "polygon", "multipoint", "multilinestring", "multipolygon", "geometrycollection" })
                table.Columns.Add(new GeometryTestColumn(type, type));
            table.Columns.Add(new GeometryTestColumn("binary_payload", "blob"));
            var row = table.AddRow(); row.Values["id"] = 1; row.Values["binary_payload"] = new byte[] { 0, 255, 128, 39 };
            int i = 0;
            foreach (var column in table.Columns.Where(c => c.Name != "id" && c.Name != "binary_payload"))
            {
                row.Values[column.Name] = i == 3 ? DBNull.Value : GeometryPointWkb;
                row.Srids[column.Name] = i == 3 ? (object)DBNull.Value : i == 0 ? 0U : i == 1 ? 4326U : i == 2 ? uint.MaxValue : 3857U;
                i++;
            }
            var result = new MySqlSqlExportService(db, "geo_source").ExportAsync(GeometryOptions(path, "shapes"), null, CancellationToken.None).Result;
            Assert(result.Success, "All eight native spatial types export through WKB: " + result.ErrorMessage);
            string dump = File.ReadAllText(path);
            string hex = "0x" + BitConverter.ToString(GeometryPointWkb).Replace("-", "");
            AssertContains(dump, "ST_GeomFromWKB(" + hex + ",0)", "Zero SRID preserved");
            AssertContains(dump, "ST_GeomFromWKB(" + hex + ",4326)", "Nonzero SRID preserved");
            AssertContains(dump, "ST_GeomFromWKB(" + hex + ",4294967295)", "Unsigned maximum SRID preserved");
            AssertContains(dump, ", NULL, ST_GeomFromWKB", "Nullable geometry emits SQL NULL");
            AssertContains(dump, "0x00FF8027", "Ordinary BLOB stays a binary literal");
            Assert(!dump.Contains("axis-order") && !dump.Contains("ST_GeomFromText"), "Use common two-argument constructor with matching default axis convention");
            Assert(MySqlImportPreflightScanner.Scan(path, CancellationToken.None).Passed, "Spatial dump passes actual parser and digest preflight");
            var target = new GeometryRecordingDatabase { ImportMode = true };
            target.Tables.Add("shapes", table);
            var imported = new MySqlImportRunner(target, "geo_target").RunAsync(new MySqlImportRequest(Guid.NewGuid(), path, "geo_target"), new MySqlImportOptions(), null, CancellationToken.None).Result;
            Assert(imported.Status == MySqlImportStatus.Completed, "Actual recording import accepts WKB constructor expressions: " + imported.Summary);
            string inserted = target.Executed.Single(sql => sql.TrimStart().StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase));
            AssertContains(inserted, "ST_GeomFromWKB(" + hex + ",4326)", "Import preserves exact WKB/SRID expression");
            Assert(target.Executed.Any(sql => sql.Contains("USE `geo_target`")), "Import selects requested target");
            string page = db.Queries.Single(sql => sql.Contains(" LIMIT "));
            AssertContains(page, "THEN ST_AsWKB(`point`) ELSE NULL END AS `point`", "WKB remains behind row-size CASE guard");
            AssertContains(page, "THEN ST_SRID(`point`) ELSE NULL END AS", "SRID remains behind row-size CASE guard");
        });
    }

    private static void MySqlExport_Geometry_GeneratedColumnsAndIdentifiers()
    {
        GeometryWithDirectory((directory, path) =>
        {
            var db = new GeometryRecordingDatabase();
            var table = db.AddTable("city`geometry.測試");
            table.Columns.Add(new GeometryTestColumn("SHAPE`一", "point"));
            table.Columns.Add(new GeometryTestColumn("other.geometry", "geometry"));
            table.Columns.Add(new GeometryTestColumn("__MYSQLPUNK_SRID_0", "varchar(30)"));
            table.Columns.Add(new GeometryTestColumn("__MYSQLPUNK_ROW_BYTES", "varchar(30)"));
            table.Columns.Add(new GeometryTestColumn("generated_shape", "geometry", true));
            var row = table.AddRow(); row.Values["SHAPE`一"] = GeometryPointWkb; row.Srids["SHAPE`一"] = 4326;
            row.Values["other.geometry"] = GeometryPointWkb; row.Srids["other.geometry"] = 3857;
            row.Values["__MYSQLPUNK_SRID_0"] = "ordinary"; row.Values["__MYSQLPUNK_ROW_BYTES"] = "ordinary marker";
            var result = new MySqlSqlExportService(db, "geo_source").ExportAsync(GeometryOptions(path, table.Name), null, CancellationToken.None).Result;
            Assert(result.Success, "Generated spatial values and quoted helper collisions export: " + result.ErrorMessage);
            string dump = File.ReadAllText(path);
            AssertContains(dump, "GENERATED ALWAYS", "Generated spatial DDL retained");
            string insert = MySqlScriptReader.Read(new StringReader(dump), CancellationToken.None).Single(s => s.Sql.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase)).Sql;
            string columnList = insert.Substring(0, insert.IndexOf(" VALUES", StringComparison.Ordinal));
            AssertContains(columnList, "`SHAPE``一`, `other.geometry`, `__MYSQLPUNK_SRID_0`, `__MYSQLPUNK_ROW_BYTES`", "Only original writable identifiers enter INSERT");
            Assert(!columnList.Contains("generated_shape") && !columnList.Contains("__mysqlpunk_srid_"), "Generated and allocated helper columns omitted from INSERT");
            foreach (string sql in db.Queries)
                Assert(!sql.Contains("generated_shape"), "Generated geometry is not fetched or counted in source bound");
            string page = db.Queries.Single(sql => sql.Contains(" LIMIT "));
            var aliases = GeometryRecordingDatabase.ReadAliases(page);
            Assert(aliases.Count == aliases.Distinct(StringComparer.OrdinalIgnoreCase).Count(), "Source projection helper aliases are unique ignoring case");
            AssertContains(page, "`geo_source`.`city``geometry.測試`", "Database and table identifiers safely quoted");
        });
    }

    private static void MySqlExport_Geometry_BoundsRaceAndMalformedValues()
    {
        GeometryWithDirectory((directory, path) =>
        {
            var malformed = new[] {
                new object[] { GeometryPointWkb, DBNull.Value }, new object[] { DBNull.Value, 4326 },
                new object[] { new byte[0], 0 }, new object[] { "not binary", 0 },
                new object[] { GeometryPointWkb, -1 }, new object[] { GeometryPointWkb, 1.5m },
                new object[] { GeometryPointWkb, 4294967296UL }, new object[] { GeometryPointWkb, "4326" }
            };
            foreach (object[] values in malformed)
            {
                File.WriteAllText(path, "previous complete backup");
                var db = GeometryOneRow(values[0], values[1]);
                var result = new MySqlSqlExportService(db, "geo_source").ExportAsync(GeometryOptions(path, "shapes"), null, CancellationToken.None).Result;
                Assert(!result.Success, "Malformed WKB/SRID provider result fails explicitly");
                AssertEquals("previous complete backup", File.ReadAllText(path), "Malformed value preserves previous complete output");
                Assert(Directory.GetFiles(directory, "*.staging.*.tmp").Length == 0, "Failed value leaves no incomplete staging file");
            }
            foreach (bool race in new[] { false, true })
            {
                File.WriteAllText(path, "previous complete backup");
                var db = GeometryOneRow(GeometryPointWkb, 4326);
                if (race) db.PageRowBytes = 1048577; else db.Tables["shapes"].MaxRowBytes = 1048577;
                var result = new MySqlSqlExportService(db, "geo_source").ExportAsync(GeometryOptions(path, "shapes"), null, CancellationToken.None).Result;
                Assert(!result.Success, "Oversized geometry source or enlargement race fails");
                Assert(db.PageReads == (race ? 1 : 0), "Initial oversize fails before materialization; race fails at guarded page");
                AssertEquals("previous complete backup", File.ReadAllText(path), "Bound failure preserves previous backup");
            }
            var nullable = GeometryOneRow(DBNull.Value, DBNull.Value);
            Assert(new MySqlSqlExportService(nullable, "geo_source").ExportAsync(GeometryOptions(path, "shapes"), null, CancellationToken.None).Result.Success, "Both-null geometry pair succeeds");
        });
    }

    private static void MySqlExport_Geometry_BatchCapsAndEarlyFailure()
    {
        GeometryWithDirectory((directory, path) =>
        {
            var db = GeometryOneRow(GeometryPointWkb, 0);
            var last = db.AddTable("last_table"); last.Columns.Add(new GeometryTestColumn("unsupported", "vector(3)"));
            File.WriteAllText(path, "previous complete backup");
            var failed = new MySqlSqlExportService(db, "geo_source").ExportAsync(GeometryOptions(path, "shapes", "last_table"), null, CancellationToken.None).Result;
            Assert(!failed.Success && db.MetadataReads == 2 && db.BoundReads == 0 && db.CountReads == 0 && db.PageReads == 0, "Unsupported last table must fail before ANY bounds, counts or pages");
            Assert(Directory.GetFiles(directory, "*.staging.*.tmp").Length == 0, "Unsupported last table creates no staging");
            db = GeometryOneRow(new byte[1048566], 0);
            db.Tables["shapes"].MaxRowBytes = 1048576;
            for (int i = 0; i < 5; i++) { var row = db.Tables["shapes"].AddRow(); row.Values["shape"] = new byte[1048566]; row.Srids["shape"] = 0; }
            var second = db.AddTable("later"); second.Columns.Add(new GeometryTestColumn("shape", "point"));
            var secondRow = second.AddRow(); secondRow.Values["shape"] = GeometryPointWkb; secondRow.Srids["shape"] = 0;
            db.BeforeSourceScan = () => Assert(Directory.GetFiles(directory, "*.staging.*.tmp").Length == 0, "All bounds and counts precede staging creation");
            var result = new MySqlSqlExportService(db, "geo_source").ExportAsync(GeometryOptions(path, "shapes", "later"), null, CancellationToken.None).Result;
            Assert(result.Success && result.TotalRows == 7, "Bounded geometries split into safe INSERT statements: " + result.ErrorMessage);
            Assert(db.Events.IndexOf("page:shapes") > db.Events.IndexOf("bound:later") && db.Events.IndexOf("page:shapes") > db.Events.IndexOf("count:later"), "All selected tables bounded and counted before first page");
            Assert(db.Events.IndexOf("bound:shapes") > db.Events.IndexOf("metadata:later"), "All shapes validated before first row bound");
            using (var reader = File.OpenText(path))
            {
                var inserts = MySqlScriptReader.Read(reader, CancellationToken.None).Where(s => s.Sql.StartsWith("INSERT INTO", StringComparison.OrdinalIgnoreCase)).ToList();
                Assert(inserts.Count > 2 && inserts.All(s => Encoding.UTF8.GetByteCount(s.Sql) + 3 <= 8 * 1024 * 1024), "Hex expansion and constructor overhead stay within 8 MiB INSERT cap");
            }
            Assert(db.Queries.Where(sql => sql.Contains(" LIMIT ")).All(sql => sql.Contains(", 16;")), "Materialized source pages remain capped at 16 rows");
        });
    }

    private static void GeometryValidateMetadata(MySqlSqlExportService service, IEnumerable<string> tables, CancellationToken cancellation)
    {
        MethodInfo method = typeof(MySqlSqlExportService).GetMethod("ValidateTableDataCompatibility", BindingFlags.Instance | BindingFlags.Public);
        Assert(method != null, "Exporter must expose metadata-only pre-confirmation validation");
        try { method.Invoke(service, new object[] { tables, cancellation }); }
        catch (TargetInvocationException ex) { throw ex.InnerException; }
    }

    private static MySqlExportWizardOptions GeometryOptions(string path, params string[] tables)
    {
        return new MySqlExportWizardOptions(MySqlExportMode.SchemaAndData, path) { SingleTransaction = false, IncludeViews = false, IncludeRoutines = false, IncludeTriggers = false, SelectedTables = tables.ToList() };
    }

    private static GeometryRecordingDatabase GeometryOneRow(object wkb, object srid)
    {
        var db = new GeometryRecordingDatabase(); var table = db.AddTable("shapes");
        table.Columns.Add(new GeometryTestColumn("shape", "geometry"));
        var row = table.AddRow(); row.Values["shape"] = wkb; row.Srids["shape"] = srid;
        return db;
    }

    private static void GeometryWithDirectory(Action<string, string> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "mysqlpunk_geometry_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { action(directory, Path.Combine(directory, "dump.sql")); }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class GeometryTestColumn
    {
        public string Name, Type;
        public bool Generated;
        public GeometryTestColumn(string name, string type, bool generated = false) { Name = name; Type = type; Generated = generated; }
    }

    private sealed class GeometryTestRow
    {
        public readonly Dictionary<string, object> Values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, object> Srids = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class GeometryTestTable
    {
        public string Name;
        public long MaxRowBytes = 64;
        public readonly List<GeometryTestColumn> Columns = new List<GeometryTestColumn>();
        public readonly List<GeometryTestRow> Rows = new List<GeometryTestRow>();
        public GeometryTestRow AddRow() { var row = new GeometryTestRow(); Rows.Add(row); return row; }
    }

    private sealed class GeometryRecordingDatabase : IDatabase
    {
        public readonly Dictionary<string, GeometryTestTable> Tables = new Dictionary<string, GeometryTestTable>(StringComparer.Ordinal);
        public readonly List<string> Queries = new List<string>(), Executed = new List<string>(), Events = new List<string>();
        public int MetadataReads, CountReads, BoundReads, PageReads;
        public long PageRowBytes;
        public bool ImportMode;
        public Action BeforeSourceScan;
        public ConnectionState State => ConnectionState.Open;
        public string ProviderName => "MySQL";
        public GeometryTestTable AddTable(string name) { var table = new GeometryTestTable { Name = name }; Tables.Add(name, table); return table; }
        public void SetConn(string value) { } public void Open() { } public void Close() { } public void Dispose() { }
        public static List<string> ReadAliases(string sql)
        {
            return Regex.Matches(sql, @"\bAS\s+(?:`(?<quoted>(?:``|[^`])+)`|(?<bare>[A-Za-z0-9_]+))", RegexOptions.IgnoreCase).Cast<Match>().Select(m => m.Groups["quoted"].Success ? m.Groups["quoted"].Value.Replace("``", "`") : m.Groups["bare"].Value).ToList();
        }
        public DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null)
        {
            Queries.Add(sql);
            var table = Tables.Values.FirstOrDefault(t => sql.Contains("." + MySqlImportRunner.QuoteIdentifier(t.Name)));
            if (table == null) throw new InvalidOperationException("Unexpected recording source query");
            var output = new DataTable();
            if (sql.Contains("AS MaxRowBytes"))
            {
                BeforeSourceScan?.Invoke(); BoundReads++; Events.Add("bound:" + table.Name);
                output.Columns.Add("MaxRowBytes", typeof(long)); output.Rows.Add(table.MaxRowBytes); return output;
            }
            Assert(sql.Contains(" LIMIT "), "Exporter must use bounded source pages");
            PageReads++; Events.Add("page:" + table.Name);
            var aliases = ReadAliases(sql); foreach (string alias in aliases) output.Columns.Add(alias, typeof(object));
            var sridAliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in Regex.Matches(sql, @"THEN ST_SRID\(`(?<column>(?:``|[^`])+)`\) ELSE NULL END AS `(?<alias>(?:``|[^`])+)`"))
                sridAliases.Add(match.Groups["alias"].Value.Replace("``", "`"), match.Groups["column"].Value.Replace("``", "`"));
            Match limit = Regex.Match(sql, @"LIMIT\s+(\d+),\s*(\d+)");
            int offset = int.Parse(limit.Groups[1].Value, CultureInfo.InvariantCulture), count = int.Parse(limit.Groups[2].Value, CultureInfo.InvariantCulture);
            foreach (var source in table.Rows.Skip(offset).Take(count))
            {
                DataRow row = output.NewRow();
                foreach (string alias in aliases.Take(aliases.Count - 1))
                {
                    object value;
                    if (sridAliases.ContainsKey(alias)) source.Srids.TryGetValue(sridAliases[alias], out value);
                    else source.Values.TryGetValue(alias, out value);
                    row[alias] = PageRowBytes > 1048576 ? DBNull.Value : value ?? DBNull.Value;
                }
                row[aliases.Last()] = PageRowBytes == 0 ? table.MaxRowBytes : PageRowBytes; output.Rows.Add(row);
            }
            return output;
        }
        public Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null) { Executed.Add(sql); return new Dictionary<string, string> { { "status", "OK" }, { "rowsAffected", sql.TrimStart().StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ? "1" : "0" } }; }
        public Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(SelectSQL(sql, parameters));
        public Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null) => Task.FromResult(ExecSQL(sql, parameters));
        public List<string> GetDatabases() => new List<string> { "geo_source" };
        public List<string> GetTables(string databaseName) => ImportMode ? new List<string>() : Tables.Keys.ToList();
        public List<string> GetViews(string databaseName) => new List<string>();
        public DataTable GetColumns(string databaseName, string tableName)
        {
            MetadataReads++; Events.Add("metadata:" + tableName);
            var output = new DataTable(); foreach (string name in new[] { "Field", "Type", "Extra", "GenerationExpression" }) output.Columns.Add(name);
            foreach (var column in Tables[tableName].Columns) output.Rows.Add(column.Name, column.Type, column.Generated ? "VIRTUAL GENERATED" : "", column.Generated ? "ST_PointFromText('POINT(1 1)')" : "");
            return output;
        }
        public long CountRows(string databaseName, string tableName) { BeforeSourceScan?.Invoke(); CountReads++; Events.Add("count:" + tableName); return Tables[tableName].Rows.Count; }
        public DataTable GetTableStatus(string databaseName) { var output = new DataTable(); output.Columns.Add("Name"); output.Columns.Add("Engine"); foreach (string name in Tables.Keys) output.Rows.Add(name, "InnoDB"); return output; }
        public Dictionary<string, string> GetDatabaseInfo(string databaseName) => new Dictionary<string, string>();
        public string GetTableCreateStatement(string databaseName, string tableName) => "CREATE TABLE " + MySqlImportRunner.QuoteIdentifier(tableName) + " (" + string.Join(", ", Tables[tableName].Columns.Select(c => MySqlImportRunner.QuoteIdentifier(c.Name) + " " + c.Type + (c.Generated ? " GENERATED ALWAYS AS (ST_PointFromText('POINT(1 1)')) VIRTUAL" : ""))) + ") ENGINE=InnoDB;";
        public DataTable GetIndexes(string databaseName, string tableName) => new DataTable();
        public bool TableExists(string databaseName, string tableName) => !ImportMode && Tables.ContainsKey(tableName);
        public bool ViewExists(string databaseName, string viewName) => false;
        public void RenameTable(string a, string b, string c) { } public void RenameView(string a, string b, string c) { }
        public DataTable GetCopyColumns(string a, string b) => new DataTable(); public DataTable GetCopyIndexes(string a, string b) => new DataTable();
        public void CreateTableForCopy(string a, string b, DataTable c, string d) { } public void DropTableForCopy(string a, string b) { }
        public void CreateIndexesForCopy(string a, string b, DataTable c, string d) { }
        public DataTable SelectTablePage(string a, string b, long c, int d) => throw new NotSupportedException("Unbounded table-page helper must not be used");
        public void InsertTableBatch(string a, string b, DataTable c) { } public string GetViewCreateStatement(string a, string b) => "";
        public void CreateViewFromStatement(string a, string b, string c) { }
    }
}
