using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace mySQLPunk.lib
{
    public sealed class MongoPipelineStage
    {
        public MongoPipelineStage(string operatorName, string bodyJson, bool enabled = true)
        {
            Operator = operatorName;
            BodyJson = bodyJson ?? string.Empty;
            Enabled = enabled;
        }

        public string Operator { get; set; }
        /// <summary>stage 的值（例如 $match 的條件文件、$limit 的數字、$unwind 的字串）。</summary>
        public string BodyJson { get; set; }
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// Aggregation Pipeline 的純邏輯：stage 範本、只允許唯讀 stage、解析與組裝 pipeline、匯出 shell 語法與查詢 JSON，
    /// 以及從既有 pipeline JSON 匯入。會寫入資料的 $out／$merge 一律拒絕，子 pipeline 也逐層檢查。
    /// </summary>
    public static class MongoPipelineService
    {
        public const int MaximumStages = 100;

        private static readonly string[] ReadOnlyStages =
        {
            "$match", "$project", "$group", "$sort", "$limit", "$skip", "$unwind", "$lookup", "$addFields", "$set", "$unset",
            "$count", "$sample", "$bucket", "$bucketAuto", "$facet", "$replaceRoot", "$replaceWith", "$sortByCount",
            "$graphLookup", "$redact", "$densify", "$fill", "$setWindowFields", "$unionWith", "$geoNear", "$documents"
        };

        private static readonly string[] WriteStages = { "$out", "$merge" };

        /// <summary>新增 stage 時帶入的範本；可直接執行或稍作修改。</summary>
        public static readonly KeyValuePair<string, string>[] Templates =
        {
            new KeyValuePair<string, string>("$match", "{ }"),
            new KeyValuePair<string, string>("$project", "{ \"_id\": 1 }"),
            new KeyValuePair<string, string>("$group", "{ \"_id\": \"$field\", \"count\": { \"$sum\": 1 } }"),
            new KeyValuePair<string, string>("$sort", "{ \"_id\": 1 }"),
            new KeyValuePair<string, string>("$limit", "10"),
            new KeyValuePair<string, string>("$skip", "0"),
            new KeyValuePair<string, string>("$unwind", "\"$field\""),
            new KeyValuePair<string, string>("$lookup", "{ \"from\": \"other\", \"localField\": \"field\", \"foreignField\": \"_id\", \"as\": \"joined\" }"),
            new KeyValuePair<string, string>("$addFields", "{ \"newField\": \"$field\" }"),
            new KeyValuePair<string, string>("$unset", "\"field\""),
            new KeyValuePair<string, string>("$count", "\"count\""),
            new KeyValuePair<string, string>("$sample", "{ \"size\": 10 }"),
            new KeyValuePair<string, string>("$sortByCount", "\"$field\""),
            new KeyValuePair<string, string>("$replaceRoot", "{ \"newRoot\": \"$field\" }"),
            new KeyValuePair<string, string>("$bucketAuto", "{ \"groupBy\": \"$field\", \"buckets\": 5 }"),
            new KeyValuePair<string, string>("$facet", "{ \"byField\": [ { \"$sortByCount\": \"$field\" } ] }")
        };

        public static string TemplateFor(string operatorName)
        {
            return Templates.Where(item => item.Key == operatorName).Select(item => item.Value).FirstOrDefault() ?? "{ }";
        }

        /// <summary>組裝啟用中的 stage（可只到 upToIndex 為止）；任何錯誤都指出第幾個 stage。</summary>
        public static List<BsonDocument> Build(IList<MongoPipelineStage> stages, int upToIndex = int.MaxValue)
        {
            if (stages == null) throw new ArgumentNullException("stages");
            if (stages.Count > MaximumStages) throw new FormatException(Localization.Format("MongoPipeline.Error.TooManyStages", MaximumStages));
            List<BsonDocument> pipeline = new List<BsonDocument>();
            for (int index = 0; index < stages.Count && index <= upToIndex; index++)
            {
                MongoPipelineStage stage = stages[index];
                if (!stage.Enabled) continue;
                pipeline.Add(ParseStage(stage, index + 1));
            }
            return pipeline;
        }

        public static BsonDocument ParseStage(MongoPipelineStage stage, int number)
        {
            string name = (stage.Operator ?? string.Empty).Trim();
            if (!name.StartsWith("$", StringComparison.Ordinal)) throw new FormatException(Localization.Format("MongoPipeline.Error.Operator", number, name));
            if (WriteStages.Contains(name)) throw new FormatException(Localization.Format("MongoPipeline.Error.WriteStage", number, name));
            if (!ReadOnlyStages.Contains(name)) throw new FormatException(Localization.Format("MongoPipeline.Error.UnknownStage", number, name));
            BsonValue body;
            try
            {
                body = BsonDocument.Parse("{ \"v\": " + (string.IsNullOrWhiteSpace(stage.BodyJson) ? "{}" : stage.BodyJson) + " }")["v"];
            }
            catch (Exception exception)
            {
                throw new FormatException(Localization.Format("MongoPipeline.Error.Json", number, name, exception.Message), exception);
            }

            BsonDocument document = new BsonDocument(name, body);
            string nested = FindWriteStage(body);
            if (nested != null) throw new FormatException(Localization.Format("MongoPipeline.Error.WriteStage", number, nested));
            return document;
        }

        /// <summary>mongosh 可直接貼上的語法。</summary>
        public static string ToShellText(string collectionName, IList<MongoPipelineStage> stages)
        {
            List<BsonDocument> pipeline = Build(stages);
            JsonWriterSettings settings = new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.RelaxedExtendedJson };
            string escaped = (collectionName ?? string.Empty).Replace("\\", "\\\\").Replace("\"", "\\\"");
            return "db.getCollection(\"" + escaped + "\").aggregate(" + new BsonArray(pipeline).ToJson(settings) + ")";
        }

        /// <summary>查詢視窗使用的 JSON：{ collection, pipeline, limit }。</summary>
        public static string ToQueryJson(string collectionName, IList<MongoPipelineStage> stages, int limit)
        {
            BsonDocument query = new BsonDocument
            {
                { "collection", collectionName ?? string.Empty },
                { "pipeline", new BsonArray(Build(stages)) },
                { "limit", limit }
            };
            return query.ToJson(new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.RelaxedExtendedJson });
        }

        /// <summary>從 [ { "$match": … }, … ] 或含 pipeline 欄位的查詢 JSON 匯入。</summary>
        public static List<MongoPipelineStage> Import(string json)
        {
            string text = (json ?? string.Empty).Trim();
            BsonArray array;
            try
            {
                BsonValue value = BsonDocument.Parse("{ \"v\": " + text + " }")["v"];
                if (value.IsBsonDocument && value.AsBsonDocument.Contains("pipeline")) value = value.AsBsonDocument["pipeline"];
                if (!value.IsBsonArray) throw new FormatException(Localization.T("MongoPipeline.Error.ImportShape"));
                array = value.AsBsonArray;
            }
            catch (FormatException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new FormatException(Localization.Format("MongoPipeline.Error.ImportJson", exception.Message), exception);
            }

            List<MongoPipelineStage> stages = new List<MongoPipelineStage>();
            // 編輯用文字採 Relaxed 表示法，數字不會變成 {"$numberInt": …}。
            JsonWriterSettings settings = new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.RelaxedExtendedJson };
            int number = 0;
            foreach (BsonValue item in array)
            {
                number++;
                if (!item.IsBsonDocument || item.AsBsonDocument.ElementCount != 1) throw new FormatException(Localization.Format("MongoPipeline.Error.StageShape", number));
                BsonElement element = item.AsBsonDocument.GetElement(0);
                MongoPipelineStage stage = new MongoPipelineStage(element.Name, BodyText(element.Value, settings));
                ParseStage(stage, number);
                stages.Add(stage);
            }
            return stages;
        }

        /// <summary>stage 值轉回可編輯文字；純量用 JSON 表示（字串保留引號）。</summary>
        public static string BodyText(BsonValue value, JsonWriterSettings settings)
        {
            if (value.IsBsonDocument) return value.AsBsonDocument.ToJson(settings);
            if (value.IsBsonArray) return value.AsBsonArray.ToJson(settings);
            BsonDocument wrapper = new BsonDocument("v", value);
            string json = wrapper.ToJson(new JsonWriterSettings { OutputMode = settings.OutputMode });
            int colon = json.IndexOf(':');
            return json.Substring(colon + 1, json.Length - colon - 2).Trim();
        }

        private static string FindWriteStage(BsonValue value)
        {
            if (value.IsBsonDocument)
            {
                foreach (BsonElement element in value.AsBsonDocument)
                {
                    if (WriteStages.Contains(element.Name)) return element.Name;
                    string nested = FindWriteStage(element.Value);
                    if (nested != null) return nested;
                }
            }
            else if (value.IsBsonArray)
            {
                foreach (BsonValue item in value.AsBsonArray)
                {
                    string nested = FindWriteStage(item);
                    if (nested != null) return nested;
                }
            }
            return null;
        }
    }
}
