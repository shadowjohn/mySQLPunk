using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;

namespace mySQLPunk.lib.MssqlExport
{
    internal enum MssqlExportMode { SchemaAndData, SchemaOnly, DataOnly }

    internal sealed class MssqlObjectKey
    {
        public int ObjectId { get; }
        public string Schema { get; }
        public string Name { get; }
        public MssqlObjectKey(int objectId, string schema, string name)
        {
            if (objectId <= 0) throw new ArgumentOutOfRangeException(nameof(objectId));
            if (string.IsNullOrWhiteSpace(schema)) throw new ArgumentException("Schema is required", nameof(schema));
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Name is required", nameof(name));
            ObjectId = objectId; Schema = schema; Name = name;
        }
    }

    internal sealed class MssqlMirrorOptions
    {
        public string Directory { get; }
        public int RetainCount { get; }
        public MssqlMirrorOptions(string directory, int retainCount)
        {
            if (string.IsNullOrWhiteSpace(directory)) throw new ArgumentException("Mirror directory is required", nameof(directory));
            if (retainCount < 1) throw new ArgumentOutOfRangeException(nameof(retainCount));
            Directory = Path.GetFullPath(directory); RetainCount = retainCount;
        }
    }

    // Captured destination facts; capture/revalidation belongs to the publication task.
    internal sealed class ExportDestinationStamp
    {
        public bool Exists { get; }
        public string FullPath { get; }
        public string FileIdentity { get; }
        public long Length { get; }
        public DateTime LastWriteTimeUtc { get; }
        public string Sha256 { get; }
        public ExportDestinationStamp(bool exists, string fullPath, string fileIdentity, long length, DateTime lastWriteTimeUtc, string sha256)
        {
            if (string.IsNullOrWhiteSpace(fullPath)) throw new ArgumentException("Destination path is required", nameof(fullPath));
            if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
            Exists = exists; FullPath = Path.GetFullPath(fullPath); FileIdentity = fileIdentity;
            Length = length; LastWriteTimeUtc = lastWriteTimeUtc; Sha256 = sha256;
        }
    }

    internal sealed class MssqlExportRequest
    {
        public Guid JobId { get; }
        public string DatabaseName { get; }
        public IReadOnlyList<MssqlObjectKey> Tables { get; }
        public MssqlExportMode Mode { get; }
        public bool IncludeViews { get; }
        public string TargetPath { get; }
        public ExportDestinationStamp Destination { get; }
        public MssqlMirrorOptions Mirror { get; }
        public MssqlExportRequest(Guid jobId, string databaseName, IReadOnlyList<MssqlObjectKey> tables, MssqlExportMode mode, bool includeViews, string targetPath, ExportDestinationStamp destination, MssqlMirrorOptions mirror)
        {
            if (jobId == Guid.Empty) throw new ArgumentException("Job identity is required", nameof(jobId));
            if (string.IsNullOrWhiteSpace(databaseName)) throw new ArgumentException("Database is required", nameof(databaseName));
            if (tables == null || tables.Count == 0) throw new ArgumentException("Select at least one table", nameof(tables));
            if (!Enum.IsDefined(typeof(MssqlExportMode), mode)) throw new ArgumentException("Unknown export mode", nameof(mode));
            if (mode == MssqlExportMode.DataOnly && includeViews) throw new ArgumentException("Data-only export cannot include views", nameof(includeViews));
            if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("Output path is required", nameof(targetPath));
            var selection = new List<MssqlObjectKey>(tables);
            if (selection.Contains(null)) throw new ArgumentException("Table selection contains null", nameof(tables));
            JobId = jobId; DatabaseName = databaseName; Tables = selection.AsReadOnly();
            Mode = mode; IncludeViews = includeViews; TargetPath = Path.GetFullPath(targetPath);
            Destination = destination; Mirror = mirror;
        }
    }

