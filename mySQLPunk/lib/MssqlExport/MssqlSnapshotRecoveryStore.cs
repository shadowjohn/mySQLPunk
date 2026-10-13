using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
namespace mySQLPunk.lib.MssqlExport
{
    internal interface IMssqlSnapshotRecoveryStore
    {
        string Create(MssqlSnapshotRecoveryRecord record);
        void Update(MssqlSnapshotRecoveryRecord record);
        void RemoveConfirmed(MssqlSnapshotRecoveryRecord expected);
        IReadOnlyList<MssqlSnapshotRecoveryNotice> ReadPending();
    }
    // The hook is a test seam at durable-write / publication / deletion boundaries.
    internal sealed class MssqlSnapshotRecoveryStore : IMssqlSnapshotRecoveryStore
    {
        private sealed class UnsafeRecoveryPathException : IOException { }
        private readonly string root;
        private readonly Action<string> fault;
        internal MssqlSnapshotRecoveryStore(string root = null, Action<string> ioFault = null)
        {
            this.root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "mySQLPunk", "ExportRecovery", "snapshot"));
            fault = ioFault;
        }
        private static void CheckPath(string path)
        {
            for (string current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new UnsafeRecoveryPathException();
        }
        private string RecordPath(Guid job) => Path.Combine(root, job.ToString("N") + ".json");
        private static bool Same(MssqlSnapshotRecoveryRecord a, MssqlSnapshotRecoveryRecord b) => a.JobId == b.JobId && a.OriginalState == b.OriginalState &&
            a.Identity.ServerName == b.Identity.ServerName && a.Identity.DatabaseName == b.Identity.DatabaseName && a.Identity.DatabaseId == b.Identity.DatabaseId && a.Identity.DatabaseCreateDate == b.Identity.DatabaseCreateDate;
        private static void Validate(MssqlSnapshotRecoveryRecord r)
        {
            if (r == null || r.JobId == Guid.Empty || r.Identity == null || !r.Identity.IsComplete || r.Identity.ServerName.Length > 256 || r.Identity.DatabaseName.Length > 128 ||
                (r.OriginalState != 0 && r.OriginalState != 1) || (r.ObservedState.HasValue && (r.ObservedState < 0 || r.ObservedState > 3)) ||
                !Enum.IsDefined(typeof(MssqlSnapshotRecoveryPhase), r.Phase) || !Enum.IsDefined(typeof(MssqlSnapshotRecoveryCode), r.NoticeCode) || r.UpdatedUtc.Kind != DateTimeKind.Utc)
                throw new ArgumentException("Invalid recovery record.");
        }
        private static JObject Encode(MssqlSnapshotRecoveryRecord r) => new JObject {
            { "version", 1 }, { "jobId", r.JobId.ToString("N") }, { "serverName", r.Identity.ServerName }, { "databaseName", r.Identity.DatabaseName },
            { "databaseId", r.Identity.DatabaseId }, { "databaseCreateDate", r.Identity.DatabaseCreateDate.ToString("O") }, { "originalState", r.OriginalState },
            { "phase", (int)r.Phase }, { "observedState", r.ObservedState.HasValue ? new JValue(r.ObservedState.Value) : JValue.CreateNull() },
            { "updatedUtc", r.UpdatedUtc.ToString("O") }, { "noticeCode", (int)r.NoticeCode } };
        public string Create(MssqlSnapshotRecoveryRecord record) { Write(record, false); return RecordPath(record.JobId); }
        public void Update(MssqlSnapshotRecoveryRecord record) { Write(record, true); }
        private void Write(MssqlSnapshotRecoveryRecord record, bool update)
        {
            Validate(record); CheckPath(root); Directory.CreateDirectory(root); CheckPath(root);
            string path = RecordPath(record.JobId);
            CheckPath(path);
            if (update)
            {
                var existing = ReadOne(path);
                if (existing.Record == null || !Same(existing.Record, record)) throw new IOException("Recovery record ownership mismatch.");
            }
            string temp = Path.Combine(root, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = new UTF8Encoding(false, true).GetBytes(Encode(record).ToString(Formatting.None));
                using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); fault?.Invoke("BeforeFlush"); stream.Flush(true); }
                fault?.Invoke("BeforePublish"); CheckPath(root); CheckPath(path); CheckPath(temp);
                if (update) File.Replace(temp, path, null); else File.Move(temp, path);
            }
            finally { if (File.Exists(temp)) { CheckPath(temp); File.Delete(temp); } }
        }
        public void RemoveConfirmed(MssqlSnapshotRecoveryRecord expected)
        {
            Validate(expected); CheckPath(root); string path = RecordPath(expected.JobId); CheckPath(path);
            if (!File.Exists(path)) return;
            var current = ReadOne(path);
            if (current.Record == null || !Same(current.Record, expected)) throw new IOException("Recovery record ownership mismatch.");
            fault?.Invoke("BeforeDelete"); CheckPath(root); CheckPath(path); File.Delete(path);
        }
        private MssqlSnapshotRecoveryNotice ReadOne(string path)
        {
            try
            {
                CheckPath(path);
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length > 65536) return Error(MssqlSnapshotRecoveryReadError.TooLarge);
                    bytes = new byte[(int)stream.Length]; int offset = 0;
                    while (offset < bytes.Length) { int count = stream.Read(bytes, offset, bytes.Length - offset); if (count == 0) throw new IOException(); offset += count; }
                }
                JObject dto;
                using (var reader = new JsonTextReader(new StringReader(new UTF8Encoding(false, true).GetString(bytes))) { DateParseHandling = DateParseHandling.None, MaxDepth = 8 })
                { dto = JObject.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error }); if (reader.Read()) throw new JsonException(); }
                if (dto["version"] == null || dto["version"].Type != JTokenType.Integer) throw new JsonException();
                if ((int?)dto["version"] != 1) return Error(MssqlSnapshotRecoveryReadError.UnsupportedVersion);
                string[] names = { "version", "jobId", "serverName", "databaseName", "databaseId", "databaseCreateDate", "originalState", "phase", "observedState", "updatedUtc", "noticeCode" };
                if (dto.Count != names.Length) throw new JsonException();
                foreach (string name in names) if (dto[name] == null) throw new JsonException();
                foreach (string name in new[] { "jobId", "serverName", "databaseName", "databaseCreateDate", "updatedUtc" }) if (dto[name].Type != JTokenType.String) throw new JsonException();
                foreach (string name in new[] { "databaseId", "originalState", "phase", "noticeCode" }) if (dto[name].Type != JTokenType.Integer) throw new JsonException();
                if (dto["observedState"].Type != JTokenType.Integer && dto["observedState"].Type != JTokenType.Null) throw new JsonException();
                var record = new MssqlSnapshotRecoveryRecord(Guid.ParseExact((string)dto["jobId"], "N"),
                    new MssqlSnapshotIdentity((string)dto["serverName"], (string)dto["databaseName"], (int)dto["databaseId"], DateTime.ParseExact((string)dto["databaseCreateDate"], "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind)),
                    (int)dto["originalState"], (MssqlSnapshotRecoveryPhase)(int)dto["phase"], (int?)dto["observedState"], DateTime.ParseExact((string)dto["updatedUtc"], "O", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind), (MssqlSnapshotRecoveryCode)(int)dto["noticeCode"]);
                Validate(record);
                if (!string.Equals(Path.GetFileName(path), record.JobId.ToString("N") + ".json", StringComparison.Ordinal)) throw new JsonException();
                return new MssqlSnapshotRecoveryNotice(record, path, MssqlSnapshotRecoveryReadError.None);
            }
            catch (UnsafeRecoveryPathException) { return Error(MssqlSnapshotRecoveryReadError.UnsafePath); }
            catch (IOException) { return Error(MssqlSnapshotRecoveryReadError.ReadFailed); }
            catch (UnauthorizedAccessException) { return Error(MssqlSnapshotRecoveryReadError.ReadFailed); }
            catch (Exception ex) when (ex is JsonException || ex is ArgumentException || ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            { return Error(MssqlSnapshotRecoveryReadError.InvalidRecord); }
        }
        private static MssqlSnapshotRecoveryNotice Error(MssqlSnapshotRecoveryReadError error) => new MssqlSnapshotRecoveryNotice(null, null, error);
        public IReadOnlyList<MssqlSnapshotRecoveryNotice> ReadPending()
        {
            var results = new List<MssqlSnapshotRecoveryNotice>();
            try
            {
                CheckPath(root); if (!Directory.Exists(root)) return results.AsReadOnly();
                foreach (string path in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly))
                {
                    if (results.Count == 100) { results.Add(Error(MssqlSnapshotRecoveryReadError.Overflow)); break; }
                    results.Add(ReadOne(path));
                }
            }
            catch (UnsafeRecoveryPathException) { results.Add(Error(MssqlSnapshotRecoveryReadError.UnsafePath)); }
            catch (IOException) { results.Add(Error(MssqlSnapshotRecoveryReadError.ReadFailed)); }
            catch (UnauthorizedAccessException) { results.Add(Error(MssqlSnapshotRecoveryReadError.ReadFailed)); }
            return results.AsReadOnly();
        }
    }
}
