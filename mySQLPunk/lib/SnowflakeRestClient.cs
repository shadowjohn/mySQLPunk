using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace mySQLPunk.lib
{
    /// <summary>Snowflake SQL API 回覆的錯誤（HTTP 4xx/5xx 或 statement 失敗）。</summary>
    public sealed class SnowflakeServerException : Exception
    {
        public string Code { get; private set; }

        public SnowflakeServerException(string message, string code) : base(message)
        {
            Code = code ?? string.Empty;
        }
    }

    /// <summary>單一 statement 的結果集：欄位名稱／型別與字串值列（SQL API JSON 一律以字串傳值）。</summary>
    public sealed class SnowflakeStatementResult
    {
        public readonly List<string> ColumnNames = new List<string>();
        public readonly List<string> ColumnTypes = new List<string>();
        public readonly List<object[]> Rows = new List<object[]>();
        public long NumRows;
        public int PartitionCount = 1;
        public string StatementHandle = string.Empty;
    }

    /// <summary>
    /// Snowflake SQL REST API v2 的最小同步 client：POST statements、202 輪詢與多 partition 讀取。
    /// 驗證使用 PAT 或 OAuth bearer token；不引入官方驅動以避免 net472 packages.config 無法承受的相依樹。
    /// </summary>
    public sealed class SnowflakeRestClient : IDisposable
    {
        private const int StatementTimeoutSeconds = 60;
        private const int PollIntervalMs = 500;
        private const int MaxPollAttempts = 60;

        private readonly HttpClient _http;
        private readonly string _baseUrl;

        public SnowflakeRestClient(string baseUrl, string token, string tokenType)
        {
            // net472 預設協定可能不含 TLS 1.2；Snowflake 端點強制要求。
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            _baseUrl = baseUrl.TrimEnd('/');
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _http.DefaultRequestHeaders.Add("X-Snowflake-Authorization-Token-Type", tokenType);
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("mySQLPunk/1.0");
        }

        public SnowflakeStatementResult ExecuteStatement(string sql, string database, string schema, string warehouse, string role)
        {
            JObject body = new JObject
            {
                ["statement"] = sql,
                ["timeout"] = StatementTimeoutSeconds
            };
            if (!string.IsNullOrWhiteSpace(database)) body["database"] = database;
            if (!string.IsNullOrWhiteSpace(schema)) body["schema"] = schema;
            if (!string.IsNullOrWhiteSpace(warehouse)) body["warehouse"] = warehouse;
            if (!string.IsNullOrWhiteSpace(role)) body["role"] = role;

            string responseText = Send(() => new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/api/v2/statements")
            {
                Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json")
            }, out HttpStatusCode status);

            // 202：statement 仍在執行，用 handle 輪詢到完成為止。
            int polls = 0;
            while (status == HttpStatusCode.Accepted)
            {
                if (++polls > MaxPollAttempts)
                    throw new SnowflakeServerException(Localization.T("Snowflake.StatementTimeout"), "");
                string handle = ReadHandle(responseText);
                Thread.Sleep(PollIntervalMs);
                responseText = Send(() => new HttpRequestMessage(HttpMethod.Get,
                    _baseUrl + "/api/v2/statements/" + Uri.EscapeDataString(handle)), out status);
            }

            SnowflakeStatementResult result = ParseStatementResult(responseText);
            for (int partition = 1; partition < result.PartitionCount; partition++)
            {
                int index = partition;
                string partitionText = Send(() => new HttpRequestMessage(HttpMethod.Get,
                    _baseUrl + "/api/v2/statements/" + Uri.EscapeDataString(result.StatementHandle)
                    + "?partition=" + index.ToString(CultureInfo.InvariantCulture)), out status);
                result.Rows.AddRange(ParsePartitionRows(partitionText, result.ColumnNames.Count));
            }
            return result;
        }

        /// <summary>解析第 0 個 partition 的完整回覆：rowType、partitionInfo 與資料列。</summary>
        public static SnowflakeStatementResult ParseStatementResult(string json)
        {
            JObject document = JObject.Parse(json);
            SnowflakeStatementResult result = new SnowflakeStatementResult
            {
                StatementHandle = (string)document["statementHandle"] ?? string.Empty
            };
            JObject metadata = document["resultSetMetaData"] as JObject;
            if (metadata == null)
                throw new SnowflakeServerException(Localization.T("Snowflake.ResponseMissingMetadata"), (string)document["code"]);
            foreach (JToken column in metadata["rowType"] as JArray ?? new JArray())
            {
                result.ColumnNames.Add((string)column["name"] ?? string.Empty);
                result.ColumnTypes.Add((string)column["type"] ?? string.Empty);
            }
            JArray partitions = metadata["partitionInfo"] as JArray;
            result.PartitionCount = partitions != null && partitions.Count > 0 ? partitions.Count : 1;
            long numRows;
            if (long.TryParse(Convert.ToString(metadata["numRows"], CultureInfo.InvariantCulture), out numRows))
                result.NumRows = numRows;
            foreach (object[] row in ParseDataRows(document["data"] as JArray, result.ColumnNames.Count))
                result.Rows.Add(row);
            return result;
        }

        /// <summary>解析第 1 個以後 partition 的回覆；這些回覆只有 data 陣列。</summary>
        public static List<object[]> ParsePartitionRows(string json, int columnCount)
        {
            JObject document = JObject.Parse(json);
            return ParseDataRows(document["data"] as JArray, columnCount);
        }

        private static List<object[]> ParseDataRows(JArray data, int columnCount)
        {
            List<object[]> rows = new List<object[]>();
            if (data == null) return rows;
            foreach (JToken rowToken in data)
            {
                JArray rowArray = rowToken as JArray;
                if (rowArray == null) continue;
                object[] row = new object[columnCount];
                for (int i = 0; i < columnCount; i++)
                {
                    JToken cell = i < rowArray.Count ? rowArray[i] : null;
                    row[i] = cell == null || cell.Type == JTokenType.Null ? (object)DBNull.Value : cell.ToString();
                }
                rows.Add(row);
            }
            return rows;
        }

        private static string ReadHandle(string json)
        {
            string handle = (string)JObject.Parse(json)["statementHandle"];
            if (string.IsNullOrWhiteSpace(handle))
                throw new SnowflakeServerException(Localization.T("Snowflake.ResponseMissingHandle"), "");
            return handle;
        }

        private string Send(Func<HttpRequestMessage> requestFactory, out HttpStatusCode status)
        {
            using (HttpRequestMessage request = requestFactory())
            using (HttpResponseMessage response = _http.SendAsync(request).GetAwaiter().GetResult())
            {
                status = response.StatusCode;
                string text = response.Content == null
                    ? string.Empty
                    : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                if (response.IsSuccessStatusCode || status == HttpStatusCode.Accepted) return text;
                throw new SnowflakeServerException(BuildErrorMessage(status, text), ReadErrorCode(text));
            }
        }

        private static string BuildErrorMessage(HttpStatusCode status, string body)
        {
            try
            {
                string message = (string)JObject.Parse(body)["message"];
                if (!string.IsNullOrWhiteSpace(message)) return message.Trim();
            }
            catch (Exception) { }
            return Localization.Format("Snowflake.RequestFailed", ((int)status).ToString(CultureInfo.InvariantCulture));
        }

        private static string ReadErrorCode(string body)
        {
            try { return (string)JObject.Parse(body)["code"] ?? string.Empty; }
            catch (Exception) { return string.Empty; }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}