    internal static class MssqlExportLimits
    {
        public const int SmallRowBytes = 262144;
        public const int StatementBytes = 262144;
        public const int BatchBytes = 4194304;
        public const int BinaryChunkBytes = 65536;
        public const int TextChunkCodeUnits = 16384;
    }
    // Native catalog facts are copied once. Extra SQL Server properties stay accessible
    // without lossy cross-provider type mapping; writers use the typed accessors below.
    internal class MssqlNativeFacts
    {
        public IReadOnlyDictionary<string, object> Facts { get; }
        internal MssqlNativeFacts(IDictionary<string, object> facts)
        {
            Facts = new ReadOnlyDictionary<string, object>(new Dictionary<string, object>(facts ?? new Dictionary<string, object>(), StringComparer.Ordinal));
        }
        public string Text(string name) { object value; return Facts.TryGetValue(name, out value) && value != null && value != DBNull.Value ? Convert.ToString(value, CultureInfo.InvariantCulture) : null; }
        public int Number(string name) { var value = Text(name); return value == null || value == "False" ? 0 : value == "True" ? 1 : int.Parse(value, CultureInfo.InvariantCulture); }
        public bool Flag(string name) { var value = Text(name); return value == "1" || string.Equals(value, "True", StringComparison.OrdinalIgnoreCase); }
        public long? Estimate(string name) { var value = Text(name); return value == null ? (long?)null : long.Parse(value, CultureInfo.InvariantCulture); }
        internal void Canonical(StringBuilder output)
        {
            foreach (var fact in Facts.Where(x => x.Key != "estimated_rows" && x.Key != "estimated_bytes").OrderBy(x => x.Key, StringComparer.Ordinal))
            { Part(output, fact.Key); Part(output, Text(fact.Key)); }
        }
        internal static void Part(StringBuilder output, string text) { output.Append(text == null ? -1 : text.Length).Append(':').Append(text); }
        internal static IReadOnlyList<T> Copy<T>(IEnumerable<T> values) { return new List<T>(values ?? Enumerable.Empty<T>()).AsReadOnly(); }
    }
    internal sealed class MssqlColumnDefinition : MssqlNativeFacts
    {
        internal MssqlColumnDefinition(IDictionary<string, object> facts) : base(facts) { }
        public int Ordinal => Number("column_id");
        public string Name => Text("name");
        public string NativeType => Text("type_name");
        public string TypeSchema => Text("type_schema");
        public int Length => Number("max_length");
        public int Precision => Number("precision");
        public int Scale => Number("scale");
        public string Collation => Text("collation_name");
        public bool Nullable => Flag("is_nullable");
        public bool Identity => Flag("is_identity");
        public string IdentitySeed => Text("seed_value");
        public string IdentityIncrement => Text("increment_value");
        public bool Computed => Flag("is_computed");
        public string ComputedDefinition => Text("computed_definition");
        public bool Persisted => Flag("is_persisted");
        public bool RowVersion => NativeType == "timestamp" || NativeType == "rowversion";
        public string DefaultName => Text("default_name");
        public string DefaultDefinition => Text("default_definition");
        public string Comment => Text("comment");
    }
    internal sealed class MssqlIndexDefinition : MssqlNativeFacts
    {
        public IReadOnlyList<MssqlNativeFacts> Columns { get; }
        internal MssqlIndexDefinition(IDictionary<string, object> facts, IEnumerable<MssqlNativeFacts> columns) : base(facts) { Columns = Copy(columns); }
        public string Name => Text("name");
        public int IndexId => Number("index_id");
    }
    internal sealed class MssqlConstraintDefinition : MssqlNativeFacts
    {
        public IReadOnlyList<MssqlNativeFacts> Columns { get; }
        internal MssqlConstraintDefinition(IDictionary<string, object> facts, IEnumerable<MssqlNativeFacts> columns) : base(facts) { Columns = Copy(columns); }
        public string Name => Text("name");
        public string Kind => Text("kind");
    }
    internal sealed class MssqlTableDefinition : MssqlNativeFacts
    {
        public MssqlObjectKey Key { get; }
        public IReadOnlyList<MssqlColumnDefinition> Columns { get; }
        public IReadOnlyList<MssqlIndexDefinition> Indexes { get; }
        public IReadOnlyList<MssqlConstraintDefinition> Constraints { get; }
        internal MssqlTableDefinition(MssqlObjectKey key, IDictionary<string, object> facts, IEnumerable<MssqlColumnDefinition> columns, IEnumerable<MssqlIndexDefinition> indexes, IEnumerable<MssqlConstraintDefinition> constraints) : base(facts)
        { Key = key; Columns = Copy(columns); Indexes = Copy(indexes); Constraints = Copy(constraints); }
        public string Comment => Text("comment");
        public long? EstimatedRows => Estimate("estimated_rows");
        public long? EstimatedBytes => Estimate("estimated_bytes");
    }
    internal sealed class MssqlViewDefinition : MssqlNativeFacts
    {
        public MssqlObjectKey Key { get; }
        internal MssqlViewDefinition(MssqlObjectKey key, IDictionary<string, object> facts) : base(facts) { Key = key; }
        public string Definition => Text("definition");
        public bool AnsiNulls => Flag("uses_ansi_nulls");
        public bool QuotedIdentifier => Flag("uses_quoted_identifier");
    }
    internal sealed class MssqlDependency
    {
        public int ObjectId { get; }
        public int? ReferencedId { get; }
        public string Server { get; }
        public string Database { get; }
        public string Schema { get; }
        public string Name { get; }
        internal MssqlDependency(int objectId, int? referencedId, string server, string database, string schema, string name)
        { ObjectId = objectId; ReferencedId = referencedId; Server = server; Database = database; Schema = schema; Name = name; }
    }
    internal sealed class MssqlExportIssue
    {
        public string Code { get; }
        public MssqlObjectKey ObjectKey { get; }
        public string ColumnName { get; }
        public string Message { get; }
        internal MssqlExportIssue(string code, MssqlObjectKey key, string column, string message) { Code = code; ObjectKey = key; ColumnName = column; Message = message; }
    }
    internal sealed class MssqlExportCatalog
    {
        public IReadOnlyList<MssqlTableDefinition> Tables { get; }
        public int SnapshotState { get; }
        public IReadOnlyList<MssqlExportIssue> Issues { get; }
        internal MssqlExportCatalog(IEnumerable<MssqlTableDefinition> tables, int snapshotState, IEnumerable<MssqlExportIssue> issues)
        { Tables = MssqlNativeFacts.Copy(tables); SnapshotState = snapshotState; Issues = MssqlNativeFacts.Copy(issues); }
    }
    internal sealed class MssqlExportSchema
    {
        public IReadOnlyList<MssqlTableDefinition> Tables { get; }
        public IReadOnlyList<MssqlViewDefinition> Views { get; }
        public IReadOnlyList<MssqlDependency> Dependencies { get; }
        public IReadOnlyList<MssqlExportIssue> Issues { get; }
        public int SnapshotState { get; }
        public string Fingerprint { get; }
        internal MssqlExportSchema(IEnumerable<MssqlTableDefinition> tables, IEnumerable<MssqlViewDefinition> views, IEnumerable<MssqlDependency> dependencies, int snapshotState, IEnumerable<MssqlExportIssue> issues)
        {
            Tables = MssqlNativeFacts.Copy(tables); Views = MssqlNativeFacts.Copy(views); Dependencies = MssqlNativeFacts.Copy(dependencies); Issues = MssqlNativeFacts.Copy(issues); SnapshotState = snapshotState;
            var text = new StringBuilder();
            foreach (var t in Tables.OrderBy(x => x.Key.ObjectId))
            {
                Key(text, t.Key); t.Canonical(text);
                foreach (var c in t.Columns.OrderBy(x => x.Ordinal)) c.Canonical(text);
                foreach (var i in t.Indexes.OrderBy(x => x.IndexId)) { i.Canonical(text); foreach (var c in i.Columns) c.Canonical(text); }
                foreach (var c in t.Constraints.OrderBy(x => x.Name, StringComparer.Ordinal)) { c.Canonical(text); foreach (var column in c.Columns) column.Canonical(text); }
            }
            foreach (var v in Views.OrderBy(x => x.Key.ObjectId)) { Key(text, v.Key); v.Canonical(text); }
            foreach (var d in Dependencies.OrderBy(x => x.ObjectId).ThenBy(x => x.ReferencedId).ThenBy(x => x.Name, StringComparer.Ordinal))
            { MssqlNativeFacts.Part(text, d.ObjectId.ToString(CultureInfo.InvariantCulture)); MssqlNativeFacts.Part(text, d.ReferencedId?.ToString(CultureInfo.InvariantCulture)); MssqlNativeFacts.Part(text, d.Server); MssqlNativeFacts.Part(text, d.Database); MssqlNativeFacts.Part(text, d.Schema); MssqlNativeFacts.Part(text, d.Name); }
            using (var sha = SHA256.Create()) Fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        }
        private static void Key(StringBuilder text, MssqlObjectKey key) { MssqlNativeFacts.Part(text, key.ObjectId.ToString(CultureInfo.InvariantCulture)); MssqlNativeFacts.Part(text, key.Schema); MssqlNativeFacts.Part(text, key.Name); }
    }
    internal sealed class MssqlExportPreflight
    {
        public MssqlExportSchema Schema { get; }
        public int SnapshotState => Schema.SnapshotState;
        public IReadOnlyList<MssqlExportIssue> Issues { get; }
        public bool CanStart => Issues.Count == 0;
        internal MssqlExportPreflight(MssqlExportSchema schema, IEnumerable<MssqlExportIssue> issues) { Schema = schema; Issues = MssqlNativeFacts.Copy(issues); }
    }

}
