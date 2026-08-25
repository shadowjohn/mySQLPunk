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

    /// <summary>一個 AI 服務供應商的預設組態。使用者訂閱哪家就選哪家。</summary>
    public class AiProviderPreset
    {
        public string Id;
        public string DisplayName;
        public string Endpoint;
        public string DefaultModel;
        /// <summary>bearer（Authorization: Bearer）、x-api-key（Anthropic）、api-key（Azure）、none（本機服務）</summary>
        public string AuthStyle;
        public bool NeedsKey;
        /// <summary>取得金鑰／註冊認證的網頁（本機服務則是下載頁），讓使用者一鍵跳過去。</summary>
        public string KeySignupUrl;

        public AiProviderPreset(string id, string displayName, string endpoint, string defaultModel, string authStyle, bool needsKey, string keySignupUrl)
        {
            Id = id; DisplayName = displayName; Endpoint = endpoint;
            DefaultModel = defaultModel; AuthStyle = authStyle; NeedsKey = needsKey;
            KeySignupUrl = keySignupUrl;
        }
    }

    /// <summary>AI 助理的服務設定。除 Anthropic 走原生 Messages API 外，其餘都走 OpenAI 相容介面。</summary>
    public class AiChatSettings
    {
        public string Provider;
        public string Endpoint;
        public string Model;

        public AiProviderPreset Preset
        {
            get { return AiChatService.FindPreset(Provider); }
        }

        public static AiChatSettings Load()
        {
            AiChatSettings s = new AiChatSettings
            {
                Provider = ApplicationOptionSettings.GetString("AiProvider"),
                Endpoint = (ApplicationOptionSettings.GetString("AiEndpoint") ?? "").Trim(),
                Model = (ApplicationOptionSettings.GetString("AiModel") ?? "").Trim()
            };
            if (string.IsNullOrWhiteSpace(s.Provider)) s.Provider = "openai";

            AiProviderPreset preset = AiChatService.FindPreset(s.Provider);
            if (string.IsNullOrWhiteSpace(s.Endpoint)) s.Endpoint = preset.Endpoint;
            if (string.IsNullOrWhiteSpace(s.Model)) s.Model = preset.DefaultModel;
            return s;
        }
    }

    public static class AiChatService
    {
        /// <summary>舊版單一金鑰的儲存位置（相容用）。</summary>
        public const string LegacyApiKeyCredentialTarget = "mySQLPunk:ai-api-key";

        /// <summary>
        /// 支援的供應商清單。使用者訂閱哪家就選哪家；本機服務（Ollama / LM Studio）不用金鑰。
        /// GitHub Models 官方已進入退場期，僅保留相容選項。
        /// </summary>
        public static readonly AiProviderPreset[] Presets = new[]
        {
            new AiProviderPreset("openai",    "OpenAI",                 "https://api.openai.com/v1",                                  "gpt-4o-mini",               "bearer",    true,  "https://platform.openai.com/api-keys"),
            new AiProviderPreset("anthropic", "Anthropic Claude",       "https://api.anthropic.com",                                  "claude-haiku-4-5",          "x-api-key", true,  "https://console.anthropic.com/settings/keys"),
            new AiProviderPreset("gemini",    "Google Gemini",          "https://generativelanguage.googleapis.com/v1beta/openai",    "gemini-2.0-flash",          "bearer",    true,  "https://aistudio.google.com/apikey"),
            new AiProviderPreset("azure",     "Azure OpenAI",           "",                                                            "",                          "api-key",   true,  "https://portal.azure.com"),
            new AiProviderPreset("openrouter","OpenRouter",             "https://openrouter.ai/api/v1",                               "openai/gpt-4o-mini",        "bearer",    true,  "https://openrouter.ai/settings/keys"),
            new AiProviderPreset("groq",      "Groq",                   "https://api.groq.com/openai/v1",                             "llama-3.3-70b-versatile",   "bearer",    true,  "https://console.groq.com/keys"),
            new AiProviderPreset("deepseek",  "DeepSeek",               "https://api.deepseek.com/v1",                                "deepseek-chat",             "bearer",    true,  "https://platform.deepseek.com/api_keys"),
            new AiProviderPreset("xai",       "xAI Grok",               "https://api.x.ai/v1",                                        "grok-3-mini",               "bearer",    true,  "https://console.x.ai"),
            new AiProviderPreset("ollama",    "Ollama（本機）",          "http://localhost:11434/v1",                                  "llama3.1",                  "none",      false, "https://ollama.com/download"),
            new AiProviderPreset("lmstudio",  "LM Studio（本機）",       "http://localhost:1234/v1",                                   "",                          "none",      false, "https://lmstudio.ai"),
            new AiProviderPreset("github",    "GitHub Models（退場中）", "https://models.github.ai/inference",                         "openai/gpt-4o-mini",        "bearer",    true,  "https://github.com/settings/tokens"),
            new AiProviderPreset("custom",    "自訂 OpenAI 相容端點",    "",                                                            "",                          "bearer",    true,  ""),
        };

        public static AiProviderPreset FindPreset(string providerId)
        {
            foreach (AiProviderPreset p in Presets)
            {
                if (string.Equals(p.Id, providerId, StringComparison.OrdinalIgnoreCase)) return p;
            }
            return Presets[0];
        }

        // ── 金鑰：一家一把，存 Windows 認證管理員 ────────────────────

        public static string ApiKeyTargetFor(string providerId)
        {
            return "mySQLPunk:ai-api-key:" + (providerId ?? "").ToLowerInvariant();
        }

        public static bool TryReadApiKey(string providerId, out string apiKey)
        {
            if (WindowsCredentialService.TryReadPassword(ApiKeyTargetFor(providerId), out apiKey)
                && !string.IsNullOrWhiteSpace(apiKey)) return true;
            // 相容 1.0.0.9 之前的單一金鑰
            return WindowsCredentialService.TryReadPassword(LegacyApiKeyCredentialTarget, out apiKey)
                && !string.IsNullOrWhiteSpace(apiKey);
        }

        public static bool HasApiKey(string providerId)
        {
            string key;
            return TryReadApiKey(providerId, out key);
        }

        // ── 對話 ────────────────────────────────────────────────────

        /// <summary>同步呼叫（請包在 Task.Run 裡），回傳 assistant 的完整回覆文字。</summary>
        public static string ChatCompletion(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            if (string.Equals(settings.Provider, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                return AnthropicChat(settings, messages);
            }
            return OpenAiCompatibleChat(settings, messages);
        }

        private static string OpenAiCompatibleChat(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            string baseUrl = settings.Endpoint.TrimEnd('/');
            string url;
            if (string.Equals(settings.Provider, "azure", StringComparison.OrdinalIgnoreCase))
            {
                // Azure 的端點填到 deployment 為止，例如
                // https://res.openai.azure.com/openai/deployments/gpt-4o-mini
                url = baseUrl + "/chat/completions";
                if (url.IndexOf("api-version=", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    url += "?api-version=2024-06-01";
                }
            }
            else
            {
                url = baseUrl + "/chat/completions";
            }

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

            string responseText = PostJson(settings, url, body.ToString());
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

        /// <summary>Anthropic 原生 Messages API：system 獨立欄位、回覆在 content 陣列裡。</summary>
        private static string AnthropicChat(AiChatSettings settings, IList<AiChatMessage> messages)
        {
            string url = settings.Endpoint.TrimEnd('/') + "/v1/messages";

            StringBuilder system = new StringBuilder();
            JArray messageArray = new JArray();
            foreach (AiChatMessage m in messages)
            {
                if (m.Role == "system")
                {
                    if (system.Length > 0) system.AppendLine();
                    system.Append(m.Content);
                }
                else
                {
                    messageArray.Add(new JObject { ["role"] = m.Role, ["content"] = m.Content });
                }
            }
            JObject body = new JObject
            {
                ["model"] = settings.Model,
                ["max_tokens"] = 4096,
                ["messages"] = messageArray
            };
            if (system.Length > 0) body["system"] = system.ToString();

            string responseText = PostJson(settings, url, body.ToString());
            JObject parsed = JObject.Parse(responseText);
            if (parsed["content"] is JArray blocks)
            {
                StringBuilder sb = new StringBuilder();
                foreach (JToken block in blocks)
                {
                    if ((string)block["type"] == "text") sb.Append((string)block["text"]);
                }
                if (sb.Length > 0) return sb.ToString().Trim();
            }
            throw new InvalidOperationException("AI 服務回應了無法解析的內容: " + Truncate(responseText, 300));
        }

        // ── 模型清單與本機偵測（「看使用者支援什麼」）──────────────

        /// <summary>跟服務要可用的模型清單（同時當「測試連線」用：能拿到清單代表端點與金鑰都通）。</summary>
        public static List<string> ListModels(AiChatSettings settings)
        {
            List<string> models = new List<string>();
            string baseUrl = settings.Endpoint.TrimEnd('/');
            string url = string.Equals(settings.Provider, "anthropic", StringComparison.OrdinalIgnoreCase)
                ? baseUrl + "/v1/models"
                : baseUrl + "/models";

            string responseText = GetJson(settings, url);
            JObject parsed = JObject.Parse(responseText);
            if (parsed["data"] is JArray data)
            {
                foreach (JToken item in data)
                {
                    string id = (string)item["id"];
                    if (!string.IsNullOrWhiteSpace(id)) models.Add(id);
                }
            }
            models.Sort(StringComparer.OrdinalIgnoreCase);
            return models;
        }

        /// <summary>偵測本機推論服務（Ollama / LM Studio），回傳偵測到的供應商與其模型。</summary>
        public static List<KeyValuePair<AiProviderPreset, List<string>>> DetectLocalServices()
        {
            var found = new List<KeyValuePair<AiProviderPreset, List<string>>>();
            foreach (string id in new[] { "ollama", "lmstudio" })
            {
                AiProviderPreset preset = FindPreset(id);
                try
                {
                    AiChatSettings probe = new AiChatSettings { Provider = id, Endpoint = preset.Endpoint, Model = preset.DefaultModel };
                    List<string> models = ListModels(probe);
                    found.Add(new KeyValuePair<AiProviderPreset, List<string>>(preset, models));
                }
                catch
                {
                    // 沒開就跳過
                }
            }
            return found;
        }

        // ── HTTP 底層 ────────────────────────────────────────────────

        private static HttpWebRequest CreateRequest(AiChatSettings settings, string url, string method)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.ContentType = "application/json";
            request.Timeout = method == "GET" ? 8000 : 120000;
            request.ReadWriteTimeout = 120000;
            request.UserAgent = "mySQLPunk-ai-assistant";
            System.Net.IWebProxy proxy = ConnectionProxySettingsService.CreateWebProxyFromOptions();
            if (proxy != null) request.Proxy = proxy;

            AiProviderPreset preset = settings.Preset;
            string apiKey;
            bool hasKey = TryReadApiKey(settings.Provider, out apiKey);
            if (hasKey) apiKey = apiKey.Trim();
            switch (preset.AuthStyle)
            {
                case "x-api-key":
                    if (hasKey) request.Headers["x-api-key"] = apiKey;
                    request.Headers["anthropic-version"] = "2023-06-01";
                    break;
                case "api-key":
                    if (hasKey) request.Headers["api-key"] = apiKey;
                    break;
                case "none":
                    break;
                default:
                    if (hasKey) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + apiKey;
                    break;
            }
            return request;
        }

        private static string PostJson(AiChatSettings settings, string url, string json)
        {
            HttpWebRequest request = CreateRequest(settings, url, "POST");
            byte[] payload = Encoding.UTF8.GetBytes(json);
            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(payload, 0, payload.Length);
            }
            return ReadResponse(request);
        }

        private static string GetJson(AiChatSettings settings, string url)
        {
            HttpWebRequest request = CreateRequest(settings, url, "GET");
            return ReadResponse(request);
        }

        private static string ReadResponse(HttpWebRequest request)
        {
            try
            {
                using (WebResponse response = request.GetResponse())
                using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    return reader.ReadToEnd();
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
