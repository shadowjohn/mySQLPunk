using System;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using System.Linq;

namespace mySQLPunk.lib.MssqlExport
{
    internal static class MssqlValueCodec
    {
        private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        internal static string QuoteIdentifier(string identifier)
        {
            if (identifier == null) throw new ArgumentNullException(nameof(identifier));
            return "[" + identifier.Replace("]", "]]") + "]";
        }
        internal static bool IsBinaryProjected(MssqlColumnDefinition column)
        {
            switch (column.NativeType) {
                case "varchar": case "char": case "text": case "nvarchar": case "nchar": case "ntext":
                case "xml": case "binary": case "varbinary": case "image": return true;
                default: return false;
            }
        }
        internal static string SourceExpression(MssqlColumnDefinition column)
        {
            string name = QuoteIdentifier(column.Name);
            if (!IsBinaryProjected(column)) return name;
            if (column.NativeType == "text") name = "CONVERT(varchar(max)," + name + ")";
            if (column.NativeType == "ntext" || column.NativeType == "xml") name = "CONVERT(nvarchar(max)," + name + ")";
            return "CONVERT(varbinary(max)," + name + ")";
        }
        internal static string BinaryPrefix(MssqlColumnDefinition column)
        {
            switch (column.NativeType) {
                case "xml": return "CONVERT(xml,CONVERT(nvarchar(max),0x";
                case "nvarchar": case "nchar": case "ntext": return "CONVERT(nvarchar(max),0x";
                default: return "0x"; // Direct binary assignment preserves ANSI bytes in the target collation.
            }
        }
        internal static string BinarySuffix(MssqlColumnDefinition column)
        {
            switch (column.NativeType) {
                case "xml": return "),1)";
                case "nvarchar": case "nchar": case "ntext": return ")";
                default: return "";
            }
        }
        internal static object ReadScalar(DbDataReader reader, int ordinal, MssqlColumnDefinition column)
        {
            if (column.NativeType == "decimal" || column.NativeType == "numeric")
                return reader.GetProviderSpecificValue(ordinal); // SqlDecimal supports precision 38.
            return reader.GetValue(ordinal); // Only fixed-width types reach this method.
        }
        internal static int ScalarBytes(MssqlColumnDefinition column)
        {
            if (column.Length > 0) return column.Length;
            switch (column.NativeType) {
                case "bit": case "tinyint": return 1;
                case "smallint": return 2;
                case "int": case "real": case "smallmoney": case "smalldatetime": return 4;
                case "uniqueidentifier": return 16;
                case "decimal": case "numeric": return 17;
                case "datetimeoffset": return 10;
                case "date": return 3;
                case "time": return 5;
                default: return 8;
            }
        }
        internal static string FormatScalar(object value, MssqlColumnDefinition column)
        {
            if (value == null || value == DBNull.Value || (value is INullable && ((INullable)value).IsNull)) return "NULL";
            switch (column.NativeType) {
                case "decimal": case "numeric":
                    // SqlDecimal.ToString is invariant and retains the declared scale without CLR decimal conversion.
                    if (value is SqlDecimal) return ((SqlDecimal)value).ToString();
                    if (value is decimal) return ((decimal)value).ToString(Invariant);
                    throw new InvalidOperationException("Expected provider SqlDecimal.");
                case "money": case "smallmoney":
                    return (value is SqlMoney ? ((SqlMoney)value).Value : (decimal)value).ToString("0.0000", Invariant);
                case "bit": return (bool)value ? "1" : "0";
                case "tinyint": case "smallint": case "int": case "bigint": return Convert.ToString(value, Invariant);
                case "float": return ((double)value).ToString("R", Invariant);
                case "real": return ((float)value).ToString("R", Invariant);
                case "uniqueidentifier": return "'" + ((Guid)value).ToString("D") + "'";
                case "date": return "'" + ((DateTime)value).ToString("yyyy-MM-dd", Invariant) + "'";
                case "datetime": case "datetime2": case "smalldatetime": return "'" + ((DateTime)value).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", Invariant) + "'";
                case "datetimeoffset": return "'" + ((DateTimeOffset)value).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", Invariant) + "'";
                case "time": return "'" + ((TimeSpan)value).ToString(@"hh\:mm\:ss\.fffffff", Invariant) + "'";
                default: throw new NotSupportedException("No scalar codec for " + column.NativeType + ".");
            }
        }
    }
}
