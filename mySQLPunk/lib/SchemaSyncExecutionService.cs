using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Linq;

namespace mySQLPunk.lib
{
    public enum SchemaSyncStatementOutcome
    {
        Succeeded,
        Failed,
        RolledBack,
        NotRun
    }

    public sealed class SchemaSyncStatementResult
    {
        public int Index { get; set; }
        public string Sql { get; set; }
        public SchemaSyncStatementOutcome Outcome { get; set; }
        public string Message { get; set; }
        public TimeSpan Elapsed { get; set; }
    }

    public sealed class SchemaSyncBatchResult
    {
        public SchemaSyncBatchResult(bool usedTransaction, List<SchemaSyncStatementResult> statements)
        {
            UsedTransaction = usedTransaction;
            Statements = statements;
        }

        public bool UsedTransaction { get; private set; }
        public List<SchemaSyncStatementResult> Statements { get; private set; }

        public bool Succeeded
        {
            get { return Statements.All(item => item.Outcome == SchemaSyncStatementOutcome.Succeeded); }
        }

        public int SucceededCount
        {
            get { return Statements.Count(item => item.Outcome == SchemaSyncStatementOutcome.Succeeded); }
        }

        public SchemaSyncStatementResult Failure
        {
            get { return Statements.FirstOrDefault(item => item.Outcome == SchemaSyncStatementOutcome.Failed); }
        }

        public string Summary
        {
            get
            {
                if (Succeeded)
                {
                    return Localization.Format(UsedTransaction ? "SchemaSync.Exec.DoneTransaction" : "SchemaSync.Exec.Done", Statements.Count);
                }

                SchemaSyncStatementResult failure = Failure;
                int index = failure == null ? 0 : failure.Index + 1;
                string message = failure == null ? string.Empty : SchemaSyncScriptService.SingleLine(failure.Message);
                return UsedTransaction
                    ? Localization.Format("SchemaSync.Exec.RolledBack", index, message)
                    : Localization.Format("SchemaSync.Exec.PartiallyApplied", index, message, SucceededCount);
            }
        }
    }

