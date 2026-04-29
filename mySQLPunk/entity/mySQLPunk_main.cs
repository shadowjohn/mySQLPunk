using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using mySQLPunk.lib;

namespace mySQLPunk.entity
{
    class mySQLPunk_main
    {
        myinclude my = new myinclude();
        private string the_code = "3WAAwesome";
        public List<Dictionary<string, object>> connections = new List<Dictionary<string, object>>();

        public void getSettingINI()
        {
            string setting_path = my.pwd() + "\\setting.ini";
            if (!my.is_file(setting_path))
            {
                my.file_put_contents(setting_path, "");
            }

            connections.Clear();

            string endata = my.b2s(my.file_get_contents(setting_path));
            if (string.IsNullOrWhiteSpace(endata))
            {
                return;
            }

            JArray ja = my.json_decode(endata);
            for (int i = 0, max_i = ja.Count; i < max_i; i++)
            {
                LoadConnectionToken(ja[i]);
            }
        }

        public void setSettingINI()
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
                    { "pwd", Crypto.Encrypt(GetVal(conn, "pwd")) },
                    { "port", GetVal(conn, "port") },
                    { "initial_database", GetVal(conn, "initial_database") },
                    { "db_kind", GetVal(conn, "db_kind") },
                    { "conn_name", GetVal(conn, "conn_name") }
                };
                saveList.Add(item);
            }
            string setting_path = my.pwd() + "\\setting.ini";
            string json = JsonConvert.SerializeObject(saveList, Formatting.Indented);
            my.file_put_contents(setting_path, json);
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
            conn["pwd"] = SafeDecrypt(GetVal(conn, "pwd"));
            conn["isConnect"] = "F";
            connections.Add(conn);
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
