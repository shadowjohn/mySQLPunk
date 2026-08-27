using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace mySQLPunk.lib
{
    /// <summary>Redis 伺服器回覆的錯誤（RESP error reply）。</summary>
    public sealed class RedisServerException : Exception
    {
        public RedisServerException(string message) : base(message) { }
    }

    /// <summary>
    /// RESP2 協定的組包與解析。獨立成純邏輯類別，smoke test 不需要真的 Redis 伺服器就能驗證。
    /// Redis 與 Microsoft Garnet 都使用同一套 wire protocol。
    /// </summary>
    public static class RedisRespProtocol
    {
        private const int MaxBulkStringLength = 64 * 1024 * 1024;
        private const int MaxArrayLength = 1000000;
        private const int MaxNestingDepth = 64;
        private const int MaxInlineLength = 1024 * 1024;

        public static byte[] BuildCommand(IList<string> args)
        {
            if (args == null || args.Count == 0) throw new ArgumentException("args");
            if (args.Count > MaxArrayLength) throw new ArgumentException(Localization.T("Redis.ProtocolTooLarge"), "args");
            using (MemoryStream buffer = new MemoryStream())
            {
                byte[] head = Encoding.UTF8.GetBytes("*" + args.Count.ToString(CultureInfo.InvariantCulture) + "\r\n");
                buffer.Write(head, 0, head.Length);
                foreach (string arg in args)
                {
                    byte[] payload = Encoding.UTF8.GetBytes(arg ?? string.Empty);
                    if (payload.Length > MaxBulkStringLength)
                        throw new ArgumentException(Localization.T("Redis.ProtocolTooLarge"), "args");
                    byte[] prefix = Encoding.UTF8.GetBytes("$" + payload.Length.ToString(CultureInfo.InvariantCulture) + "\r\n");
                    buffer.Write(prefix, 0, prefix.Length);
                    buffer.Write(payload, 0, payload.Length);
                    buffer.WriteByte((byte)'\r');
                    buffer.WriteByte((byte)'\n');
                }
                return buffer.ToArray();
            }
        }

        /// <summary>
        /// 讀取一則完整回覆：+simple → string、-error → 丟 RedisServerException、
        /// :integer → long、$bulk → string（$-1 → null）、*array → object[]（*-1 → null）。
        /// </summary>
        public static object ReadReply(Stream stream)
        {
            return ReadReply(stream, 0);
        }

        private static object ReadReply(Stream stream, int depth)
        {
            if (depth > MaxNestingDepth)
                throw new FormatException(Localization.T("Redis.ProtocolTooDeep"));
            int typeByte = ReadByteStrict(stream);
            string line = ReadLine(stream);
            switch ((char)typeByte)
            {
                case '+':
                    return line;
                case '-':
                    throw new RedisServerException(line);
                case ':':
                    return long.Parse(line, CultureInfo.InvariantCulture);
                case '$':
                    {
                        int length = int.Parse(line, CultureInfo.InvariantCulture);
                        if (length == -1) return null;
                        if (length < -1) throw new FormatException(Localization.T("Redis.ProtocolInvalidLength"));
                        if (length > MaxBulkStringLength)
                            throw new FormatException(Localization.T("Redis.ProtocolTooLarge"));
                        byte[] payload = ReadExact(stream, length);
                        ReadLineTerminator(stream);
                        return Encoding.UTF8.GetString(payload);
                    }
                case '*':
                    {
                        int count = int.Parse(line, CultureInfo.InvariantCulture);
                        if (count == -1) return null;
                        if (count < -1) throw new FormatException(Localization.T("Redis.ProtocolInvalidLength"));
                        if (count > MaxArrayLength)
                            throw new FormatException(Localization.T("Redis.ProtocolTooLarge"));
                        object[] items = new object[count];
                        for (int i = 0; i < count; i++) items[i] = ReadReply(stream, depth + 1);
                        return items;
                    }
                default:
                    throw new FormatException(Localization.Format("Redis.ProtocolUnexpectedType", ((char)typeByte).ToString()));
            }
        }

        private static int ReadByteStrict(Stream stream)
        {
            int value = stream.ReadByte();
            if (value < 0) throw new EndOfStreamException(Localization.T("Redis.ProtocolUnexpectedEnd"));
            return value;
        }

        private static string ReadLine(Stream stream)
        {
            using (MemoryStream line = new MemoryStream())
            {
                while (true)
                {
                    int value = ReadByteStrict(stream);
                    if (value == '\r')
                    {
                        int next = ReadByteStrict(stream);
                        if (next != '\n') throw new FormatException(Localization.T("Redis.ProtocolUnexpectedEnd"));
                        return Encoding.UTF8.GetString(line.ToArray());
                    }
                    if (line.Length >= MaxInlineLength)
                        throw new FormatException(Localization.T("Redis.ProtocolTooLarge"));
                    line.WriteByte((byte)value);
                }
            }
        }

        private static void ReadLineTerminator(Stream stream)
        {
            if (ReadByteStrict(stream) != '\r' || ReadByteStrict(stream) != '\n')
                throw new FormatException(Localization.T("Redis.ProtocolUnexpectedEnd"));
        }

        private static byte[] ReadExact(Stream stream, int length)
        {
            byte[] buffer = new byte[length];
            int offset = 0;
            while (offset < length)
            {
                int read = stream.Read(buffer, offset, length - offset);
                if (read <= 0) throw new EndOfStreamException(Localization.T("Redis.ProtocolUnexpectedEnd"));
                offset += read;
            }
            return buffer;
        }
    }

    /// <summary>
    /// 最小可用的同步 Redis 客戶端：TCP（可選 TLS）、AUTH、逐一命令往返。
    /// 只給 my_redis 內部使用；呼叫端負責序列化（my_redis 以 lock 保護）。
    /// </summary>
    internal sealed class RedisRespClient : IDisposable
    {
        private readonly TcpClient _tcp;
        private readonly Stream _stream;

        private RedisRespClient(TcpClient tcp, Stream stream)
        {
            _tcp = tcp;
            _stream = stream;
        }

        public static RedisRespClient Connect(string host, int port, bool useTls, int timeoutMs)
        {
            TcpClient tcp = new TcpClient();
            try
            {
                IAsyncResult connect = tcp.BeginConnect(host, port, null, null);
                WaitHandle waitHandle = connect.AsyncWaitHandle;
                try
                {
                    if (!waitHandle.WaitOne(timeoutMs))
                        throw new TimeoutException(Localization.Format("Redis.ConnectTimeout", host, port));
                }
                finally
                {
                    waitHandle.Close();
                }
                tcp.EndConnect(connect);
                tcp.ReceiveTimeout = timeoutMs;
                tcp.SendTimeout = timeoutMs;

                Stream stream = tcp.GetStream();
                if (useTls)
                {
                    SslStream ssl = new SslStream(stream, false);
                    ssl.AuthenticateAsClient(host);
                    stream = ssl;
                }
                return new RedisRespClient(tcp, stream);
            }
            catch
            {
                try { tcp.Close(); } catch { }
                throw;
            }
        }

        public object Execute(params string[] args)
        {
            byte[] command = RedisRespProtocol.BuildCommand(args);
            _stream.Write(command, 0, command.Length);
            _stream.Flush();
            return RedisRespProtocol.ReadReply(_stream);
        }

        public void Dispose()
        {
            try { _stream.Dispose(); } catch { }
            try { _tcp.Close(); } catch { }
        }
    }
}
