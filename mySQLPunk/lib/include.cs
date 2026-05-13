using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;




namespace utility
{
    public class myinclude
    {

        public string pwd()
        {
            return Directory.GetCurrentDirectory();
        }
        public bool is_dir(string path)
        {
            return Directory.Exists(path);
        }
        public bool is_file(string filepath)
        {
            return File.Exists(filepath);
        }
        public void unlink(string filepath)
        {
            if (is_file(filepath))
            {
                File.Delete(filepath);
            }
        }


        public bool is_string_like(string data, string find_string)
        {
            return (data.IndexOf(find_string) == -1) ? false : true;
        }
        public bool is_istring_like(string data, string find_string)
        {
            return (data.ToUpper().IndexOf(find_string.ToUpper()) == -1) ? false : true;
        }

        public string getSystemKey(string keyindex)
        {
            return ConfigurationManager.AppSettings[keyindex];
        }

        //大小寫
        public string strtoupper(string input)
        {
            return input.ToUpper();
        }
        public string strtolower(string input)
        {
            return input.ToLower();
        }


        public DateTime UnixTimeToDateTime(string text)
        {
            System.DateTime dateTime = new System.DateTime(1970, 1, 1, 0, 0, 0, 0);
            // Add the number of seconds in UNIX timestamp to be converted.            
            dateTime = dateTime.AddSeconds(Convert.ToDouble(text));
            return dateTime;
        }
        //仿php的date
        public string time()
        {
            return strtotime(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        }
        public string date()
        {
            return date("Y-m-d H:i:s", strtotime(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));
        }
        public string date(string format)
        {
            return date(format, strtotime(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.ffffff")));
        }
        public string date(string format, string unixtimestamp)
        {
            DateTime tmp = UnixTimeToDateTime(unixtimestamp);
            tmp = tmp.AddHours(+8);
            switch (format)
            {
                case "Y-m-d H:i:s":
                    return tmp.ToString("yyyy-MM-dd HH:mm:ss");
                case "Y/m/d":
                    return tmp.ToString("yyyy/MM/dd");
                case "Y/m/d H:i:s":
                    return tmp.ToString("yyyy/MM/dd HH:mm:ss");
                case "Y/m/d H:i:s.fff":
                    return tmp.ToString("yyyy/MM/dd HH:mm:ss.fff");
                case "Y-m-d_H_i_s":
                    return tmp.ToString("yyyy-MM-dd_HH_mm_ss");
                case "Y-m-d":
                    return tmp.ToString("yyyy-MM-dd");
                case "H:i:s":
                    return tmp.ToString("HH:mm:ss");
                case "Y-m-d H:i":
                    return tmp.ToString("yyyy-MM-dd HH:mm");
                case "Y_m_d_H_i_s":
                    return tmp.ToString("yyyy_MM_dd_HH_mm_ss");
                case "Y_m_d_H_i_s_fff":
                    return tmp.ToString("yyyy_MM_dd_HH_mm_ss_fff");
                case "w":
                    //回傳week, sun =0 , sat = 6, mon=1.....
                    return Convert.ToInt16(tmp.DayOfWeek).ToString();
                case "Y":
                    return tmp.ToString("yyyy");
                case "m":
                    return tmp.ToString("MM");
                case "d":
                    return tmp.ToString("dd");
                case "H":
                    return tmp.ToString("HH");
                case "i":
                    return tmp.ToString("mm");
                case "s":
                    return tmp.ToString("ss");
                case "Y-m-d H:i:s.fff":
                    return tmp.ToString("yyyy-MM-dd HH:mm:ss.fff");
                case "Y-m-d H:i:s.ffffff":
                    return tmp.ToString("yyyy-MM-dd HH:mm:ss.ffffff");
                case "H:i:s.fff":
                    return tmp.ToString("HH:mm:ss.fff");
                case "H:i:s.ffffff":
                    return tmp.ToString("HH:mm:ss.ffffff");
            }
            return "";
        }
        //strtotime 轉換成 Unix time
        public string strtotime(string value)
        {
            //create Timespan by subtracting the value provided from
            //the Unix Epoch
            TimeSpan span = (Convert.ToDateTime(value) - new DateTime(1970, 1, 1, 0, 0, 0, 0).ToLocalTime());

            //return the total seconds (which is a UNIX timestamp)
            if (is_string_like(value, "."))
            {
                //有小數點               
                double sec = span.Ticks / (TimeSpan.TicksPerMillisecond / 1000.0) / 1000000.0;
                return sec.ToString();
            }
            else
            {
                return span.TotalSeconds.ToString();
            }
        }
        public string strtotime(DateTime value)
        {
            //create Timespan by subtracting the value provided from
            //the Unix Epoch
            TimeSpan span = (value - new DateTime(1970, 1, 1, 0, 0, 0, 0).ToLocalTime());

            //return the total seconds (which is a UNIX timestamp)
            return span.TotalSeconds.ToString();
        }
        //javascript用的吐js資料
        public string jsAddSlashes(string value)
        {
            if (string.IsNullOrEmpty(value)) return value;
            StringBuilder sb = new StringBuilder(value.Length + 10);
            foreach (char c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\"': sb.Append("\\\""); break;
                    case '&': sb.Append("\\x26"); break;
                    case '<': sb.Append("\\x3C"); break;
                    case '>': sb.Append("\\x3E"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        public string basename(string path)
        {
            return Path.GetFileName(path);
        }
        public string mainname(string path)
        {
            return Path.GetFileNameWithoutExtension(path);
        }
        public string subname(string path)
        {
            return Path.GetExtension(path);
        }
        public long getfilesize(string path)
        {
            FileInfo f = new FileInfo(path);
            return f.Length;
        }
        public string size_hum_read(long bytes)
        {
            string[] Suffix = { "B", "KB", "MB", "GB", "TB", "PB", "EB", "ZB", "YB" };
            int i = 0;
            double dblSByte = Convert.ToDouble(bytes);
            if (bytes > 1024)
                for (i = 0; (bytes / 1024) > 0; i++, bytes /= 1024)
                    dblSByte = bytes / 1024.0;
            return String.Format("{0:0.##} {1}", dblSByte, Suffix[i]);
        }

        public void mkdir(string path)
        {
            Directory.CreateDirectory(path);
        }
        public void copy(string sourceFile, string destFile)
        {
            System.IO.File.Copy(sourceFile, destFile, true);
        }
        public string dirname(string path)
        {
            return Directory.GetParent(path).FullName;
        }
        public string basedir()
        {
            //取得專案的起始位置
            return pwd();
        }

        public string microtime()
        {
            System.DateTime dt = DateTime.Now;
            System.DateTime UnixEpoch = new System.DateTime(1970, 1, 1, 0, 0, 0, 0);
            TimeSpan span = dt - UnixEpoch;
            long microseconds = span.Ticks / (TimeSpan.TicksPerMillisecond / 1000);
            return microseconds.ToString();
        }
        public string b2s(byte[] input)
        {
            return System.Text.Encoding.UTF8.GetString(input);
        }
        public byte[] s2b(string input)
        {
            return System.Text.Encoding.UTF8.GetBytes(input);
        }
        private byte[] ReadStream(Stream stream, int initialLength)
        {
            if (initialLength < 1)
            {
                initialLength = 32768;
            }
            byte[] buffer = new byte[initialLength];
            int read = 0;
            int chunk;
            while ((chunk = stream.Read(buffer, read, buffer.Length - read)) > 0)
            {
                read += chunk;
                if (read == buffer.Length)
                {
                    int nextByte = stream.ReadByte();
                    if (nextByte == -1)
                    {
                        return buffer;
                    }
                    byte[] newBuffer = new byte[buffer.Length * 2];
                    Array.Copy(buffer, newBuffer, buffer.Length);
                    newBuffer[read] = (byte)nextByte;
                    buffer = newBuffer;
                    read++;
                }
            }
            byte[] bytes = new byte[read];
            Array.Copy(buffer, bytes, read);
            return bytes;
        }
        public string base64_encode(byte[] data)
        {
            //base64編碼
            return Convert.ToBase64String(data);
        }
        public byte[] base64_decode(string data)
        {
            //base64解碼
            return Convert.FromBase64String(data);
        }
        public string BuildQueryString(Dictionary<string, string> nvc)
        {
            System.Collections.Specialized.NameValueCollection queryString = new System.Collections.Specialized.NameValueCollection();
            foreach (var item in nvc.Keys)
            {
                queryString[item] = nvc[item];
            }
            //queryString["key1"] = "value1";
            //queryString["key2"] = "value2";
            return queryString.ToString();
        }
        public byte[] file_get_contents(string url)
        {
            if (url.ToLower().IndexOf("http:") > -1)
            {
                // URL                 

                HttpWebRequest request = null;
                HttpWebResponse response = null;
                byte[] byteData = null;

                request = (HttpWebRequest)WebRequest.Create(url);
                request.Timeout = 60000;
                request.Proxy = null;
                request.UserAgent = "user_agent','Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 6.1)";
                //request.Referer = getSystemKey("HTTP_REFERER");
                response = (HttpWebResponse)request.GetResponse();
                Stream stream = response.GetResponseStream();
                byteData = ReadStream(stream, 32765);
                response.Close();
                stream.Close();
                return byteData;
            }
            else
            {
                System.IO.StreamReader sr = new System.IO.StreamReader(url);
                string sContents = sr.ReadToEnd();
                sr.Close();
                return s2b(sContents);
            }
        }
        public byte[] file_get_contents_post(string url, string postData)
        {
            HttpWebRequest httpWReq =
            (HttpWebRequest)WebRequest.Create(url);

            //ASCIIEncoding encoding = new ASCIIEncoding();

            byte[] data = Encoding.UTF8.GetBytes(postData);

            httpWReq.Method = "POST";
            httpWReq.ContentType = "application/x-www-form-urlencoded";
            httpWReq.UserAgent = "user_agent','Mozilla/4.0 (compatible; MSIE 8.0; Windows NT 6.1)";
            httpWReq.Proxy = null;
            httpWReq.Timeout = 60000;
            //httpWReq.Referer = HttpContext.Current.Request.ServerVariables["SERVER_NAME"];
            //httpWReq.Referer = url;//getSystemKey("HTTP_REFERER");
            httpWReq.ContentLength = data.Length;

            using (Stream stream = httpWReq.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
                stream.Close();
            }

            HttpWebResponse response = (HttpWebResponse)httpWReq.GetResponse();

            Stream streamD = response.GetResponseStream();
            byte[] byteData = ReadStream(streamD, 32767);
            response.Close();
            streamD.Close();
            return byteData;
            //byte[] responseString = new StreamReader(response.GetResponseStream()).ToArray();

        }
        public string enPWD_string(string input, string thekey)
        {
            if (string.IsNullOrEmpty(input)) return "";
            input = base64_encode(s2b(input));
            thekey = base64_encode(s2b(thekey));

            // Pre-calculate XOR sum of the key (A ^ B ^ C is commutative)
            int combinedKeyXor = 0;
            foreach (char tch in thekey)
            {
                combinedKeyXor ^= (int)tch;
            }

            char[] input_arr = input.ToCharArray();
            char[] output_arr = new char[input_arr.Length];
            for (int i = 0; i < input_arr.Length; i++)
            {
                output_arr[i] = (char)((int)input_arr[i] ^ combinedKeyXor);
            }

            return base64_encode(s2b(new string(output_arr)));
        }
        string UTF8toBig5(string strUtf)
        {
            Encoding utf81 = Encoding.GetEncoding("utf-8");
            Encoding big51 = Encoding.GetEncoding("big5");
            System.Web.HttpContext.Current.Response.ContentEncoding = big51;
            byte[] strUtf81 = utf81.GetBytes(strUtf.Trim());
            byte[] strBig51 = Encoding.Convert(utf81, big51, strUtf81);

            char[] big5Chars1 = new char[big51.GetCharCount(strBig51, 0, strBig51.Length)];
            big51.GetChars(strBig51, 0, strBig51.Length, big5Chars1, 0);
            string tempString1 = new string(big5Chars1);
            return tempString1;
        }
        string BIG5toUTF8(string strUtf)
        {
            Encoding utf81 = Encoding.GetEncoding("utf-8");
            Encoding big51 = Encoding.GetEncoding("big5");
            System.Web.HttpContext.Current.Response.ContentEncoding = utf81;
            byte[] strBig51 = big51.GetBytes(strUtf.Trim());
            byte[] strUtf81 = Encoding.Convert(big51, utf81, strBig51);

            char[] utf8Chars1 = new char[utf81.GetCharCount(strUtf81, 0, strUtf81.Length)];
            utf81.GetChars(strUtf81, 0, strUtf81.Length, utf8Chars1, 0);
            string tempString1 = new string(utf8Chars1);
            return tempString1;
        }
        public string dePWD_string(string input, string thekey)
        {
            if (string.IsNullOrEmpty(input)) return "";
            input = b2s(base64_decode(input));
            thekey = base64_encode(s2b(thekey));

            // Pre-calculate XOR sum of the key (commutative)
            int combinedKeyXor = 0;
            foreach (char tch in thekey)
            {
                combinedKeyXor ^= (int)tch;
            }

            char[] input_arr = input.ToCharArray();
            char[] output_arr = new char[input_arr.Length];
            for (int i = 0; i < input_arr.Length; i++)
            {
                output_arr[i] = (char)((int)input_arr[i] ^ combinedKeyXor);
            }

            string xored = new string(output_arr);
            try
            {
                return b2s(base64_decode(xored));
            }
            catch
            {
                return xored;
            }
        }
        public string EscapeUnicode(string input)
        {
            StringBuilder sb = new StringBuilder(input.Length);
            foreach (char ch in input)
            {
                if (ch <= 0x7f)
                    sb.Append(ch);
                else
                    sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)ch);
            }
            return sb.ToString();
        }
        public string unEscapeUnicode(string input)
        {
            return Regex.Unescape(input);
        }
        public string json_encode(object input)
        {
            return EscapeUnicode(JsonConvert.SerializeObject(input, Formatting.None));
        }
        public string json_format(string input)
        {
            JArray jdod = json_decode(input);
            return EscapeUnicode(JsonConvert.SerializeObject(jdod, Formatting.Indented));
        }
        public string json_format_utf8(string input)
        {
            JArray jdod = json_decode(input);
            return JsonConvert.SerializeObject(jdod, Formatting.Indented);
        }
        public string trim(string input)
        {
            return input.Trim();
        }
        public Dictionary<string, object> json_decode_output_dictionary(string input)
        {
            return jobjToDictionary(json_decode(input));
        }
        public Dictionary<string, object> jobjToDictionary(JToken obj, string name = null)
        {
            name = name ?? "obj";
            if (obj is JObject)
            {
                var asBag =
                    from prop in (obj as JObject).Properties()
                    let propName = prop.Name
                    let propValue = prop.Value is JValue
                        ? new Dictionary<string, object>()
                        {
                            {prop.Name, prop.Value}
                        }
                        : jobjToDictionary(prop.Name)
                    select new KeyValuePair<string, object>(propName, propValue);
                return asBag.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
            if (obj is JArray)
            {
                var vals = (obj as JArray).Values();
                var alldicts = vals
                    .SelectMany(val => jobjToDictionary(name))
                    .Select(x => x.Value)
                    .ToArray();
                return new Dictionary<string, object>()
            {
                {name, (object)alldicts}
            };
            }
            if (obj is JValue)
            {
                return new Dictionary<string, object>()
            {
                {name, (obj as JValue)}
            };
            }
            return new Dictionary<string, object>()
            {
                {name, null}
            };
        }
        public JArray json_decode(string input)
        {
            input = trim(input);
            if (input.Length != 0)
            {
                if (input.Substring(1, 1) != "[")
                {
                    input = "[" + input + "]";
                    return (JArray)JsonConvert.DeserializeObject<JArray>(input);
                }
                else
                {
                    return (JArray)JsonConvert.DeserializeObject<JArray>(input);
                }
            }
            else
            {
                return null;
            }
        }
        public void file_put_contents(string filepath, string input)
        {
            file_put_contents(filepath, s2b(input));
        }
        public void file_put_contents(string filepath, byte[] input)
        {
            FileStream myFile = File.Open(@filepath, FileMode.Create);
            myFile.Write(input, 0, input.Length);
            myFile.Dispose();
        }
        public string implode(string keyword, string[] arrays)
        {
            return string.Join(keyword, arrays);
        }
        public string implode(string keyword, List<string> arrays)
        {
            return string.Join(keyword, arrays);
        }
        public string implode(string keyword, Dictionary<int, string> arrays)
        {
            return string.Join(keyword, arrays.Values);
        }
        public string implode(string keyword, Dictionary<string, string> arrays)
        {
            return string.Join(keyword, arrays.Values);
        }
        public string implode(string keyword, ArrayList arrays)
        {
            return string.Join(keyword, arrays.Cast<object>());
        }
        public string[] explode(string keyword, string data)
        {
            return data.Split(new string[] { keyword }, StringSplitOptions.None);
        }
        public string[] explode(string keyword, object data)
        {
            return data.ToString().Split(new string[] { keyword }, StringSplitOptions.None);
        }
        public string[] explode(string[] keyword, string data)
        {
            return data.Split(keyword, StringSplitOptions.None);
        }
    }

}