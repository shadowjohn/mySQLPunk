using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace mySQLPunk.lib.MssqlExport
{
    internal sealed class MssqlViewReferenceResolution
    {
        public int? SourceDatabaseId { get; }
        public int? ReferencedDatabaseId { get; }
        public int? ObjectId { get; }
        public string ObjectType { get; }
        internal MssqlViewReferenceResolution(int? sourceDatabaseId, int? referencedDatabaseId, int? objectId, string objectType)
        { SourceDatabaseId = sourceDatabaseId; ReferencedDatabaseId = referencedDatabaseId; ObjectId = objectId; ObjectType = objectType; }
    }

    internal sealed class MssqlViewRewriteResult
    {
        public string Sql { get; }
        public IReadOnlyList<int> ReferencedIds { get; }
        public MssqlExportIssue Issue { get; }
        public bool Success => Issue == null;
        internal MssqlViewRewriteResult(string sql, IEnumerable<int> referencedIds, MssqlExportIssue issue)
        { Sql = sql; ReferencedIds = (referencedIds ?? Enumerable.Empty<int>()).Distinct().OrderBy(x => x).ToList().AsReadOnly(); Issue = issue; }
    }

    internal static class MssqlViewReferenceRewriter
    {
        internal static MssqlViewRewriteResult Rewrite(MssqlViewDefinition view,
            Func<string, string, string, MssqlViewReferenceResolution> resolver,
            IEnumerable<int?> qualifiedDependencyIds, IEnumerable<int> selectedObjectIds)
        {
            if (view == null) throw new ArgumentNullException(nameof(view));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            var expected = (qualifiedDependencyIds ?? Enumerable.Empty<int?>()).ToArray();
            if (expected.Any(x => !x.HasValue || x.Value <= 0)) return Failure(view, "A qualified dependency has no verified local object identity.");
            var selected = new HashSet<int>(selectedObjectIds ?? Enumerable.Empty<int>());
            ViewStatementBody statement;
            if (!ParseView(view.Definition, view.QuotedIdentifier, out statement))
                return Failure(view, "The view definition is not a single supported SQL Server 2019 view statement.");
            var audit = new ReferenceAudit();
            statement.SelectStatement.Accept(audit);
            if (audit.Error != null) return Failure(view, audit.Error);
            var patches = new List<TextPatch>();
            var ids = new HashSet<int>();
            int? sourceDatabaseId = null;
            foreach (var name in audit.QualifiedNames)
            {
                string database = name.DatabaseIdentifier.Value, schema = name.SchemaIdentifier.Value, entity = name.BaseIdentifier.Value;
                if (!ValidIdentifier(database) || !ValidIdentifier(schema) || !ValidIdentifier(entity))
                    return Failure(view, "Qualified identifiers must contain between 1 and 128 UTF-16 characters.");
                // SQL Server, not a client comparer, decides database and object identity.
                var resolution = resolver(database, schema, entity);
                if (resolution == null || !resolution.SourceDatabaseId.HasValue || resolution.SourceDatabaseId.Value <= 0 ||
                    resolution.SourceDatabaseId != resolution.ReferencedDatabaseId || !resolution.ObjectId.HasValue || resolution.ObjectId.Value <= 0 ||
                    (resolution.ObjectType != "U" && resolution.ObjectType != "V") || !selected.Contains(resolution.ObjectId.Value) ||
                    (sourceDatabaseId.HasValue && sourceDatabaseId != resolution.SourceDatabaseId))
                    return Failure(view, "A qualified reference is not a verified selected table or view in the source database.");
                sourceDatabaseId = resolution.SourceDatabaseId;
                ids.Add(resolution.ObjectId.Value);
                if (!RemoveDatabaseQualifier(view.Definition, name, patches))
                    return Failure(view, "Qualified identifier token boundaries could not be verified.");
            }
            if (!ids.SetEquals(expected.Select(x => x.Value)))
                return Failure(view, "The parsed qualified references do not match the catalog dependency identities.");
            if (!NormalizeDeclaration(view, statement, patches))
                return Failure(view, "The view declaration token boundaries could not be verified.");
            string sql;
            if (!ApplyPatches(view.Definition, patches, out sql)) return Failure(view, "View definition edits overlap or have invalid token boundaries.");
            ViewStatementBody rewritten;
            if (!ParseView(sql, view.QuotedIdentifier, out rewritten) || !(rewritten is CreateViewStatement) ||
                rewritten.SchemaObjectName.Count != 2 || rewritten.SchemaObjectName.SchemaIdentifier.Value != view.Key.Schema ||
                rewritten.SchemaObjectName.BaseIdentifier.Value != view.Key.Name)
                return Failure(view, "The rewritten view declaration could not be verified.");
            var finalAudit = new ReferenceAudit();
            rewritten.SelectStatement.Accept(finalAudit);
            if (finalAudit.Error != null || finalAudit.QualifiedNames.Count != 0)
                return Failure(view, "A qualified reference remains after rewriting the view.");
            return new MssqlViewRewriteResult(sql, ids, null);
        }

        private static bool ParseView(string text, bool quotedIdentifier, out ViewStatementBody statement)
        {
            statement = null;
            if (string.IsNullOrEmpty(text)) return false;
            IList<ParseError> errors;
            var script = new TSql150Parser(quotedIdentifier).Parse(new StringReader(text), out errors) as TSqlScript;
            if (errors.Count != 0 || script == null || script.Batches.Count != 1 || script.Batches[0].Statements.Count != 1 ||
                script.ScriptTokenStream.Any(x => x.TokenType == TSqlTokenType.Go)) return false;
            var candidate = script.Batches[0].Statements[0];
            if (!(candidate is CreateViewStatement) && !(candidate is AlterViewStatement) && !(candidate is CreateOrAlterViewStatement)) return false;
            statement = (ViewStatementBody)candidate;
            return statement.SchemaObjectName != null && statement.SelectStatement != null && !statement.IsMaterialized;
        }

        private static bool RemoveDatabaseQualifier(string text, SchemaObjectName name, List<TextPatch> patches)
        {
            var database = name.DatabaseIdentifier;
            var schema = name.SchemaIdentifier;
            var tokens = name.ScriptTokenStream;
            if (tokens == null || database.FirstTokenIndex != database.LastTokenIndex ||
                database.FirstTokenIndex < 0 || schema.FirstTokenIndex <= database.LastTokenIndex || schema.FirstTokenIndex >= tokens.Count) return false;
            var separator = Enumerable.Range(database.LastTokenIndex + 1, schema.FirstTokenIndex - database.LastTokenIndex - 1)
                .Select(i => tokens[i]).Where(t => !Trivia(t)).ToArray();
            if (separator.Length != 1 || separator[0].TokenType != TSqlTokenType.Dot) return false;
            return AddTokenPatch(text, tokens[database.FirstTokenIndex], "", patches) && AddTokenPatch(text, separator[0], "", patches);
        }

        private static bool NormalizeDeclaration(MssqlViewDefinition view, ViewStatementBody statement, List<TextPatch> patches)
        {
            var name = statement.SchemaObjectName;
            var tokens = statement.ScriptTokenStream;
            if (name.FirstTokenIndex <= statement.FirstTokenIndex || name.StartOffset < 0 || name.FragmentLength <= 0 ||
                name.StartOffset > view.Definition.Length - name.FragmentLength) return false;
            var header = Enumerable.Range(statement.FirstTokenIndex, name.FirstTokenIndex - statement.FirstTokenIndex)
                .Select(i => tokens[i]).Where(t => !Trivia(t)).ToArray();
            if (header.Length == 2 && header[1].TokenType == TSqlTokenType.View &&
                (header[0].TokenType == TSqlTokenType.Create || header[0].TokenType == TSqlTokenType.Alter))
            {
                if (header[0].TokenType == TSqlTokenType.Alter && !AddTokenPatch(view.Definition, header[0], "CREATE", patches)) return false;
            }
            else if (header.Length == 4 && header[0].TokenType == TSqlTokenType.Create && header[1].TokenType == TSqlTokenType.Or &&
                header[2].TokenType == TSqlTokenType.Alter && header[3].TokenType == TSqlTokenType.View)
            {
                if (!AddTokenPatch(view.Definition, header[1], "", patches) || !AddTokenPatch(view.Definition, header[2], "", patches)) return false;
            }
            else return false;
            patches.Add(new TextPatch(name.StartOffset, name.FragmentLength, MssqlExportMetadataReader.Qualified(view.Key)));
            return true;
        }

        private static bool AddTokenPatch(string text, TSqlParserToken token, string replacement, List<TextPatch> patches)
        {
            if (token.Offset < 0 || string.IsNullOrEmpty(token.Text) || token.Offset > text.Length - token.Text.Length ||
                string.CompareOrdinal(text, token.Offset, token.Text, 0, token.Text.Length) != 0) return false;
            patches.Add(new TextPatch(token.Offset, token.Text.Length, replacement));
            return true;
        }

        private static bool ApplyPatches(string text, List<TextPatch> patches, out string result)
        {
            result = null;
            var sql = new StringBuilder(text);
            int end = text.Length;
            foreach (var patch in patches.OrderByDescending(x => x.Offset))
            {
                if (patch.Offset < 0 || patch.Length <= 0 || patch.Offset > end - patch.Length) return false;
                sql.Remove(patch.Offset, patch.Length).Insert(patch.Offset, patch.Replacement);
                end = patch.Offset;
            }
            result = sql.ToString();
            return true;
        }

        private static bool Trivia(TSqlParserToken token) => token.TokenType == TSqlTokenType.WhiteSpace ||
            token.TokenType == TSqlTokenType.SingleLineComment || token.TokenType == TSqlTokenType.MultilineComment;
        private static bool ValidIdentifier(string name) => !string.IsNullOrEmpty(name) && name.Length <= 128;
        private static MssqlViewRewriteResult Failure(MssqlViewDefinition view, string message) =>
            new MssqlViewRewriteResult(null, null, new MssqlExportIssue("UnsupportedViewReference", view.Key, null, message));

        private sealed class TextPatch
        {
            internal readonly int Offset, Length;
            internal readonly string Replacement;
            internal TextPatch(int offset, int length, string replacement) { Offset = offset; Length = length; Replacement = replacement; }
        }

        private sealed class ReferenceAudit : TSqlFragmentVisitor
        {
            internal readonly List<SchemaObjectName> QualifiedNames = new List<SchemaObjectName>();
            internal string Error;
            private void Reject(string reason) { if (Error == null) Error = reason; }
            public override void Visit(NamedTableReference node)
            {
                var name = node.SchemaObject;
                if (name.ServerIdentifier != null) Reject("Server-qualified references are not supported.");
                if (name.DatabaseIdentifier != null)
                {
                    if (name.Count != 3 || name.SchemaIdentifier == null || !ValidIdentifier(name.SchemaIdentifier.Value) ||
                        name.BaseIdentifier == null || !ValidIdentifier(name.BaseIdentifier.Value) || !ValidIdentifier(name.DatabaseIdentifier.Value))
                        Reject("A qualified table or view reference requires explicit database, schema and object identifiers.");
                    else QualifiedNames.Add(name);
                }
                base.Visit(node);
            }
            public override void Visit(SchemaObjectName node)
            {
                if ((node.ServerIdentifier != null || node.DatabaseIdentifier != null) && !QualifiedNames.Contains(node))
                    Reject("A qualified reference occurs outside a supported named table or view reference.");
                base.Visit(node);
            }
            public override void Visit(ColumnReferenceExpression node)
            {
                if (node.MultiPartIdentifier != null && node.MultiPartIdentifier.Count >= 4)
                    Reject("Database-qualified column references require a separate supported rewrite and are not exported.");
                base.Visit(node);
            }
            public override void Visit(SelectStarExpression node)
            {
                if (node.Qualifier != null && node.Qualifier.Count >= 3) Reject("Database-qualified wildcard references are not supported.");
                base.Visit(node);
            }
            public override void Visit(MultiPartIdentifierCallTarget node)
            {
                if (node.MultiPartIdentifier != null && node.MultiPartIdentifier.Count >= 2)
                    Reject("Multipart function or method targets cannot be safely normalized in this view.");
                base.Visit(node);
            }
            public override void Visit(NextValueForExpression node) { Reject("Sequence references are not supported."); }
            public override void Visit(PartitionFunctionCall node) { Reject("Partition function references are not supported."); }
            public override void Visit(SchemaObjectFunctionTableReference node) { Reject("Table-valued function references are not supported."); }
            public override void Visit(OpenQueryTableReference node) { Reject("External rowset references are not supported."); }
            public override void Visit(OpenRowsetTableReference node) { Reject("External rowset references are not supported."); }
            public override void Visit(AdHocTableReference node) { Reject("External rowset references are not supported."); }
            public override void Visit(BulkOpenRowset node) { Reject("External rowset references are not supported."); }
            public override void Visit(TSqlFragment node)
            {
                // Bound the accepted rowset forms for this new normalization path. Concrete
                // external/function overrides above do not rely on base-type visitation.
                if (node is TableReference && !(node is NamedTableReference) && !(node is QueryDerivedTable) && !(node is InlineDerivedTable) &&
                    !(node is QualifiedJoin) && !(node is UnqualifiedJoin) && !(node is JoinParenthesisTableReference) &&
                    !(node is OdbcQualifiedJoinTableReference) && !(node is PivotedTableReference) && !(node is UnpivotedTableReference) && !(node is OpenJsonTableReference))
                    Reject("This view contains an unsupported rowset reference context.");
            }
        }
    }
}
