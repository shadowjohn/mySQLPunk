using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using mySQLPunk;

namespace mySQLPunk.lib
{
    /// <summary>
    /// 「跳瀏覽器授權、自動拿金鑰」的 CLI 式認證。
    /// 大多數模型商（OpenAI / Anthropic / Groq…）不開放第三方桌面程式做 OAuth，
    /// 只能手動貼 API key；OpenRouter 有官方 PKCE 流程，而且一把鑰匙可用各家模型，
    /// 所以一鍵授權先做它。流程：
    ///   1. 本機開一個 loopback 回呼埠（TcpListener，不需要管理員權限）
    ///   2. 開瀏覽器到 openrouter.ai/auth 讓使用者按同意
    ///   3. 瀏覽器帶著 code 轉回本機，用 PKCE code_verifier 換正式金鑰
    ///   4. 金鑰直接存進 Windows 認證管理員
    /// </summary>
    public static class AiOAuthService
    {
        /// <summary>整段授權流程（同步阻塞，請包在 Task.Run 裡）。成功回傳金鑰並已存好。</summary>
        public static string ConnectOpenRouter(int timeoutSeconds = 180)
        {
            // PKCE：code_verifier 留在本機，網址上只放 SHA-256 過的 challenge
            string codeVerifier = CreateCodeVerifier();
            string codeChallenge = CreateCodeChallenge(codeVerifier);

            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                int port = ((IPEndPoint)listener.LocalEndpoint).Port;
                string callbackUrl = "http://127.0.0.1:" + port + "/callback";
                string authUrl = "https://openrouter.ai/auth?callback_url=" + Uri.EscapeDataString(callbackUrl)
                    + "&code_challenge=" + codeChallenge
                    + "&code_challenge_method=S256";

                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(authUrl) { UseShellExecute = true });

                string code = WaitForCallbackCode(listener, timeoutSeconds);
                if (string.IsNullOrWhiteSpace(code))
                {
                    throw new InvalidOperationException(Localization.T("Ai.OAuthNoCode"));
                }

                string apiKey = ExchangeCodeForKey(code, codeVerifier);
                if (string.IsNullOrWhiteSpace(apiKey))
                {
                    throw new InvalidOperationException(Localization.T("Ai.OAuthNoKey"));
                }

                WindowsCredentialService.TryWritePassword(AiChatService.ApiKeyTargetFor("openrouter"), "ai", apiKey);
                return apiKey;
            }
            finally
            {
                try { listener.Stop(); } catch { }
            }
        }

        /// <summary>等瀏覽器把授權碼帶回本機回呼埠，並回一頁「完成」給瀏覽器。</summary>
        private static string WaitForCallbackCode(TcpListener listener, int timeoutSeconds)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline)
            {
                if (!listener.Pending())
                {
                    System.Threading.Thread.Sleep(200);
                    continue;
                }
                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    client.ReceiveTimeout = 5000;
                    string requestLine = ReadLine(stream);
                    // 形如 GET /callback?code=xxx HTTP/1.1
                    string code = null;
                    if (requestLine != null && requestLine.StartsWith("GET ", StringComparison.Ordinal))
                    {
                        int pathStart = 4;
                        int pathEnd = requestLine.IndexOf(' ', pathStart);
                        string path = pathEnd > pathStart ? requestLine.Substring(pathStart, pathEnd - pathStart) : "";
                        int q = path.IndexOf("code=", StringComparison.Ordinal);
                        if (q >= 0)
                        {
                            string value = path.Substring(q + 5);
                            int amp = value.IndexOf('&');
                            if (amp >= 0) value = value.Substring(0, amp);
                            code = Uri.UnescapeDataString(value);
                        }
                    }

                    string page = code != null
                        ? "<html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif;text-align:center;padding-top:80px\">"
                          + "<h2>" + Localization.T("Ai.OAuthDoneTitle") + "</h2><p>" + Localization.T("Ai.OAuthDoneBody") + "</p></body></html>"
                        : "<html><meta charset=\"utf-8\"><body style=\"font-family:sans-serif;text-align:center;padding-top:80px\">"
                          + "<h2>mySQLPunk</h2></body></html>";
                    byte[] body = Encoding.UTF8.GetBytes(page);
                    string header = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
                    byte[] headerBytes = Encoding.ASCII.GetBytes(header);
                    stream.Write(headerBytes, 0, headerBytes.Length);
                    stream.Write(body, 0, body.Length);
                    stream.Flush();
                    // 給瀏覽器一點時間把回應讀完再關線，不然完成頁可能顯示不出來
                    try { client.Client.Shutdown(SocketShutdown.Send); } catch { }
                    System.Threading.Thread.Sleep(150);

                    // 瀏覽器可能先來要 favicon，沒帶 code 就繼續等下一個請求
                    if (code != null) return code;
                }
            }
            return null;
        }

        private static string ReadLine(NetworkStream stream)
        {
            StringBuilder sb = new StringBuilder();
            int b;
            while ((b = stream.ReadByte()) >= 0)
            {
                if (b == '\n') break;
                if (b != '\r') sb.Append((char)b);
                if (sb.Length > 4096) break;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        /// <summary>拿授權碼 + code_verifier 跟 OpenRouter 換正式 API 金鑰。</summary>
        private static string ExchangeCodeForKey(string code, string codeVerifier)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create("https://openrouter.ai/api/v1/auth/keys");
            request.Method = "POST";
            request.ContentType = "application/json";
            request.Timeout = 30000;
            request.UserAgent = "mySQLPunk-ai-assistant";
            IWebProxy proxy = ConnectionProxySettingsService.CreateWebProxyFromOptions();
            if (proxy != null) request.Proxy = proxy;

            JObject body = new JObject
            {
                ["code"] = code,
                ["code_verifier"] = codeVerifier,
                ["code_challenge_method"] = "S256"
            };
            byte[] payload = Encoding.UTF8.GetBytes(body.ToString());
            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(payload, 0, payload.Length);
            }
            using (WebResponse response = request.GetResponse())
            using (StreamReader reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                JObject parsed = JObject.Parse(reader.ReadToEnd());
                return (string)parsed["key"];
            }
        }

        private static string CreateCodeVerifier()
        {
            byte[] bytes = new byte[64];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            return Base64Url(bytes);
        }

        private static string CreateCodeChallenge(string verifier)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return Base64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
            }
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }
    }
}
