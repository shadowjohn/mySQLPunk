using System;
using System.Collections.Generic;
using System.Data;

namespace mySQLPunk.lib
{
    public sealed class DatabaseMetadataSnapshot
    {
        public List<string> Tables { get; set; }
        public List<string> Views { get; set; }
        public DataTable Functions { get; set; }
        public DataTable Users { get; set; }
        public DataTable Events { get; set; }
    }

    public sealed class MetadataLoadService
    {
        private readonly Func<IDatabase, string, DataTable> _functionLoader;
        private readonly Func<IDatabase, string, Dictionary<string, object>, DataTable> _userLoader;
        private readonly Func<IDatabase, string, DataTable> _eventLoader;

        public MetadataLoadService(
            Func<IDatabase, string, DataTable> functionLoader,
            Func<IDatabase, string, Dictionary<string, object>, DataTable> userLoader,
            Func<IDatabase, string, DataTable> eventLoader)
        {
            _functionLoader = functionLoader ?? throw new ArgumentNullException(nameof(functionLoader));
            _userLoader = userLoader ?? throw new ArgumentNullException(nameof(userLoader));
            _eventLoader = eventLoader ?? throw new ArgumentNullException(nameof(eventLoader));
        }

        public DatabaseMetadataSnapshot Load(IDatabase db, string databaseName, Dictionary<string, object> connInfo)
        {
            return Load(db, databaseName, connInfo, false);
        }

        public DatabaseMetadataSnapshot Load(IDatabase db, string databaseName, Dictionary<string, object> connInfo, bool includeHiddenObjects)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            DatabaseMetadataSnapshot snapshot = new DatabaseMetadataSnapshot();
            try { snapshot.Tables = ObjectVisibilityService.FilterNames(db.GetTables(databaseName), db.ProviderName, "table", includeHiddenObjects); }
            catch (Exception ex) { throw new Exception(ExceptionMessageService.Format("Metadata.LoadTablesFailed", ex), ex); }
            try { snapshot.Views = ObjectVisibilityService.FilterNames(db.GetViews(databaseName), db.ProviderName, "view", includeHiddenObjects); }
            catch (Exception ex) { throw new Exception(ExceptionMessageService.Format("Metadata.LoadViewsFailed", ex), ex); }
            try { snapshot.Functions = _functionLoader(db, databaseName); }
            catch (Exception ex) { throw new Exception(ExceptionMessageService.Format("Metadata.LoadFunctionsFailed", ex), ex); }
            try { snapshot.Users = _userLoader(db, databaseName, connInfo); }
            catch (Exception ex) { throw new Exception(ExceptionMessageService.Format("Metadata.LoadUsersFailed", ex), ex); }
            try { snapshot.Events = _eventLoader(db, databaseName); }
            catch (Exception ex) { throw new Exception(ExceptionMessageService.Format("Metadata.LoadEventsFailed", ex), ex); }
            return snapshot;
        }
    }
}