    /// <summary>
    /// 在「目標」資料庫依序執行同步語句，遇到第一個錯誤就停止。PostgreSQL／SQL Server／SQLite 包在單一交易、
    /// 失敗全部回滾；MySQL／MariaDB 的 DDL 會隱含提交，因此回報哪些語句已生效。
    /// 使用各 provider 既有的連線，並照現有慣例切換資料庫：MySQL 先 USE、SQL Server 暫時 ChangeDatabase 後切回，
    /// PostgreSQL 的連線綁定單一資料庫，名稱不符時拒絕執行。
    /// </summary>
    public static class SchemaSyncExecutionService
    {
        public static SchemaSyncBatchResult Execute(IDatabase database, string databaseName, IList<string> statements)
        {
            if (database == null) throw new ArgumentNullException("database");
            ValidateStatements(statements);
            string provider = SchemaSyncScriptService.NormalizeProvider(database.ProviderName);

            my_mysql mysql = database as my_mysql;
            if (mysql != null)
            {
                EnsureOpen(mysql.MCT);
                using (DbCommand use = mysql.MCT.CreateCommand())
                {
                    use.CommandText = "USE `" + (databaseName ?? string.Empty).Replace("`", "``") + "`";
                    use.ExecuteNonQuery();
                }

                return ExecuteOnConnection(mysql.MCT, false, statements);
            }

            my_mssql sqlServer = database as my_mssql;
            if (sqlServer != null)
            {
                EnsureOpen(sqlServer.MCT);
                string original = sqlServer.MCT.Database;
                try
                {
                    sqlServer.MCT.ChangeDatabase(databaseName);
                    return ExecuteOnConnection(sqlServer.MCT, true, statements);
                }
                finally
                {
                    if (!string.IsNullOrEmpty(original) && sqlServer.MCT.State == ConnectionState.Open)
                    {
                        sqlServer.MCT.ChangeDatabase(original);
                    }
                }
            }

            my_postgresql postgres = database as my_postgresql;
            if (postgres != null)
            {
                EnsureOpen(postgres.MCT);
                if (!string.Equals(postgres.MCT.Database, databaseName, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(Localization.Format("SchemaSync.Exec.WrongDatabase", postgres.MCT.Database, databaseName));
                }

                return ExecuteOnConnection(postgres.MCT, true, statements);
            }

            my_sqlite sqlite = database as my_sqlite;
            if (sqlite != null)
            {
                EnsureOpen(sqlite.MCT);
                return ExecuteOnConnection(sqlite.MCT, true, statements);
            }

            throw new NotSupportedException(Localization.Format("SchemaSync.UnsupportedProvider", provider));
        }

        /// <summary>Provider-independent core, also used by tests: runs in order, stops at the first failure.</summary>
        public static SchemaSyncBatchResult ExecuteOnConnection(DbConnection connection, bool useTransaction, IList<string> statements)
        {
            if (connection == null) throw new ArgumentNullException("connection");
            ValidateStatements(statements);
            EnsureOpen(connection);
            List<SchemaSyncStatementResult> results = new List<SchemaSyncStatementResult>();
            DbTransaction transaction = useTransaction ? connection.BeginTransaction() : null;
            try
            {
                for (int index = 0; index < statements.Count; index++)
                {
                    Stopwatch stopwatch = Stopwatch.StartNew();
                    try
                    {
                        using (DbCommand command = connection.CreateCommand())
                        {
                            command.Transaction = transaction;
                            command.CommandText = statements[index];
                            command.ExecuteNonQuery();
                        }

                        results.Add(new SchemaSyncStatementResult
                        {
                            Index = index,
                            Sql = statements[index],
                            Outcome = SchemaSyncStatementOutcome.Succeeded,
                            Message = string.Empty,
                            Elapsed = stopwatch.Elapsed
                        });
                    }
                    catch (Exception ex)
                    {
                        if (ex is OutOfMemoryException || ex is StackOverflowException) throw;
                        results.Add(new SchemaSyncStatementResult
                        {
                            Index = index,
                            Sql = statements[index],
                            Outcome = SchemaSyncStatementOutcome.Failed,
                            Message = ex.Message,
                            Elapsed = stopwatch.Elapsed
                        });
                        if (transaction != null)
                        {
                            transaction.Rollback();
                            transaction.Dispose();
                            transaction = null;
                            foreach (SchemaSyncStatementResult done in results.Where(item => item.Outcome == SchemaSyncStatementOutcome.Succeeded))
                            {
                                done.Outcome = SchemaSyncStatementOutcome.RolledBack;
                            }
                            return Complete(true, results, statements);
                        }

                        return Complete(false, results, statements);
                    }
                }

                if (transaction != null) transaction.Commit();
                return new SchemaSyncBatchResult(useTransaction, results);
            }
            finally
            {
                if (transaction != null) transaction.Dispose();
            }
        }

        private static SchemaSyncBatchResult Complete(bool usedTransaction, List<SchemaSyncStatementResult> results, IList<string> statements)
        {
            for (int rest = results.Count; rest < statements.Count; rest++)
            {
                results.Add(new SchemaSyncStatementResult
                {
                    Index = rest,
                    Sql = statements[rest],
                    Outcome = SchemaSyncStatementOutcome.NotRun,
                    Message = string.Empty,
                    Elapsed = TimeSpan.Zero
                });
            }

            return new SchemaSyncBatchResult(usedTransaction, results);
        }

        private static void ValidateStatements(IList<string> statements)
        {
            if (statements == null || statements.Count == 0 || statements.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(Localization.T("SchemaSync.Exec.Empty"));
            }
        }

        private static void EnsureOpen(DbConnection connection)
        {
            if (connection == null) throw new InvalidOperationException(Localization.T("SchemaSync.Exec.NotConnected"));
            if (connection.State != ConnectionState.Open) connection.Open();
        }
    }
}
