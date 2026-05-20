using System;
using System.Net;

namespace mySQLPunk.lib
{
    public sealed class ConnectionProxySettings
    {
        public bool Enabled { get; set; }
        public string Type { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string UserName { get; set; }
        public string Password { get; set; }

        public bool IsHttpProxy
        {
            get
            {
                string type = (Type ?? string.Empty).Trim();
                return string.Equals(type, "http", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(type, "https", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public sealed class ConnectionProxyTestResult
    {
        public bool Success { get; set; }
        public bool AttemptedRequest { get; set; }
        public bool UsedProxy { get; set; }
        public string Message { get; set; }
        public string TargetUrl { get; set; }
        public int StatusCode { get; set; }
    }

    public static class ConnectionProxySettingsService
    {
        public static readonly Uri DefaultConnectivityTestUri = new Uri("https://www.google.com/generate_204");

        public static ConnectionProxySettings Load()
        {
            return new ConnectionProxySettings
            {
                Enabled = ApplicationOptionSettings.GetBool("ConnectionUseProxy"),
                Type = ApplicationOptionSettings.GetString("ConnectionProxyType"),
                Host = ApplicationOptionSettings.GetString("ConnectionProxyHost"),
                Port = ApplicationOptionSettings.GetInt("ConnectionProxyPort"),
                UserName = ApplicationOptionSettings.GetString("ConnectionProxyUser"),
                Password = ApplicationOptionSettings.GetString("ConnectionProxyPassword")
            };
        }

        public static IWebProxy CreateWebProxyFromOptions()
        {
            return CreateWebProxy(Load());
        }

        public static IWebProxy CreateWebProxy(ConnectionProxySettings settings)
        {
            if (settings == null || !settings.Enabled) return null;
            if (!settings.IsHttpProxy) return null;
            if (string.IsNullOrWhiteSpace(settings.Host)) return null;

            int port = settings.Port <= 0 ? 8080 : settings.Port;
            UriBuilder builder = new UriBuilder("http", settings.Host.Trim(), port);
            WebProxy proxy = new WebProxy(builder.Uri);

            if (!string.IsNullOrWhiteSpace(settings.UserName))
            {
                proxy.Credentials = new NetworkCredential(settings.UserName, settings.Password ?? string.Empty);
            }

            return proxy;
        }

        public static HttpWebRequest CreateConnectivityTestRequest(ConnectionProxySettings settings, Uri targetUri, int timeoutMilliseconds)
        {
            Uri uri = targetUri ?? DefaultConnectivityTestUri;
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = "HEAD";
            request.Timeout = Math.Max(1000, timeoutMilliseconds);
            request.ReadWriteTimeout = Math.Max(1000, timeoutMilliseconds);
            request.AllowAutoRedirect = false;
            request.UserAgent = "mySQLPunk connectivity test";
            request.Proxy = CreateWebProxy(settings);
            return request;
        }

        public static ConnectionProxyTestResult TestConnectivity(ConnectionProxySettings settings, Uri targetUri, int timeoutMilliseconds)
        {
            Uri uri = targetUri ?? DefaultConnectivityTestUri;
            ConnectionProxyTestResult preflight = ValidateConnectivityTest(settings, uri);
            if (!preflight.Success) return preflight;

            HttpWebRequest request = CreateConnectivityTestRequest(settings, uri, timeoutMilliseconds);
            try
            {
                using (HttpWebResponse response = (HttpWebResponse)request.GetResponse())
                {
                    int statusCode = (int)response.StatusCode;
                    bool ok = statusCode >= 200 && statusCode < 400;
                    return new ConnectionProxyTestResult
                    {
                        Success = ok,
                        AttemptedRequest = true,
                        UsedProxy = settings != null && settings.Enabled && settings.IsHttpProxy && !string.IsNullOrWhiteSpace(settings.Host),
                        TargetUrl = uri.ToString(),
                        StatusCode = statusCode,
                        Message = ok
                            ? "Connectivity test succeeded (" + statusCode + ")."
                            : "Connectivity test returned HTTP " + statusCode + "."
                    };
                }
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                int statusCode = response == null ? 0 : (int)response.StatusCode;
                if (response != null) response.Close();
                return new ConnectionProxyTestResult
                {
                    Success = false,
                    AttemptedRequest = true,
                    UsedProxy = settings != null && settings.Enabled && settings.IsHttpProxy && !string.IsNullOrWhiteSpace(settings.Host),
                    TargetUrl = uri.ToString(),
                    StatusCode = statusCode,
                    Message = statusCode > 0
                        ? "Connectivity test failed with HTTP " + statusCode + ": " + ex.Message
                        : "Connectivity test failed: " + ex.Message
                };
            }
            catch (Exception ex)
            {
                return new ConnectionProxyTestResult
                {
                    Success = false,
                    AttemptedRequest = true,
                    UsedProxy = settings != null && settings.Enabled && settings.IsHttpProxy && !string.IsNullOrWhiteSpace(settings.Host),
                    TargetUrl = uri.ToString(),
                    Message = "Connectivity test failed: " + ex.Message
                };
            }
        }

        public static ConnectionProxyTestResult ValidateConnectivityTest(ConnectionProxySettings settings, Uri targetUri)
        {
            Uri uri = targetUri ?? DefaultConnectivityTestUri;
            if (settings == null || !settings.Enabled)
            {
                return new ConnectionProxyTestResult
                {
                    Success = true,
                    AttemptedRequest = false,
                    UsedProxy = false,
                    TargetUrl = uri.ToString(),
                    Message = "Proxy disabled; connectivity test will use the direct connection."
                };
            }

            if (string.IsNullOrWhiteSpace(settings.Host))
            {
                return new ConnectionProxyTestResult
                {
                    Success = false,
                    AttemptedRequest = false,
                    UsedProxy = false,
                    TargetUrl = uri.ToString(),
                    Message = "Proxy host is empty."
                };
            }

            if (!settings.IsHttpProxy)
            {
                return new ConnectionProxyTestResult
                {
                    Success = false,
                    AttemptedRequest = false,
                    UsedProxy = false,
                    TargetUrl = uri.ToString(),
                    Message = "SOCKS5 proxy settings are saved, but WebRequest connectivity tests currently support HTTP/HTTPS proxies only."
                };
            }

            return new ConnectionProxyTestResult
            {
                Success = true,
                AttemptedRequest = false,
                UsedProxy = true,
                TargetUrl = uri.ToString(),
                Message = BuildStatusText(settings)
            };
        }

        public static void ApplyTo(WebRequest request)
        {
            if (request == null) return;
            request.Proxy = CreateWebProxyFromOptions();
        }

        public static string BuildStatusText(ConnectionProxySettings settings)
        {
            if (settings == null || !settings.Enabled) return "Proxy disabled";
            if (string.IsNullOrWhiteSpace(settings.Host)) return "Proxy enabled but host is empty";
            if (!settings.IsHttpProxy) return "SOCKS5 proxy settings are saved but not supported by WebRequest";
            int port = settings.Port <= 0 ? 8080 : settings.Port;
            return "HTTP proxy " + settings.Host.Trim() + ":" + port;
        }
    }
}
