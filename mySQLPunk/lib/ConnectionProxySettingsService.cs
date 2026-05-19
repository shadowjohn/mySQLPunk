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

    public static class ConnectionProxySettingsService
    {
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

        public static void ApplyTo(WebRequest request)
        {
            if (request == null) return;
            request.Proxy = CreateWebProxyFromOptions();
        }

        public static string BuildStatusText(ConnectionProxySettings settings)
        {
            if (settings == null || !settings.Enabled) return "Proxy disabled";
            if (string.IsNullOrWhiteSpace(settings.Host)) return "Proxy enabled but host is empty";
            if (!settings.IsHttpProxy) return "Proxy type is saved but not supported by WebRequest";
            int port = settings.Port <= 0 ? 8080 : settings.Port;
            return "HTTP proxy " + settings.Host.Trim() + ":" + port;
        }
    }
}
