using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using mySQLPunk.lib;

namespace mySQLPunk.entity
{
    class mySQLPunk_main
    {
        public const string DefaultProfileName = "default";
        myinclude my = new myinclude();
        public List<Dictionary<string, object>> connections = new List<Dictionary<string, object>>();
        public List<string> groups = new List<string>();
        public string ActiveProfileName { get; private set; } = DefaultProfileName;
        private bool _credentialsMigrated;

        public void getSettingINI()
        {
            LoadActiveProfileName();
            _credentialsMigrated = false;
            string setting_path = GetSettingPath();
            if (!my.is_file(setting_path))
            {
                my.file_put_contents(setting_path, "");
            }

            connections.Clear();
            groups.Clear();

            string endata = my.b2s(my.file_get_contents(setting_path));
            if (string.IsNullOrWhiteSpace(endata))
            {
                return;
            }

            JToken root;
            try { root = JToken.Parse(endata); }
            catch { return; }

            if (root.Type == JTokenType.Array)
            {
                // 舊格式：直接是連線陣列
                foreach (JToken t in (JArray)root)
                    LoadConnectionToken(t);
            }
            else if (root.Type == JTokenType.Object)
            {
                // 新格式：{ "connections": [...], "groups": [...] }
                JObject obj = (JObject)root;
                JArray connArray = obj["connections"] as JArray;
                if (connArray != null)
                    foreach (JToken t in connArray) LoadConnectionToken(t);
                JArray grpArray = obj["groups"] as JArray;
                if (grpArray != null)
                    foreach (JToken g in grpArray)
                    {
                        string gName = g.ToString();
                        if (!string.IsNullOrWhiteSpace(gName) && !groups.Contains(gName))
                            groups.Add(gName);
                    }
            }

            if (_credentialsMigrated)
            {
                setSettingINI();
            }
        }

        public void setSettingINI()
        {
            string setting_path = GetSettingPath();
            my.file_put_contents(setting_path, BuildSettingsJson());
        }

