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
            /*
             # setting.ini
             [
               {
                 "name":"",
                 "ip":"",
                 "port":"",
                 "kind":"",
                 "login_id":"",
                 "pwd":"",
                 "isConnect":"F",
                 "pdo": obj ,
                 "connString": string
               }
             ]
            */
            List<Dictionary<string, string>> data = new List<Dictionary<string, string>>();
            string setting_path = my.pwd() + "\\setting.ini";
            if (!my.is_file(setting_path))
            {
                my.file_put_contents(setting_path, "");
            }
            string endata = my.b2s(my.file_get_contents(setting_path));
            //string dedata = my.dePWD_string(endata, the_code);
            JArray ja = new JArray();
            if (endata != "")
            {
                ja = my.json_decode(endata);
            }

            for (int i = 0, max_i = ja.Count; i < max_i; i++)
            {  
                List<Dictionary<string, object>> list = ja[i].ToObject<List<Dictionary<string, object>>>();
                foreach (var conn in list)
                {
                    // 解密帳號密碼
                    conn["username"] = Crypto.Decrypt(GetVal(conn, "username"));
                    conn["pwd"] = Crypto.Decrypt(GetVal(conn, "pwd"));
                    
                    conn["isConnect"] = "F";
                    connections.Add(conn);
                }
            }
        }
        public void setSettingINI()
        {
            List<Dictionary<string, object>> saveList = new List<Dictionary<string, object>>();
            foreach (var conn in connections)
            {
                var item = new Dictionary<string, object>
                {
                    { "host", GetVal(conn, "host") },
                    { "username", Crypto.Encrypt(GetVal(conn, "username")) },
                    { "pwd", Crypto.Encrypt(GetVal(conn, "pwd")) },
                    { "port", GetVal(conn, "port") },
                    { "db_kind", GetVal(conn, "db_kind") },
                    { "conn_name", GetVal(conn, "conn_name") }
                };
                saveList.Add(item);
            }
            string setting_path = my.pwd() + "\\setting.ini";
            string json = JsonConvert.SerializeObject(saveList, Formatting.Indented);
            my.file_put_contents(setting_path, json);
        }

        private string GetVal(Dictionary<string, object> dict, string key)
        {
            if (dict.ContainsKey(key) && dict[key] != null) return dict[key].ToString();
            return "";
        }

    }

}
