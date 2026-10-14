using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using mySQLPunk.lib;

public static class SqlServerMigrationTests
{
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    public static void Run()
    {
        foreach (string type in new[] { "int", "nvarchar(4000)", "decimal(38,38)", "datetime2(7)", "varbinary(8000)" }) Check(SqlServerCompatibilityMigration.IsSupportedType(type), "Supported type " + type);
        foreach (string type in new[] { "vector", "xml", "nvarchar(max)", "nvarchar(4001)", "varchar(8001)", "decimal(2,3)", "int);DROP TABLE x--" }) Check(!SqlServerCompatibilityMigration.IsSupportedType(type), "Unsafe type " + type);
        foreach (int major in SqlServerCompatibilityMigration.SupportedTargetMajors.Where(value => value < 17))
            Check(SqlServerCompatibilityMigration.Preview(Source().Catalog, Target(major).Catalog, major).CanStart, "Declared matrix " + major);
        foreach (int sourceMajor in SqlServerCompatibilityMigration.SupportedTargetMajors)
            foreach (int targetMajor in SqlServerCompatibilityMigration.SupportedTargetMajors)
            {
                var origin = Source(); origin.Catalog.MajorVersion = sourceMajor;
                var destination = Target(targetMajor);
                var direction = SqlServerCompatibilityMigration.Preview(origin.Catalog, destination.Catalog, targetMajor);
                Check(direction.CanStart, "Bidirectional preflight " + sourceMajor + " -> " + targetMajor);
                var migrated = SqlServerCompatibilityMigration.Run(direction, "empty", origin, destination, CancellationToken.None);
                Check(migrated.Succeeded && migrated.Rows == 1001 && destination.Commits == 1, "Bidirectional verified transaction " + sourceMajor + " -> " + targetMajor);
            }
        var from = Source(); var to = Target(15);
        var plan = SqlServerCompatibilityMigration.Preview(from.Catalog, to.Catalog, 15);
        Check(plan.CanStart && plan.Statements.Single().Contains("[na]]me]"), "Preview quoted DDL");
        Check(!SqlServerCompatibilityMigration.Run(plan, "wrong", from, to, CancellationToken.None).Succeeded && to.Begins == 0, "Confirmation precedes writes");
        var success = SqlServerCompatibilityMigration.Run(plan, "empty", from, to, CancellationToken.None);
        Check(success.Succeeded && success.Rows == 1001 && to.Commits == 1 && to.Batches == 3, "Batched verified migration");
        foreach (string failure in new[] { "write", "content", "schema", "begin", "cancel", "cancel_midway", "rollback", "commit", "commit_ack_lost" })
        {
            from = Source(); to = Target(15); plan = SqlServerCompatibilityMigration.Preview(from.Catalog, to.Catalog, 15);
            to.Failure = failure;
            using (var cancellation = new CancellationTokenSource())
            {
                if (failure == "cancel") cancellation.Cancel();
                if (failure == "cancel_midway") to.Stop = cancellation.Cancel;
                var result = SqlServerCompatibilityMigration.Run(plan, "empty", from, to, cancellation.Token);
                Check(!result.Succeeded && to.Commits == 0, "Failure never succeeds " + failure);
                Check(result.CleanupConfirmed == (failure != "rollback" && failure != "commit" && failure != "commit_ack_lost"), "Cleanup certainty " + failure);
                if (result.CleanupConfirmed) Check(to.Rows.Rows.Count == 0, "Failure rollback rows " + failure);
                if (failure == "commit_ack_lost") Check(to.Rows.Rows.Count == 1001, "Lost commit acknowledgement can leave committed rows");
            }
        }
        from = Source(); to = Target(15); plan = SqlServerCompatibilityMigration.Preview(from.Catalog, to.Catalog, 15);
        to.Catalog.ObjectCount = 1;
        Check(!SqlServerCompatibilityMigration.Run(plan, "empty", from, to, CancellationToken.None).Succeeded && to.Batches == 0, "Changed target denied");
        from = Source(); to = Target(15); to.Catalog.Collation = "different";
        Check(!SqlServerCompatibilityMigration.Preview(from.Catalog, to.Catalog, 15).CanStart, "Collation denied");
        foreach (string feature in new[] { "identity", "constraint", "index", "trigger", "procedure", "schema", "ledger" })
        {
            from = Source(); from.Catalog.Blockers.Add(feature);
            Check(!SqlServerCompatibilityMigration.Preview(from.Catalog, Target(15).Catalog, 15).CanStart, "Feature blocked " + feature);
        }
        Check(!SqlServerCompatibilityMigration.Preview(Source().Catalog, Target(12).Catalog, 12).CanStart, "Unknown/old target denied");
        Check(!SqlServerCompatibilityMigration.Preview(Source().Catalog, Target(15).Catalog, 13).CanStart, "Explicit version mismatch denied");
    }
    private static MockSession Source()
    {
        var session = Target(17); session.Catalog.Identity = "source/1"; session.Catalog.DatabaseName = "source";
        var table = new SqlServerMigrationTable { Name = "na]me", RowCount = 1001 };
        table.Columns.Add(new SqlServerMigrationColumn { Name = "id", TypeSql = "int" });
        table.Columns.Add(new SqlServerMigrationColumn { Name = "value", TypeSql = "nvarchar(30)", Nullable = true });
        session.Catalog.Tables.Add(table); session.Catalog.ObjectCount = 1;
        for (int index = 0; index < 1001; index++) session.Rows.Rows.Add(index, index % 2 == 0 ? (object)DBNull.Value : "中文");
        return session;
    }
    private static MockSession Target(int major)
    {
        var session = new MockSession { Catalog = new SqlServerMigrationCatalog { Identity = "target/2", DatabaseName = "empty", MajorVersion = major, Collation = "same" } };
        session.Rows.Columns.Add("id", typeof(int)); session.Rows.Columns.Add("value", typeof(string)); return session;
    }
    private sealed class MockSession : ISqlServerMigrationSession
    {
        public SqlServerMigrationCatalog Catalog = new SqlServerMigrationCatalog();
        public DataTable Rows = new DataTable();
        public string Failure = string.Empty;
        public int Begins, Commits, Batches;
        public Action Stop = () => { };
        public SqlServerMigrationCatalog ReadCatalog()
        {
            return new SqlServerMigrationCatalog
            {
                Identity = Catalog.Identity, DatabaseName = Catalog.DatabaseName, MajorVersion = Catalog.MajorVersion,
                Collation = Catalog.Collation, ObjectCount = Catalog.ObjectCount, Blockers = Catalog.Blockers.ToList(),
                Tables = Catalog.Tables.Select(table => new SqlServerMigrationTable
                {
                    Name = table.Name, RowCount = table.RowCount,
                    Columns = table.Columns.Select(column => new SqlServerMigrationColumn { Name = column.Name, TypeSql = column.TypeSql, Nullable = column.Nullable }).ToList()
                }).ToList()
            };
        }
        public void Begin(bool source) { Begins++; if (Failure == "begin") throw new InvalidOperationException(); }
        public IEnumerable<DataTable> ReadBatches(SqlServerMigrationTable table, int batchSize, CancellationToken cancellationToken)
        {
            for (int offset = 0; offset < Rows.Rows.Count; offset += batchSize)
            {
                var batch = Rows.Clone();
                for (int index = offset; index < Math.Min(offset + batchSize, Rows.Rows.Count); index++) batch.ImportRow(Rows.Rows[index]);
                if (Failure == "content") batch.Rows[0]["value"] = "corrupt";
                yield return batch;
            }
        }
        public void Execute(string sql)
        {
            var table = Source().Catalog.Tables.Single(); table.RowCount = 0; Catalog.Tables.Add(table); Catalog.ObjectCount = 1;
            if (Failure == "schema") table.Columns[0].TypeSql = "bigint";
        }
        public void Insert(SqlServerMigrationTable table, DataTable rows)
        {
            Batches++;
            foreach (DataRow row in rows.Rows) Rows.ImportRow(row);
            Catalog.Tables[0].RowCount = Rows.Rows.Count;
            if (Batches == 2) Stop();
            if ((Failure == "write" || Failure == "rollback") && Batches == 2) throw new InvalidOperationException();
        }
        public void Commit() { if (Failure == "commit" || Failure == "commit_ack_lost") throw new InvalidOperationException(); Commits++; }
        public void Rollback()
        {
            if (Failure == "rollback" || Failure == "commit") throw new InvalidOperationException();
            if (Failure == "commit_ack_lost") return;
            if (Catalog.Identity.StartsWith("target")) { Rows.Clear(); Catalog.Tables.Clear(); Catalog.ObjectCount = 0; }
        }
        public void Dispose() { }
    }
}