        public List<string> GetProfileNames()
        {
            var result = new List<string> { DefaultProfileName };
            string dir = GetProfilesDirectory();
            if (Directory.Exists(dir))
            {
                foreach (string file in Directory.GetFiles(dir, "*.json"))
                {
                    string name = DecodeProfileFileName(Path.GetFileNameWithoutExtension(file));
                    if (!string.IsNullOrWhiteSpace(name) &&
                        !result.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        result.Add(name);
                    }
                }
            }

            return result.OrderBy(n => n == DefaultProfileName ? "" : n, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public void SwitchProfile(string profileName)
        {
            string normalized = NormalizeProfileName(profileName);
            ActiveProfileName = normalized;
            SaveActiveProfileName();
            getSettingINI();
        }

        public void CreateProfile(string profileName)
        {
            string normalized = NormalizeProfileName(profileName);
            if (string.Equals(normalized, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Directory.CreateDirectory(GetProfilesDirectory());
            string path = GetProfileSettingPath(normalized);
            if (!File.Exists(path))
            {
                File.WriteAllText(path, BuildEmptySettingsJson(), Encoding.UTF8);
            }

            SwitchProfile(normalized);
        }

        public void CopyProfile(string sourceProfileName, string targetProfileName)
        {
            string source = NormalizeProfileName(sourceProfileName);
            string target = NormalizeProfileName(targetProfileName);
            if (string.Equals(target, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Localization.T("Connection.ProfileDefaultAlreadyExists"));
            }
            if (ProfileExists(target))
            {
                throw new InvalidOperationException(Localization.Format("Connection.ProfileExists", target));
            }

            Directory.CreateDirectory(GetProfilesDirectory());
            string sourcePath = GetProfileReadPath(source);
            string content = File.Exists(sourcePath) ? File.ReadAllText(sourcePath, Encoding.UTF8) : BuildEmptySettingsJson();
            File.WriteAllText(GetProfileSettingPath(target), content, Encoding.UTF8);
        }

        public void RenameProfile(string oldProfileName, string newProfileName)
        {
            string oldName = NormalizeProfileName(oldProfileName);
            string newName = NormalizeProfileName(newProfileName);
            if (string.Equals(oldName, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Localization.T("Connection.ProfileDefaultCannotRename"));
            }
            if (string.Equals(newName, DefaultProfileName, StringComparison.OrdinalIgnoreCase) || ProfileExists(newName))
            {
                throw new InvalidOperationException(Localization.Format("Connection.ProfileExists", newName));
            }

            string oldPath = GetProfileSettingPath(oldName);
            string newPath = GetProfileSettingPath(newName);
            Directory.CreateDirectory(GetProfilesDirectory());
            if (!File.Exists(oldPath))
            {
                File.WriteAllText(oldPath, BuildEmptySettingsJson(), Encoding.UTF8);
            }
            File.Move(oldPath, newPath);

            if (string.Equals(ActiveProfileName, oldName, StringComparison.OrdinalIgnoreCase))
            {
                ActiveProfileName = newName;
                SaveActiveProfileName();
            }
        }

        public void DeleteProfile(string profileName)
        {
            string normalized = NormalizeProfileName(profileName);
            if (string.Equals(normalized, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(Localization.T("Connection.ProfileDefaultCannotDelete"));
            }

            string path = GetProfileSettingPath(normalized);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            if (string.Equals(ActiveProfileName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                ActiveProfileName = DefaultProfileName;
                SaveActiveProfileName();
                getSettingINI();
            }
        }

        public void exportConnections(string path)
        {
            File.WriteAllText(path, BuildSettingsJson(), Encoding.UTF8);
        }

        public void importConnections(string path)
        {
            string json = File.ReadAllText(path, Encoding.UTF8);
            JToken root = JToken.Parse(json);
            connections.Clear();
            groups.Clear();
            if (root.Type == JTokenType.Array)
            {
                foreach (JToken t in (JArray)root) LoadConnectionToken(t);
            }
            else if (root.Type == JTokenType.Object)
            {
                JObject obj = (JObject)root;
                JArray connArray = obj["connections"] as JArray;
                if (connArray != null)
                    foreach (JToken t in connArray) LoadConnectionToken(t);
                JArray grpArray = obj["groups"] as JArray;
                if (grpArray != null)
                    foreach (JToken g in grpArray)
                    {
                        string gName = g.ToString();
                        if (!string.IsNullOrWhiteSpace(gName) && !groups.Contains(gName))
                            groups.Add(gName);
                    }
            }
            setSettingINI();
        }

        private string GetSettingPath()
        {
            if (string.Equals(ActiveProfileName, DefaultProfileName, StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine(my.pwd(), "setting.ini");
            }

            Directory.CreateDirectory(GetProfilesDirectory());
            return GetProfileSettingPath(ActiveProfileName);
        }

        private string GetProfilesDirectory()
        {
            return Path.Combine(my.pwd(), "connection_profiles");
        }

        private string GetProfileSettingPath(string profileName)
        {
            return Path.Combine(GetProfilesDirectory(), EncodeProfileFileName(profileName) + ".json");
        }

        private string GetProfileReadPath(string profileName)
        {
            return string.Equals(profileName, DefaultProfileName, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(my.pwd(), "setting.ini")
                : GetProfileSettingPath(profileName);
        }

        private string GetActiveProfilePath()
        {
            return Path.Combine(my.pwd(), "connection-profile.txt");
        }

        private void LoadActiveProfileName()
        {
            string path = GetActiveProfilePath();
            if (!File.Exists(path))
            {
                ActiveProfileName = DefaultProfileName;
                return;
            }

            string name = File.ReadAllText(path, Encoding.UTF8).Trim();
            ActiveProfileName = NormalizeProfileName(name);
        }

        private void SaveActiveProfileName()
        {
            File.WriteAllText(GetActiveProfilePath(), ActiveProfileName, Encoding.UTF8);
        }

        private static string NormalizeProfileName(string profileName)
        {
            string name = (profileName ?? string.Empty).Trim();
            return string.IsNullOrWhiteSpace(name) ? DefaultProfileName : name;
        }

        private bool ProfileExists(string profileName)
        {
            string normalized = NormalizeProfileName(profileName);
            if (string.Equals(normalized, DefaultProfileName, StringComparison.OrdinalIgnoreCase)) return true;
            return File.Exists(GetProfileSettingPath(normalized));
        }

        private static string EncodeProfileFileName(string profileName)
        {
            return Uri.EscapeDataString(NormalizeProfileName(profileName));
        }

        private static string DecodeProfileFileName(string fileName)
        {
            try { return Uri.UnescapeDataString(fileName ?? string.Empty); }
            catch { return fileName ?? string.Empty; }
        }

        private static string BuildEmptySettingsJson()
        {
            var root = new
            {
                connections = new object[0],
                groups = new object[0]
            };
            return JsonConvert.SerializeObject(root, Formatting.Indented);
        }

        private string BuildSettingsJson()
        {
            List<Dictionary<string, object>> saveList = new List<Dictionary<string, object>>();
            foreach (var sourceConn in connections)
            {
                var conn = new Dictionary<string, object>(sourceConn);
                NormalizeConnection(conn);

                var item = new Dictionary<string, object>
                {
                    { "host", GetVal(conn, "host") },
                    { "username", Crypto.Encrypt(GetVal(conn, "username")) },
                    { "pwd", "" },
                    { "credential_target", SaveConnectionPasswordToCredential(conn) },
                    { "port", GetVal(conn, "port") },
                    { "initial_database", GetVal(conn, "initial_database") },
                    { "db_kind", GetVal(conn, "db_kind") },
                    { "conn_name", GetVal(conn, "conn_name") },
                    { "path", GetVal(conn, "path") },
                    { "init_geospatial", GetVal(conn, "init_geospatial") },
                    { "trusted_connection", GetVal(conn, "trusted_connection") },
                    { "conn_group", GetVal(conn, "conn_group") }
                };
                saveList.Add(item);
            }

            // 合併明確儲存的群組與連線衍生的群組（去重、排序）
            var allGroups = new List<string>(groups);
            foreach (var conn in connections)
            {
                string g = GetVal(conn, "conn_group");
                if (!string.IsNullOrWhiteSpace(g) && !allGroups.Contains(g))
                    allGroups.Add(g);
            }
            allGroups.Sort(StringComparer.Ordinal);

            var root = new
            {
                connections = saveList,
                groups = allGroups
            };
            return JsonConvert.SerializeObject(root, Formatting.Indented);
        }

        private void LoadConnectionToken(JToken token)
        {
            if (token == null)
            {
                return;
            }

            if (token.Type == JTokenType.Array)
            {
                JArray list = (JArray)token;
                for (int i = 0, max_i = list.Count; i < max_i; i++)
                {
                    LoadConnectionToken(list[i]);
                }
                return;
            }

            Dictionary<string, object> conn = token.ToObject<Dictionary<string, object>>();
            if (conn == null)
            {
                return;
            }

            NormalizeConnection(conn);
            conn["username"] = SafeDecrypt(GetVal(conn, "username"));
            LoadConnectionPassword(conn);
            conn["isConnect"] = "F";
            connections.Add(conn);
        }

        private string SaveConnectionPasswordToCredential(Dictionary<string, object> conn)
        {
            string existingTarget = GetVal(conn, "credential_target");
            string password = GetVal(conn, "pwd");
            if (string.IsNullOrEmpty(password))
            {
                if (!string.IsNullOrWhiteSpace(existingTarget))
                {
                    WindowsCredentialService.TryDeletePassword(existingTarget);
                }
                return "";
            }

            string target = WindowsCredentialService.BuildTargetName(ActiveProfileName, conn);
            if (!string.Equals(existingTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                WindowsCredentialService.TryDeletePassword(existingTarget);
            }

            if (WindowsCredentialService.TryWritePassword(target, GetVal(conn, "username"), password))
            {
                return target;
            }

            return "";
        }

        private void LoadConnectionPassword(Dictionary<string, object> conn)
        {
            string legacyPassword = SafeDecrypt(GetVal(conn, "pwd"));
            string target = GetVal(conn, "credential_target");
            string credentialPassword;

            if (!string.IsNullOrWhiteSpace(target) && WindowsCredentialService.TryReadPassword(target, out credentialPassword))
            {
                conn["pwd"] = credentialPassword;
                return;
            }

            if (!string.IsNullOrEmpty(legacyPassword))
            {
                target = WindowsCredentialService.BuildTargetName(ActiveProfileName, conn);
                if (WindowsCredentialService.TryWritePassword(target, GetVal(conn, "username"), legacyPassword))
                {
                    conn["credential_target"] = target;
                    _credentialsMigrated = true;
                }
                conn["pwd"] = legacyPassword;
                return;
            }

            conn["pwd"] = "";
        }

        private void NormalizeConnection(Dictionary<string, object> conn)
        {
            CopyIfMissing(conn, "name", "conn_name");
            CopyIfMissing(conn, "ip", "host");
            CopyIfMissing(conn, "kind", "db_kind");
            CopyIfMissing(conn, "login_id", "username");

            if (!conn.ContainsKey("initial_database"))
            {
                conn["initial_database"] = "";
            }
            if (!conn.ContainsKey("isConnect"))
            {
                conn["isConnect"] = "F";
            }
            if (!conn.ContainsKey("trusted_connection"))
            {
                conn["trusted_connection"] = "F";
            }
            if (!conn.ContainsKey("conn_group"))
            {
                conn["conn_group"] = "";
            }
            if (!conn.ContainsKey("credential_target"))
            {
                conn["credential_target"] = "";
            }
        }

        private void CopyIfMissing(Dictionary<string, object> conn, string oldKey, string newKey)
        {
            if (!conn.ContainsKey(newKey) && conn.ContainsKey(oldKey))
            {
                conn[newKey] = conn[oldKey];
            }
        }

        private string SafeDecrypt(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "";
            }

            try
            {
                return Crypto.Decrypt(value);
            }
            catch
            {
                return value;
            }
        }

        private string GetVal(Dictionary<string, object> dict, string key)
        {
            if (dict.ContainsKey(key) && dict[key] != null) return dict[key].ToString();
            return "";
        }

    }

}
