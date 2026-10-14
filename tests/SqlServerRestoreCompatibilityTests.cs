using System;
using System.Data;
using mySQLPunk.lib;

public static class SqlServerRestoreCompatibilityTests
{
    public static void Run()
    {
        var headers = new DataTable(); headers.Columns.Add("SoftwareVersionMajor", typeof(int)); headers.Columns.Add("BackupType", typeof(int)); headers.Rows.Add(16, 1);
        Reject(() => SqlServerRestoreCompatibility.Validate(headers, "15.0.0.0"));
        SqlServerRestoreCompatibility.Validate(headers, "16.0.0.0");
        Reject(() => SqlServerRestoreCompatibility.Validate(headers, "unknown"));
        headers.Rows.Add(15, 1); Reject(() => SqlServerRestoreCompatibility.Validate(headers, "16.0.0.0")); headers.Rows.RemoveAt(1);
        headers.Rows[0]["BackupType"] = 2; Reject(() => SqlServerRestoreCompatibility.Validate(headers, "16.0.0.0"));
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidOperationException) { return; }
        throw new Exception("Unsafe restore accepted");
    }
}
