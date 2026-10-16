using System;
using System.Collections.Generic;
using System.IO;

namespace mySQLPunk.lib
{
    internal sealed class WizardOwnedSession : IDisposable
    {
        private IDatabase owner;
        internal IDatabase Database { get; private set; }

        private WizardOwnedSession(ConnectionOpenResult opened)
        {
            owner = opened.Database;
            // Native streaming readers need the underlying provider; owner retains the SSH lease.
            Database = opened.NativeDatabase ?? opened.Database;
        }

        internal static Dictionary<string, object> CopySettings(IDictionary<string, object> source, string provider, string database)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (string.IsNullOrWhiteSpace(database)) throw new ArgumentException("An explicit target database is required.");
            var settings = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in source)
            {
                if (string.Equals(pair.Key, "pdo", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key, "isConnect", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(pair.Key, "connString", StringComparison.OrdinalIgnoreCase)) continue;
                if (pair.Value == null || pair.Value is string || pair.Value.GetType().IsPrimitive || pair.Value is decimal)
                    settings[pair.Key] = pair.Value;
            }
            settings["db_kind"] = ConnectionConfigurationService.NormalizeProvider(provider);
            settings["initial_database"] = database;
            return settings;
        }

        internal static WizardOwnedSession Open(IDictionary<string, object> source, string provider, string database)
        {
            var settings = CopySettings(source, provider, database);
            if (string.Equals(Convert.ToString(settings["db_kind"]), "sqlite", StringComparison.OrdinalIgnoreCase))
            {
                string path = ConnectionConfigurationService.GetValue(settings, "path");
                if (!string.Equals(database, "main", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException("SQLite wizard currently requires the main database.");
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    throw new IOException("The selected SQLite file must already exist; the wizard will not create an empty source.");
            }
            return new WizardOwnedSession(ConnectionOpenService.OpenWizardConnection(settings));
        }

        public void Dispose()
        {
            var current = owner; owner = null;
            if (current != null) current.Dispose();
        }
    }
}
