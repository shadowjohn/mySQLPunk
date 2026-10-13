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

    internal enum MssqlPrerequisite { ViewDefinitionRequired, SqlPermissionDenied, LoginRejected, DatabaseUnavailable }

    // Only fixed categories cross the provider boundary; never retain provider text or inner exceptions.
    internal sealed class MssqlExportPrerequisiteException : InvalidOperationException
    {
        public MssqlPrerequisite Reason { get; }
        internal MssqlExportPrerequisiteException(MssqlPrerequisite reason) : base(Describe(reason)) { Reason = reason; }
        private static string Describe(MssqlPrerequisite reason)
        {
            switch (reason) {
                case MssqlPrerequisite.ViewDefinitionRequired: return "[ViewDefinitionRequired] Database VIEW DEFINITION is required to inspect complete object definitions and dependencies. Ask the DBA to review definition visibility, then retry.";
                case MssqlPrerequisite.SqlPermissionDenied: return "[SqlPermissionDenied] SQL Server denied a required operation. Ask the DBA to review definition-read permissions and, for data export, table SELECT permissions, then retry.";
                case MssqlPrerequisite.LoginRejected: return "[LoginRejected] SQL Server rejected the login. Review the connection account and authentication configuration, then retry.";
                case MssqlPrerequisite.DatabaseUnavailable: return "[DatabaseUnavailable] The requested or default database is unavailable or access was rejected. Review the database name, availability and account access, then retry.";
                default: throw new ArgumentOutOfRangeException(nameof(reason));
            }
        }
        internal static MssqlExportPrerequisiteException From(Exception error)
        {
            var known = error as MssqlExportPrerequisiteException;
            if (known != null) return known;
            var sql = error as System.Data.SqlClient.SqlException;
            if (sql == null) return null;
            // The first SqlError may be a general wrapper; inspect every entry without reading messages.
            var numbers = new HashSet<int>(sql.Errors.Cast<System.Data.SqlClient.SqlError>().Select(x => x.Number));
            if (numbers.Contains(18456) || numbers.Contains(18452)) return new MssqlExportPrerequisiteException(MssqlPrerequisite.LoginRejected);
            if (numbers.Contains(916) || numbers.Contains(4060) || numbers.Contains(4064)) return new MssqlExportPrerequisiteException(MssqlPrerequisite.DatabaseUnavailable);
            if (numbers.Contains(229) || numbers.Contains(230) || numbers.Contains(297) || numbers.Contains(300)) return new MssqlExportPrerequisiteException(MssqlPrerequisite.SqlPermissionDenied);
            return null;
        }
    }

    internal static class MssqlSnapshotState
    {
        internal static string Describe(int state)
        {
            string name = state == 0 ? "OFF" : state == 1 ? "ON" : state == 2 ? "IN_TRANSITION_TO_OFF" : state == 3 ? "IN_TRANSITION_TO_ON" : "UNKNOWN";
            return name + " (" + state.ToString(CultureInfo.InvariantCulture) + ")";
        }
    }

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

    // Null selection and null seed mean database scope. An explicit empty or unresolved
    // selection never broadens to all tables. Raw seeds use the existing provider tree
    // representation (dbo => name, other schemas => schema.name), not SQL parsing.
    internal sealed class MssqlExportWizardOptions
    {
        public MssqlExportMode Mode { get; }
        public IReadOnlyList<MssqlObjectKey> InitialTables { get; }
        public string InitialObjectName { get; }
        public bool IncludeViews { get; }
        public string DefaultFileName { get; }
        public MssqlMirrorOptions Mirror { get; }
        internal MssqlExportWizardOptions(MssqlExportMode mode, IReadOnlyList<MssqlObjectKey> initialTables = null, bool includeViews = true, string defaultFileName = null, MssqlMirrorOptions mirror = null, string initialObjectName = null)
        {
            if (!Enum.IsDefined(typeof(MssqlExportMode), mode)) throw new ArgumentOutOfRangeException(nameof(mode));
            Mode = mode; InitialTables = initialTables == null ? null : new List<MssqlObjectKey>(initialTables).AsReadOnly();
            if (InitialTables != null && InitialTables.Any(x => x == null)) throw new ArgumentException("Null initial table", nameof(initialTables));
            InitialObjectName = initialObjectName; IncludeViews = includeViews; DefaultFileName = defaultFileName; Mirror = mirror;
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
        public bool UseSnapshot { get; }
        public MssqlExportRequest(Guid jobId, string databaseName, IReadOnlyList<MssqlObjectKey> tables, MssqlExportMode mode, bool includeViews, string targetPath, ExportDestinationStamp destination, MssqlMirrorOptions mirror, bool useSnapshot = false)
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
            if (mode == MssqlExportMode.SchemaOnly && useSnapshot) throw new ArgumentException("Schema-only export cannot use snapshot", nameof(useSnapshot));
            Destination = destination; Mirror = mirror; UseSnapshot = useSnapshot;
        }
    }

    internal static class MssqlExportLimits
    {
        public const int ProgressLogLimit = 200;
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
        public string PreparedDefinition => Text("prepared_definition");
        public bool HasPreparedReferences => Flag("prepared_same_database_references") && !string.IsNullOrEmpty(PreparedDefinition);
        public bool AnsiNulls => Flag("uses_ansi_nulls");
        public bool QuotedIdentifier => Flag("uses_quoted_identifier");
    }
    // SQL variant metadata is stored as immutable scalar facts and native value hex.
    // A byte[] fact would be mutable and would not be represented by Canonical().
    internal sealed class MssqlExtendedPropertyDefinition : MssqlNativeFacts
    {
        public MssqlObjectKey Owner { get; }
        public string OwnerType { get; }
        internal MssqlExtendedPropertyDefinition(MssqlObjectKey owner, string ownerType, IDictionary<string, object> facts) : base(facts)
        { Owner = owner ?? throw new ArgumentNullException(nameof(owner)); OwnerType = ownerType; }
        public int MinorId => Number("minor_id");
        public string ColumnName => Text("column_name");
        public string Name => Text("name");
        public string BaseType => Text("base_type");
        public int? MaxLengthBytes => Text("max_length") == null ? (int?)null : Number("max_length");
        public string Collation => Text("collation");
        public bool IsNull => Flag("is_null");
        public string ValueHex => Text("value_hex");
    }
    internal sealed class MssqlDependency
    {
        public int ObjectId { get; }
        public int? ReferencedId { get; }
        public string Server { get; }
        public string Database { get; }
        public string Schema { get; }
        public string Name { get; }
        public int? SourceDatabaseId { get; }
        public int? ReferencedDatabaseId { get; }
        public int? ResolvedLocalId { get; }
        public bool IsAmbiguous { get; }
        public bool IsCallerDependent { get; }
        public bool PreparedSameDatabaseReference { get; }
        public bool HasSameDatabaseIdentity => Server == null && !string.IsNullOrEmpty(Database) && !string.IsNullOrEmpty(Schema) &&
            SourceDatabaseId.HasValue && SourceDatabaseId.Value > 0 && SourceDatabaseId == ReferencedDatabaseId &&
            ResolvedLocalId.HasValue && ResolvedLocalId.Value > 0 && (!ReferencedId.HasValue || ReferencedId == ResolvedLocalId) &&
            !IsAmbiguous && !IsCallerDependent;
        public int? EffectiveReferencedId => Server == null && Database == null && !IsAmbiguous && !IsCallerDependent ? ReferencedId :
            PreparedSameDatabaseReference && HasSameDatabaseIdentity ? ResolvedLocalId : null;
        internal MssqlDependency(int objectId, int? referencedId, string server, string database, string schema, string name,
            int? sourceDatabaseId = null, int? referencedDatabaseId = null, int? resolvedLocalId = null,
            bool isAmbiguous = false, bool isCallerDependent = false, bool preparedSameDatabaseReference = false)
        {
            ObjectId = objectId; ReferencedId = referencedId; Server = server; Database = database; Schema = schema; Name = name;
            SourceDatabaseId = sourceDatabaseId; ReferencedDatabaseId = referencedDatabaseId; ResolvedLocalId = resolvedLocalId;
            IsAmbiguous = isAmbiguous; IsCallerDependent = isCallerDependent; PreparedSameDatabaseReference = preparedSameDatabaseReference;
        }
        internal MssqlDependency WithPreparedSameDatabaseReference() => new MssqlDependency(ObjectId, ReferencedId, Server, Database, Schema, Name,
            SourceDatabaseId, ReferencedDatabaseId, ResolvedLocalId, IsAmbiguous, IsCallerDependent, true);
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
        public MssqlSnapshotCapability SnapshotCapability { get; }
        public IReadOnlyList<MssqlExportIssue> Issues { get; }
        internal MssqlExportCatalog(IEnumerable<MssqlTableDefinition> tables, int snapshotState, IEnumerable<MssqlExportIssue> issues, MssqlSnapshotCapability snapshotCapability = null)
        { Tables = MssqlNativeFacts.Copy(tables); SnapshotState = snapshotState; SnapshotCapability = snapshotCapability;
            if (snapshotCapability != null && snapshotCapability.State != snapshotState) throw new ArgumentException("Snapshot capability state mismatch", nameof(snapshotCapability)); Issues = MssqlNativeFacts.Copy(issues); }
    }
    internal sealed class MssqlExportSchema
    {
        public IReadOnlyList<MssqlTableDefinition> Tables { get; }
        public IReadOnlyList<MssqlViewDefinition> Views { get; }
        public IReadOnlyList<MssqlDependency> Dependencies { get; }
        public IReadOnlyList<MssqlExtendedPropertyDefinition> ExtendedProperties { get; }
        public IReadOnlyList<MssqlExportIssue> Issues { get; }
        public int SnapshotState { get; }
        public MssqlSnapshotCapability SnapshotCapability { get; }
        public string Fingerprint { get; }
        internal MssqlExportSchema(IEnumerable<MssqlTableDefinition> tables, IEnumerable<MssqlViewDefinition> views, IEnumerable<MssqlDependency> dependencies, int snapshotState, IEnumerable<MssqlExportIssue> issues, IEnumerable<MssqlExtendedPropertyDefinition> extendedProperties = null, MssqlSnapshotCapability snapshotCapability = null)
        {
            ExtendedProperties = MssqlNativeFacts.Copy(extendedProperties);
            Tables = MssqlNativeFacts.Copy(tables); Views = MssqlNativeFacts.Copy(views); Dependencies = MssqlNativeFacts.Copy(dependencies); Issues = MssqlNativeFacts.Copy(issues); SnapshotState = snapshotState; SnapshotCapability = snapshotCapability;
            if (snapshotCapability != null && snapshotCapability.State != snapshotState) throw new ArgumentException("Snapshot capability state mismatch", nameof(snapshotCapability));
            var text = new StringBuilder();
            foreach (var t in Tables.OrderBy(x => x.Key.ObjectId))
            {
                Key(text, t.Key); t.Canonical(text);
                foreach (var c in t.Columns.OrderBy(x => x.Ordinal)) c.Canonical(text);
                foreach (var i in t.Indexes.OrderBy(x => x.IndexId)) { i.Canonical(text); foreach (var c in i.Columns) c.Canonical(text); }
                foreach (var c in t.Constraints.OrderBy(x => x.Name, StringComparer.Ordinal)) { c.Canonical(text); foreach (var column in c.Columns) column.Canonical(text); }
            }
            foreach (var v in Views.OrderBy(x => x.Key.ObjectId)) { Key(text, v.Key); v.Canonical(text); }
            foreach (var d in Dependencies.OrderBy(x => x.ObjectId).ThenBy(x => x.ReferencedId).ThenBy(x => x.Server, StringComparer.Ordinal)
                .ThenBy(x => x.Database, StringComparer.Ordinal).ThenBy(x => x.Schema, StringComparer.Ordinal).ThenBy(x => x.Name, StringComparer.Ordinal)
                .ThenBy(x => x.SourceDatabaseId).ThenBy(x => x.ReferencedDatabaseId).ThenBy(x => x.ResolvedLocalId)
                .ThenBy(x => x.IsAmbiguous).ThenBy(x => x.IsCallerDependent).ThenBy(x => x.PreparedSameDatabaseReference))
            {
                MssqlNativeFacts.Part(text, d.ObjectId.ToString(CultureInfo.InvariantCulture)); MssqlNativeFacts.Part(text, d.ReferencedId?.ToString(CultureInfo.InvariantCulture));
                MssqlNativeFacts.Part(text, d.Server); MssqlNativeFacts.Part(text, d.Database); MssqlNativeFacts.Part(text, d.Schema); MssqlNativeFacts.Part(text, d.Name);
                MssqlNativeFacts.Part(text, d.SourceDatabaseId?.ToString(CultureInfo.InvariantCulture)); MssqlNativeFacts.Part(text, d.ReferencedDatabaseId?.ToString(CultureInfo.InvariantCulture));
                MssqlNativeFacts.Part(text, d.ResolvedLocalId?.ToString(CultureInfo.InvariantCulture)); MssqlNativeFacts.Part(text, d.IsAmbiguous.ToString());
                MssqlNativeFacts.Part(text, d.IsCallerDependent.ToString()); MssqlNativeFacts.Part(text, d.PreparedSameDatabaseReference.ToString());
            }
            foreach (var p in ExtendedProperties.OrderBy(x => x.Owner.ObjectId).ThenBy(x => x.MinorId).ThenBy(x => x.Name, StringComparer.Ordinal))
            { Key(text, p.Owner); MssqlNativeFacts.Part(text, p.OwnerType); p.Canonical(text); }
            using (var sha = SHA256.Create()) Fingerprint = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        }
        private static void Key(StringBuilder text, MssqlObjectKey key) { MssqlNativeFacts.Part(text, key.ObjectId.ToString(CultureInfo.InvariantCulture)); MssqlNativeFacts.Part(text, key.Schema); MssqlNativeFacts.Part(text, key.Name); }
    }
    internal sealed class MssqlExportPreflight
    {
        public MssqlExportSchema Schema { get; }
        public int SnapshotState => Schema.SnapshotState;
        public MssqlSnapshotCapability SnapshotCapability => Schema.SnapshotCapability;
        public IReadOnlyList<MssqlExportIssue> Issues { get; }
        public bool CanStart => Issues.Count == 0;
        internal MssqlExportPreflight(MssqlExportSchema schema, IEnumerable<MssqlExportIssue> issues) { Schema = schema; Issues = MssqlNativeFacts.Copy(issues); }
    }
    internal sealed class MssqlTableRowCount
    {
        public MssqlObjectKey Key { get; }
        public long Rows { get; }
        public MssqlTableRowCount(MssqlObjectKey key, long rows)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (rows < 0) throw new ArgumentOutOfRangeException(nameof(rows));
            Key = key; Rows = rows;
        }
    }
    internal sealed class MssqlArtifactReceipt
    {
        public Guid JobId { get; }
        public string TemporaryPath { get; }
        public string IntendedTargetPath { get; }
        public long Bytes { get; }
        public string Sha256 { get; }
        public IReadOnlyList<MssqlTableRowCount> Tables { get; }
        public long TotalRows { get; }
        public string CompletionMarker { get; }
        internal MssqlArtifactReceipt(Guid jobId, string temporaryPath, string publishedPath, long bytes, string sha256, IReadOnlyList<MssqlTableRowCount> tables, string marker)
        {
            JobId = jobId; TemporaryPath = temporaryPath; IntendedTargetPath = publishedPath; Bytes = bytes; Sha256 = sha256;
            Tables = new List<MssqlTableRowCount>(tables).AsReadOnly(); CompletionMarker = marker;
            long total = 0; foreach (var table in Tables) total = checked(total + table.Rows); TotalRows = total;
        }
    }

    internal enum MssqlExportStage { Preflight, Schema, Data, Verifying, Publishing, Mirroring, CancelRequested, Completed, Canceled, Failed, LocalCompletedMirrorIncomplete, EnablingSnapshot, RestoringSnapshot }
    internal enum MssqlExportEstimationState { Estimating, Measuring, Waiting, Unreliable, Complete }
    internal sealed class MssqlExportProgress
    {
        public Guid JobId { get; }
        public MssqlExportStage Stage { get; }
        public MssqlObjectKey CurrentTable { get; }
        public long CurrentTableRows { get; }
        public long CurrentTableSourceBytes { get; }
        public long Rows { get; }
        public long SourceBytes { get; }
        public long WrittenBytes { get; }
        public long? EstimatedRows { get; }
        public long? EstimatedBytes { get; }
        public int CompletedTables { get; }
        public int TotalTables { get; }
        public TimeSpan Elapsed { get; }
        public TimeSpan LastProgressElapsed { get; }
        // Source bytes/s during Data; processed local phase bytes/s during verification/mirroring.
        public double? BytesPerSecond { get; }
        public TimeSpan? Eta { get; }
        public double? Percent { get; }
        public MssqlExportEstimationState EstimationState { get; }
        public long PhaseProcessedBytes { get; }
        public long? PhaseTotalBytes { get; }
        public string Message { get; }
        internal MssqlExportProgress(Guid jobId, MssqlExportStage stage, MssqlObjectKey table, long tableRows, long tableBytes,
            long rows, long sourceBytes, long writtenBytes, long? estimatedRows, long? estimatedBytes, int completed, int total,
            TimeSpan elapsed, TimeSpan lastProgressElapsed, double? bytesPerSecond, TimeSpan? eta, double? percent, MssqlExportEstimationState estimation, long phaseBytes, long? phaseTotal, string message)
        {
            JobId = jobId; Stage = stage; CurrentTable = table; CurrentTableRows = tableRows; CurrentTableSourceBytes = tableBytes;
            Rows = rows; SourceBytes = sourceBytes; WrittenBytes = writtenBytes; EstimatedRows = estimatedRows; EstimatedBytes = estimatedBytes;
            CompletedTables = completed; TotalTables = total; Elapsed = elapsed; LastProgressElapsed = lastProgressElapsed; BytesPerSecond = bytesPerSecond; Eta = eta; Percent = percent; EstimationState = estimation;
            PhaseProcessedBytes = phaseBytes; PhaseTotalBytes = phaseTotal; Message = (message ?? string.Empty).Substring(0, Math.Min(512, (message ?? string.Empty).Length));
        }
    }
    internal sealed class MssqlExportResult
    {
        public Guid JobId { get; }
        public MssqlExportStage Stage { get; }
        public string PublishedPath { get; }
        public string MirrorPath { get; }
        public IReadOnlyList<MssqlTableRowCount> RowCounts { get; }
        public TimeSpan Elapsed { get; }
        public IReadOnlyList<string> Warnings { get; }
        public IReadOnlyList<string> ResidualPaths { get; }
        public string ErrorMessage { get; }
        public bool UseSnapshot { get; }
        public MssqlSnapshotCleanupResult SnapshotCleanup { get; }
        public MssqlSnapshotStartRejection StartRejection { get; }
        public MssqlSnapshotCapability StartCapability { get; }
        internal MssqlExportResult(Guid jobId, MssqlExportStage stage, string publishedPath, string mirrorPath,
            IEnumerable<MssqlTableRowCount> rows, TimeSpan elapsed, IEnumerable<string> warnings, IEnumerable<string> residuals, string errorMessage, bool useSnapshot = false, MssqlSnapshotCleanupResult snapshotCleanup = null,
            MssqlSnapshotStartRejection startRejection = MssqlSnapshotStartRejection.None, MssqlSnapshotCapability startCapability = null)
        {
            JobId = jobId; Stage = stage; PublishedPath = publishedPath; MirrorPath = mirrorPath; RowCounts = MssqlNativeFacts.Copy(rows);
            Elapsed = elapsed; Warnings = MssqlNativeFacts.Copy(warnings); ResidualPaths = MssqlNativeFacts.Copy(residuals); ErrorMessage = errorMessage; UseSnapshot = useSnapshot; SnapshotCleanup = snapshotCleanup;
            if (!Enum.IsDefined(typeof(MssqlSnapshotStartRejection), startRejection)) throw new ArgumentOutOfRangeException(nameof(startRejection));
            StartRejection = startRejection; StartCapability = startCapability;
        }
    }

}
