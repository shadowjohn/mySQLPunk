using System;
using System.Collections.Generic;
using System.Data;

namespace mySQLPunk.lib
{
    public interface IDatabase : IDisposable
    {
        void SetConn(string connectionString);
        void Open();
        void Close();
        ConnectionState State { get; }
        string ProviderName { get; }

        // 核心查詢 (同步)
        DataTable SelectSQL(string sql, Dictionary<string, object> parameters = null);
        Dictionary<string, string> ExecSQL(string sql, Dictionary<string, object> parameters = null);

        // 核心查詢 (非同步)
        System.Threading.Tasks.Task<DataTable> SelectSQLAsync(string sql, Dictionary<string, object> parameters = null);
        System.Threading.Tasks.Task<Dictionary<string, string>> ExecSQLAsync(string sql, Dictionary<string, object> parameters = null);
        
        // 元數據獲取
        List<string> GetDatabases();
        List<string> GetTables(string databaseName);
        List<string> GetViews(string databaseName);
        DataTable GetColumns(string databaseName, string tableName);
        DataTable GetIndexes(string databaseName, string tableName);
        DataTable GetTableStatus(string databaseName);
        Dictionary<string, string> GetDatabaseInfo(string databaseName);
        string GetTableCreateStatement(string databaseName, string tableName);

        // 物件複製用 API
        bool TableExists(string databaseName, string tableName);
        bool ViewExists(string databaseName, string viewName);
        void RenameTable(string databaseName, string oldTableName, string newTableName);
        void RenameView(string databaseName, string oldViewName, string newViewName);
        long CountRows(string databaseName, string tableName);
        DataTable GetCopyColumns(string databaseName, string tableName);
        DataTable GetCopyIndexes(string databaseName, string tableName);
        void CreateTableForCopy(string databaseName, string tableName, DataTable sourceColumns, string sourceProvider);
        void CreateIndexesForCopy(string databaseName, string tableName, DataTable sourceIndexes, string sourceProvider);
        DataTable SelectTablePage(string databaseName, string tableName, long offset, int limit);
        void InsertTableBatch(string databaseName, string tableName, DataTable rows);
        string GetViewCreateStatement(string databaseName, string viewName);
        void CreateViewFromStatement(string databaseName, string viewName, string sourceViewSql);
    }
}
