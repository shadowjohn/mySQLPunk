using System;
using System.Globalization;
using System.Threading;

namespace mySQLPunk.lib
{
    public sealed class RedisPubSubMessageEventArgs : EventArgs
    {
        public DateTime ReceivedAtUtc { get; internal set; }
        public string Pattern { get; internal set; }
        public string Channel { get; internal set; }
        public string Message { get; internal set; }
    }

    public sealed class RedisPubSubErrorEventArgs : EventArgs
    {
        public Exception Error { get; internal set; }
    }

    /// <summary>
    /// 單一 Redis channel 或 pattern 的接收工作。訂閱後只在背景執行緒讀取專用連線，
    /// 不與 provider 的一般查詢連線共用 socket。
    /// </summary>
    public sealed class RedisPubSubSubscription : IDisposable
    {
        private readonly RedisRespClient _client;
        private readonly object _sync = new object();
        private Thread _reader;
        private volatile bool _disposed;

        private RedisPubSubSubscription(RedisRespClient client, string topic, bool pattern)
        {
            _client = client;
            Topic = topic;
            IsPattern = pattern;
        }

        public string Topic { get; private set; }
        public bool IsPattern { get; private set; }

        public event EventHandler<RedisPubSubMessageEventArgs> MessageReceived;
        public event EventHandler<RedisPubSubErrorEventArgs> Failed;

        internal static RedisPubSubSubscription Create(RedisRespClient client, string topic, bool pattern)
        {
            if (client == null) throw new ArgumentNullException("client");
            if (string.IsNullOrWhiteSpace(topic)) throw new ArgumentException(Localization.T("Redis.PubSubTopicRequired"), "topic");

            string command = pattern ? "PSUBSCRIBE" : "SUBSCRIBE";
            object[] acknowledgement = client.Execute(command, topic) as object[];
            if (acknowledgement == null || acknowledgement.Length != 3 ||
                !string.Equals(Convert.ToString(acknowledgement[0], CultureInfo.InvariantCulture), command.ToLowerInvariant(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Convert.ToString(acknowledgement[1], CultureInfo.InvariantCulture), topic, StringComparison.Ordinal))
                throw new FormatException(Localization.T("Redis.PubSubInvalidReply"));

            return new RedisPubSubSubscription(client, topic, pattern);
        }

        public void Start()
        {
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(GetType().Name);
                if (_reader != null) return;
                _reader = new Thread(ReadLoop)
                {
                    IsBackground = true,
                    Name = "mySQLPunk Redis PubSub"
                };
                _reader.Start();
            }
        }

        private void ReadLoop()
        {
            try
            {
                while (!_disposed)
                {
                    object[] reply = _client.ReadReply() as object[];
                    RedisPubSubMessageEventArgs message = ParseMessage(reply);
                    if (message == null) continue;
                    EventHandler<RedisPubSubMessageEventArgs> handler = MessageReceived;
                    if (handler != null) handler(this, message);
                }
            }
            catch (Exception ex)
            {
                if (_disposed) return;
                EventHandler<RedisPubSubErrorEventArgs> handler = Failed;
                if (handler != null) handler(this, new RedisPubSubErrorEventArgs { Error = ex });
            }
        }

        internal static RedisPubSubMessageEventArgs ParseMessage(object[] reply)
        {
            if (reply == null || reply.Length < 3) return null;
            string kind = Convert.ToString(reply[0], CultureInfo.InvariantCulture);
            if (string.Equals(kind, "message", StringComparison.OrdinalIgnoreCase) && reply.Length == 3)
            {
                return new RedisPubSubMessageEventArgs
                {
                    ReceivedAtUtc = DateTime.UtcNow,
                    Pattern = string.Empty,
                    Channel = Convert.ToString(reply[1], CultureInfo.InvariantCulture),
                    Message = Convert.ToString(reply[2], CultureInfo.InvariantCulture)
                };
            }
            if (string.Equals(kind, "pmessage", StringComparison.OrdinalIgnoreCase) && reply.Length == 4)
            {
                return new RedisPubSubMessageEventArgs
                {
                    ReceivedAtUtc = DateTime.UtcNow,
                    Pattern = Convert.ToString(reply[1], CultureInfo.InvariantCulture),
                    Channel = Convert.ToString(reply[2], CultureInfo.InvariantCulture),
                    Message = Convert.ToString(reply[3], CultureInfo.InvariantCulture)
                };
            }
            return null;
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
                _client.Dispose();
            }
        }
    }
}
