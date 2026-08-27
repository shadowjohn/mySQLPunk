using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace mySQLPunk.lib
{
    /// <summary>文件樹的單一節點；公開 API 只用字串，讓 smoke test 不必參考 MongoDB 套件。</summary>
    public sealed class MongoDocumentTreeNode
    {
        public string Name = string.Empty;
        public string BsonType = string.Empty;
        public string DisplayValue = string.Empty;
        public List<MongoDocumentTreeNode> Children = new List<MongoDocumentTreeNode>();
    }

    public sealed class MongoDocumentEditValidation
    {
        public bool Success;
        public string Error = string.Empty;
        public string NormalizedJson = string.Empty;
        public bool HasChanges;
    }

    /// <summary>
    /// MongoDB 文件檢視與安全編輯的純邏輯：樹狀結構、編輯驗證與並行比對規則。
    /// 編輯區一律使用 Canonical Extended JSON，儲存後欄位型別才不會被 relaxed 表示法默默改掉。
    /// </summary>
    public static class MongoDocumentEditService
    {
        private static readonly JsonWriterSettings CanonicalIndented =
            new JsonWriterSettings { Indent = true, OutputMode = JsonOutputMode.CanonicalExtendedJson };
        private static readonly JsonWriterSettings CanonicalCompact =
            new JsonWriterSettings { Indent = false, OutputMode = JsonOutputMode.CanonicalExtendedJson };

        public static string FormatDocumentJson(string documentJson)
        {
            return ParseDocument(documentJson).ToJson(CanonicalIndented);
        }

        public static MongoDocumentTreeNode BuildTree(string documentJson)
        {
            BsonDocument document = ParseDocument(documentJson);
            MongoDocumentTreeNode root = CreateNode(Localization.T("MongoDB.DocumentRootNode"), document);
            return root;
        }

        /// <summary>取出 <c>{"_id": ...}</c> 過濾器；文件沒有可用 _id 時回傳 false（此時只能唯讀）。</summary>
        public static bool TryGetIdFilterJson(string documentJson, out string filterJson)
        {
            filterJson = string.Empty;
            BsonDocument document;
            try { document = ParseDocument(documentJson); }
            catch (Exception) { return false; }
            if (!document.Contains("_id") || document["_id"].IsBsonNull) return false;
            filterJson = new BsonDocument("_id", document["_id"]).ToJson(CanonicalCompact);
            return true;
        }

        /// <summary>驗證要新增的文件：只要求最外層是 JSON object，_id 可留給伺服器產生。</summary>
        public static MongoDocumentEditValidation ValidateInsert(string documentJson)
        {
            MongoDocumentEditValidation result = new MongoDocumentEditValidation();
            BsonDocument document;
            try { document = ParseDocument(documentJson); }
            catch (Exception ex)
            {
                result.Error = Localization.Format("MongoDB.DocumentInvalid", ex.Message);
                return result;
            }
            result.Success = true;
            result.HasChanges = true;
            result.NormalizedJson = document.ToJson(CanonicalIndented);
            return result;
        }

        public static MongoDocumentEditValidation ValidateEdit(string originalJson, string editedJson)
        {
            MongoDocumentEditValidation result = new MongoDocumentEditValidation();

            BsonDocument original;
            try { original = ParseDocument(originalJson); }
            catch (Exception ex)
            {
                result.Error = Localization.Format("MongoDB.DocumentInvalid", ex.Message);
                return result;
            }

            BsonDocument edited;
            try { edited = ParseDocument(editedJson); }
            catch (Exception ex)
            {
                result.Error = Localization.Format("MongoDB.DocumentInvalid", ex.Message);
                return result;
            }

            if (!original.Contains("_id") || original["_id"].IsBsonNull)
            {
                result.Error = Localization.T("MongoDB.DocumentIdRequired");
                return result;
            }
            if (!edited.Contains("_id") || !original["_id"].Equals(edited["_id"]))
            {
                result.Error = Localization.T("MongoDB.DocumentIdImmutable");
                return result;
            }

            result.Success = true;
            result.NormalizedJson = edited.ToJson(CanonicalIndented);
            result.HasChanges = !original.Equals(edited);
            return result;
        }

        /// <summary>
        /// 完整文件比對只有在頂層值不會被查詢引擎當成運算子時才安全：
        /// 頂層欄位值若是「第一個 key 以 $ 開頭」的物件，find 會把它解讀成 $gt 這類條件而非資料。
        /// 這種文件改用 _id 過濾＋寫入前重新讀取比對。
        /// </summary>
        public static bool CanUseFullDocumentFilter(string documentJson)
        {
            BsonDocument document = ParseDocument(documentJson);
            foreach (BsonElement element in document)
            {
                if (element.Value != null && element.Value.IsBsonDocument)
                {
                    BsonDocument value = element.Value.AsBsonDocument;
                    if (value.ElementCount > 0 && value.GetElement(0).Name.StartsWith("$", StringComparison.Ordinal))
                        return false;
                }
            }
            return true;
        }

        public static bool DocumentsEqual(string leftJson, string rightJson)
        {
            return ParseDocument(leftJson).Equals(ParseDocument(rightJson));
        }

        private static BsonDocument ParseDocument(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
                throw new FormatException(Localization.T("MongoDB.DocumentMustBeObject"));
            string trimmed = json.Trim();
            if (!trimmed.StartsWith("{", StringComparison.Ordinal))
                throw new FormatException(Localization.T("MongoDB.DocumentMustBeObject"));
            return BsonDocument.Parse(trimmed);
        }

        private static MongoDocumentTreeNode CreateNode(string name, BsonValue value)
        {
            MongoDocumentTreeNode node = new MongoDocumentTreeNode
            {
                Name = name ?? string.Empty,
                BsonType = value == null ? "Null" : value.BsonType.ToString()
            };
            if (value == null || value.IsBsonNull)
            {
                node.DisplayValue = "null";
                return node;
            }
            if (value.IsBsonDocument)
            {
                BsonDocument document = value.AsBsonDocument;
                node.DisplayValue = "{" + document.ElementCount.ToString(CultureInfo.InvariantCulture) + "}";
                foreach (BsonElement element in document)
                {
                    node.Children.Add(CreateNode(element.Name, element.Value));
                }
                return node;
            }
            if (value.IsBsonArray)
            {
                BsonArray array = value.AsBsonArray;
                node.DisplayValue = "[" + array.Count.ToString(CultureInfo.InvariantCulture) + "]";
                for (int i = 0; i < array.Count; i++)
                {
                    node.Children.Add(CreateNode("[" + i.ToString(CultureInfo.InvariantCulture) + "]", array[i]));
                }
                return node;
            }
            node.DisplayValue = LeafDisplayValue(value);
            return node;
        }

        private static string LeafDisplayValue(BsonValue value)
        {
            if (value.IsString) return value.AsString;
            if (value.IsObjectId) return value.AsObjectId.ToString();
            if (value.IsBoolean) return value.AsBoolean ? "true" : "false";
            if (value.IsBsonDateTime) return value.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            if (value.IsBsonBinaryData) return "BinData(" + value.AsBsonBinaryData.Bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes)";
            return value.ToString();
        }
    }
}
