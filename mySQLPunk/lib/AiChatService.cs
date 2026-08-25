using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using Newtonsoft.Json.Linq;
using mySQLPunk;

namespace mySQLPunk.lib
{
    /// <summary>一則對話訊息（role: system / user / assistant）。</summary>
    public class AiChatMessage
    {
        public string Role;
        public string Content;

        public AiChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    /// <summary>
    /// AI 助理的服務設定。走 OpenAI 相容的 chat/completions 介面，
    /// 同一套程式碼支援 GitHub Models、OpenAI、本機 Ollama 與自訂端點。
    /// </summary>
    public class AiChatSettings
    {
        public string Provider;
        public string Endpoint;
        public string Model;

        public static AiChatSettings Load()
        {
            AiChatSettings s = new AiChatSettings
            {
                Provider = ApplicationOptionSettings.GetString("AiProvider"),
                Endpoint = (ApplicationOptionSettings.GetString("AiEndpoint") ?? "").Trim(),
                Model = (ApplicationOptionSettings.GetString("AiModel") ?? "").Trim()
            };
            // 預設 OpenAI:GitHub Models 已進入退場期(brownout),不適合當預設
            if (string.IsNullOrWhiteSpace(s.Provider)) s.Provider = "openai";

            // 端點與模型留空時用各家的合理預設
            if (string.IsNullOrWhiteSpace(s.Endpoint))
            {
                switch (s.Provider)
                {
                    case "openai": s.Endpoint = "https://api.openai.com/v1"; break;
                    case "ollama": s.Endpoint = "http://localhost:11434/v1"; break;
                    case "github":
                    default: s.Endpoint = "https://models.github.ai/inference"; break;
                }
            }
            if (string.IsNullOrWhiteSpace(s.Model))
            {
                switch (s.Provider)
                {
                    case "openai": s.Model = "gpt-4o-mini"; break;
                    case "ollama": s.Model = "llama3.1"; break;
                    case "github":
                    default: s.Model = "openai/gpt-4o-mini"; break;
                }
            }
            return s;
        }
    }

    public static class AiChatService
    {
        /// <summary>API 金鑰放 Windows 認證管理員，跟連線密碼同一套機制。</summary>
        public const string ApiKeyCredentialTarget = "mySQLPunk:ai-api-key";

        public static bool HasApiKey()
        {
            string key;
            return WindowsCredentialService.TryReadPassword(ApiKeyCredentialTarget, out key)
                && !string.IsNullOrWhiteSpace(key);
        }

        /// <summary>同步呼叫（請包在 Task.Run 裡），回傳 assistant 的完整回覆文字。</summary>
        public static string ChatCompletion(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            string url = settings.Endpoint.TrimEnd('/') + "/chat/completions";

            JArray messageArray = new JArray();
            foreach (AiChatMessage m in messages)
            {
                messageArray.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });
            }
            JObject body = new JObject
            {
                ["model"] = settings.Model,
                ["messages"] = messageArray
            };
            byte[] payload = Encoding.UTF8.GetBytes(body.ToString());

            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 120000;
            request.ReadWriteTimeout = 120000;
            request.UserAgent = "mySQLPunk-ai-assistant";
            System.Net.IWebProxy proxy = ConnectionProxySettingsService.CreateWebProxyFromOptions();
            if (proxy != null) request.Proxy = proxy;

            string apiKey;
            if (WindowsCredentialService.TryReadPassword(ApiKeyCredentialTarget, out apiKey)
                && !string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey.Trim();
            }

            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(payload, 0, payload.Length);
            }

            string responseText;
            try
            {
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    responseText = reader.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                // 盡量把服務端的錯誤訊息帶出來（額度、金鑰、模型名稱錯誤都在這裡）
                string detail = ex.Message;
                try
                {
                    if (ex.Response != null)
                    {
                        using (StreamReader reader = new StreamReader(ex.Response.GetResponseStream(), Encoding.UTF8))
                        {
                            string raw = reader.ReadToEnd();
                            JObject err = JObject.Parse(raw);
                            string msg = (string)(err["error"] is JObject errObj ? errObj["message"] : err["message"]);
                            if (!string.IsNullOrWhiteSpace(msg)) detail = msg;
                            else if (!string.IsNullOrWhiteSpace(raw)) detail = raw;
                        }
                    }
                }
                catch { }
                throw new InvalidOperationException(detail, ex);
            }

            JObject parsed = JObject.Parse(responseText);
            JToken choices = parsed["choices"];
            if (choices is JArray choiceArray && choiceArray.Count > 0)
            {
                JToken message = choiceArray[0]["message"];
                if (message != null)
                {
                    string content = (string)message["content"];
                    if (!string.IsNullOrEmpty(content)) return content.Trim();
                }
            }
            throw new InvalidOperationException("AI 服務回應了無法解析的內容: " + Truncate(responseText, 300));
        }

        /// <summary>取出回覆裡最後一段 ```sql 程式碼區塊（沒有語言標記的區塊也接受）。</summary>
        public static string ExtractLastSqlBlock(string reply)
        {
            if (string.IsNullOrEmpty(reply)) return null;
            string result = null;
            int index = 0;
            while (true)
            {
                int start = reply.IndexOf("```", index, StringComparison.Ordinal);
                if (start < 0) break;
                int lineEnd = reply.IndexOf('\n', start);
                if (lineEnd < 0) break;
                string lang = reply.Substring(start + 3, lineEnd - start - 3).Trim().ToLowerInvariant();
                int end = reply.IndexOf("```", lineEnd, StringComparison.Ordinal);
                if (end < 0) break;
                string block = reply.Substring(lineEnd + 1, end - lineEnd - 1).Trim();
                if (block.Length > 0 && (lang == "sql" || lang == "")) result = block;
                index = end + 3;
            }
            return result;
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max) + "…";
        }
    }
}
