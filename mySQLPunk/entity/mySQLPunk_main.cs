using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
                Dictionary<string, object> doo = new Dictionary<string, object>();
                foreach (JObject obj in ja[i])
                {
                    Dictionary<string, object> dictObj = obj.ToObject<Dictionary<string, object>>();
                    dictObj["isConnect"] = "F";
                    connections.Add(dictObj);
                }
            }
        }
        public void setSettingINI()
        {

        }

    }

}
