using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Windows.Forms;
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
            catch
            {
                // 不能默默當作空清單：下一次存檔會用空清單覆蓋整份連線。
                // 先備份損毀檔再告知使用者，至少留下救援的機會。
                string backupPath = string.Empty;
                try
                {
                    backupPath = setting_path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
                    File.Copy(setting_path, backupPath, true);
                }
                catch { }
                try
                {
                    MessageBox.Show(
                        Localization.Format("Connection.SettingsCorrupt", setting_path, backupPath),
                        Localization.T("Common.Error"),
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }
                catch { }
                return;
            }

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
            // 憑證名稱含 profile 名。若沿用來源的 credential_target，之後在新 profile 的
            // 任何一次存檔都會把憑證「搬走」並刪掉來源 profile 的密碼（來源密碼無聲消失）。
            // 這裡當場把讀得到的密碼複寫到新 profile 名下；讀不到的清空 target，寧可要求重打。
            content = RewriteCredentialTargetsForCopiedProfile(content, target);
            File.WriteAllText(GetProfileSettingPath(target), content, Encoding.UTF8);
        }

        private string RewriteCredentialTargetsForCopiedProfile(string content, string targetProfileName)
        {
            try
            {
                JToken root = JToken.Parse(content);
                JToken connectionsToken = root.Type == JTokenType.Array ? root : root["connections"];
                JArray connections = connectionsToken as JArray;
                if (connections == null) return content;

                foreach (JToken token in connections)
                {
                    JObject conn = token as JObject;
                    if (conn == null) continue;
                    string oldTarget = (string)conn["credential_target"] ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(oldTarget)) continue;

                    string password;
                    string newTarget = string.Empty;
                    if (WindowsCredentialService.TryReadPassword(oldTarget, out password) && !string.IsNullOrEmpty(password))
                    {
                        Dictionary<string, object> connDict = new Dictionary<string, object>();
                        foreach (var property in conn.Properties())
                        {
                            connDict[property.Name] = property.Value == null ? string.Empty : property.Value.ToString();
                        }
                        connDict["username"] = SafeDecrypt(GetVal(connDict, "username"));
                        string candidate = WindowsCredentialService.BuildTargetName(targetProfileName, connDict);
                        if (WindowsCredentialService.TryWritePassword(candidate, GetVal(connDict, "username"), password))
                        {
                            newTarget = candidate;
                        }
                    }
                    conn["credential_target"] = newTarget;
                }
                return root.ToString(Newtonsoft.Json.Formatting.Indented);
            }
            catch
            {
                // 解析不了就原樣複製，最壞情況與舊行為相同
                return content;
            }
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
                TryDeleteProfileCredentials(path);
                File.Delete(path);
            }

            if (string.Equals(ActiveProfileName, normalized, StringComparison.OrdinalIgnoreCase))
            {
                ActiveProfileName = DefaultProfileName;
                SaveActiveProfileName();
                getSettingINI();
            }
        }

        /// <summary>刪除 profile 前，把檔案裡記錄的憑證從 Windows 認證管理員清掉。</summary>
        private static void TryDeleteProfileCredentials(string settingPath)
        {
            try
            {
                JToken root = JToken.Parse(File.ReadAllText(settingPath, Encoding.UTF8));
                JToken connectionsToken = root.Type == JTokenType.Array ? root : root["connections"];
                JArray connections = connectionsToken as JArray;
                if (connections == null) return;
                foreach (JToken token in connections)
                {
                    JObject conn = token as JObject;
                    string target = conn == null ? null : (string)conn["credential_target"];
                    if (!string.IsNullOrWhiteSpace(target))
                    {
                        WindowsCredentialService.TryDeletePassword(target);
                    }
                }
            }
            catch
            {
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
                    { "conn_group", GetVal(conn, "conn_group") },
                    // Oracle 連線靠這幾個欄位組 TNS 描述；漏存的話重開程式後連線全數失效
                    { "service_name", GetVal(conn, "service_name") },
                    { "sid", GetVal(conn, "sid") },
                    { "tns_name", GetVal(conn, "tns_name") },
                    { "connection_type", GetVal(conn, "connection_type") },
                    { "oracle_identifier_type", GetVal(conn, "oracle_identifier_type") }
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
            // 先寫新的、確認成功後才刪舊的；反過來的話寫入失敗（群組原則停用
            // Credential Manager 等）會讓密碼三處皆無且完全無感
            if (WindowsCredentialService.TryWritePassword(target, GetVal(conn, "username"), password))
            {
                if (!string.IsNullOrWhiteSpace(existingTarget) &&
                    !string.Equals(existingTarget, target, StringComparison.OrdinalIgnoreCase))
                {
                    WindowsCredentialService.TryDeletePassword(existingTarget);
                }
                return target;
            }

            // 寫入失敗：沿用還存在的舊憑證，至少不把密碼弄丟
            return string.IsNullOrWhiteSpace(existingTarget) ? "" : existingTarget;
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
