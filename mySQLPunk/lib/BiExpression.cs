using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace mySQLPunk.lib
{
    /// <summary>
    /// BI 計算欄位的運算式：欄位以 [名稱] 參照，支援 + - * / %、比較、AND／OR／NOT、字串與數字常值、NULL，
    /// 以及 IF、ROUND、ABS、FLOOR、CEILING、COALESCE、UPPER、LOWER、LEN、CONCAT、LEFT、RIGHT、YEAR、MONTH、DAY、DATE 等函式。
    /// 只做計算，不能存取資料庫或檔案；任何一列計算失敗都回報該列與原因。
    /// </summary>
    public sealed class BiExpression
    {
        private const int MaximumLength = 2000;
        private const int MaximumDepth = 64;
        private readonly Node root;

        private BiExpression(string text, Node root)
        {
            Text = text;
            this.root = root;
            Fields = new List<string>();
            CollectFields(root, Fields);
        }

        public string Text { get; private set; }

        /// <summary>運算式參照到的欄位名稱（不分大小寫、不重複）。</summary>
        public List<string> Fields { get; private set; }

        public static BiExpression Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) throw new FormatException(Localization.T("BiExpression.Error.Empty"));
            if (text.Length > MaximumLength) throw new FormatException(Localization.Format("BiExpression.Error.TooLong", MaximumLength));
            Parser parser = new Parser(Tokenize(text));
            Node node = parser.ParseExpression(0, 0);
            parser.ExpectEnd();
            return new BiExpression(text, node);
        }

        /// <summary>以欄位值計算；field 查無時丟出錯誤。回傳 decimal、string、bool、DateTime 或 null。</summary>
        public object Evaluate(Func<string, object> field)
        {
            return Evaluate(root, field);
        }

        private static void CollectFields(Node node, List<string> fields)
        {
            if (node.Kind == NodeKind.Field)
            {
                if (!fields.Contains(node.Text, StringComparer.OrdinalIgnoreCase)) fields.Add(node.Text);
                return;
            }
            foreach (Node child in node.Children) CollectFields(child, fields);
        }

        // ------------------------------------------------------------ Evaluation

        private static object Evaluate(Node node, Func<string, object> field)
        {
            switch (node.Kind)
            {
                case NodeKind.Number: return node.Number;
                case NodeKind.String: return node.Text;
                case NodeKind.Null: return null;
                case NodeKind.Boolean: return node.Text == "TRUE";
                case NodeKind.Field: return Normalize(field(node.Text));
                case NodeKind.Unary:
                {
                    object value = Evaluate(node.Children[0], field);
                    if (node.Text == "NOT") return value == null ? (object)null : !ToBool(value);
                    if (value == null) return null;
                    return node.Text == "-" ? -ToNumber(value) : ToNumber(value);
                }
                case NodeKind.Binary:
                    return Binary(node, field);
                case NodeKind.Call:
                    return Call(node, field);
                default:
                    throw new InvalidOperationException(node.Kind.ToString());
            }
        }

        private static object Binary(Node node, Func<string, object> field)
        {
            string op = node.Text;
            if (op == "AND" || op == "OR")
            {
                object leftValue = Evaluate(node.Children[0], field);
                bool left = leftValue != null && ToBool(leftValue);
                if (op == "AND" && leftValue != null && !left) return false;
                if (op == "OR" && left) return true;
                object rightValue = Evaluate(node.Children[1], field);
                if (leftValue == null || rightValue == null) return op == "OR" && rightValue != null && ToBool(rightValue) ? (object)true : null;
                return op == "AND" ? left && ToBool(rightValue) : left || ToBool(rightValue);
            }

            object a = Evaluate(node.Children[0], field);
            object b = Evaluate(node.Children[1], field);
            if (a == null || b == null) return null;
            switch (op)
            {
                case "+":
                    if (a is string || b is string) return ToText(a) + ToText(b);
                    return ToNumber(a) + ToNumber(b);
                case "-": return ToNumber(a) - ToNumber(b);
                case "*": return ToNumber(a) * ToNumber(b);
                case "/":
                {
                    decimal divisor = ToNumber(b);
                    if (divisor == 0m) return null;
                    return ToNumber(a) / divisor;
                }
                case "%":
                {
                    decimal divisor = ToNumber(b);
                    if (divisor == 0m) return null;
                    return ToNumber(a) % divisor;
                }
                default:
                    int compare = Compare(a, b);
                    switch (op)
                    {
                        case "=": return compare == 0;
                        case "<>": return compare != 0;
                        case "<": return compare < 0;
                        case "<=": return compare <= 0;
                        case ">": return compare > 0;
                        default: return compare >= 0;
                    }
            }
        }

        private static object Call(Node node, Func<string, object> field)
        {
            string name = node.Text;
            List<Node> args = node.Children;
            Func<int, object> arg = index => Evaluate(args[index], field);
            switch (name)
            {
                case "IF":
                {
                    object condition = arg(0);
                    return condition != null && ToBool(condition) ? arg(1) : arg(2);
                }
                case "COALESCE":
                    foreach (Node child in args)
                    {
                        object value = Evaluate(child, field);
                        if (value != null) return value;
                    }
                    return null;
                case "CONCAT":
                    return string.Concat(args.Select(child => ToText(Evaluate(child, field))));
            }

            object first = arg(0);
            if (first == null) return null;
            switch (name)
            {
                case "ROUND":
                    return Math.Round(ToNumber(first), args.Count > 1 ? (int)Math.Max(0, Math.Min(10, ToNumber(arg(1)))) : 0, MidpointRounding.AwayFromZero);
                case "ABS": return Math.Abs(ToNumber(first));
                case "FLOOR": return Math.Floor(ToNumber(first));
                case "CEILING": return Math.Ceiling(ToNumber(first));
                case "UPPER": return ToText(first).ToUpperInvariant();
                case "LOWER": return ToText(first).ToLowerInvariant();
                case "TRIM": return ToText(first).Trim();
                case "LEN": return (decimal)ToText(first).Length;
                case "LEFT":
                {
                    string text = ToText(first);
                    int count = (int)Math.Max(0, Math.Min(text.Length, ToNumber(arg(1))));
                    return text.Substring(0, count);
                }
                case "RIGHT":
                {
                    string text = ToText(first);
                    int count = (int)Math.Max(0, Math.Min(text.Length, ToNumber(arg(1))));
                    return text.Substring(text.Length - count);
                }
                case "YEAR": return (decimal)ToDate(first).Year;
                case "MONTH": return (decimal)ToDate(first).Month;
                case "DAY": return (decimal)ToDate(first).Day;
                case "DATE": return ToDate(first).Date;
                case "NUMBER": return ToNumber(first);
                case "TEXT": return ToText(first);
                default:
                    throw new FormatException(Localization.Format("BiExpression.Error.Function", name));
            }
        }

        private static readonly Dictionary<string, KeyValuePair<int, int>> FunctionArity = new Dictionary<string, KeyValuePair<int, int>>(StringComparer.OrdinalIgnoreCase)
        {
            { "IF", new KeyValuePair<int, int>(3, 3) },
            { "COALESCE", new KeyValuePair<int, int>(1, 16) },
            { "CONCAT", new KeyValuePair<int, int>(1, 16) },
            { "ROUND", new KeyValuePair<int, int>(1, 2) },
            { "ABS", new KeyValuePair<int, int>(1, 1) },
            { "FLOOR", new KeyValuePair<int, int>(1, 1) },
            { "CEILING", new KeyValuePair<int, int>(1, 1) },
            { "UPPER", new KeyValuePair<int, int>(1, 1) },
            { "LOWER", new KeyValuePair<int, int>(1, 1) },
            { "TRIM", new KeyValuePair<int, int>(1, 1) },
            { "LEN", new KeyValuePair<int, int>(1, 1) },
            { "LEFT", new KeyValuePair<int, int>(2, 2) },
            { "RIGHT", new KeyValuePair<int, int>(2, 2) },
            { "YEAR", new KeyValuePair<int, int>(1, 1) },
            { "MONTH", new KeyValuePair<int, int>(1, 1) },
            { "DAY", new KeyValuePair<int, int>(1, 1) },
            { "DATE", new KeyValuePair<int, int>(1, 1) },
            { "NUMBER", new KeyValuePair<int, int>(1, 1) },
            { "TEXT", new KeyValuePair<int, int>(1, 1) }
        };

        /// <summary>資料庫讀出的值統一成 decimal、string、bool、DateTime 或 null。</summary>
        public static object Normalize(object value)
        {
            if (value == null || value is DBNull) return null;
            if (value is string || value is bool || value is DateTime) return value;
            if (value is DateTimeOffset) return ((DateTimeOffset)value).UtcDateTime;
            if (value is sbyte || value is byte || value is short || value is ushort || value is int || value is uint || value is long || value is ulong || value is decimal)
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            if (value is float || value is double)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number) || double.IsInfinity(number)) return null;
                return (decimal)number;
            }
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static decimal ToNumber(object value)
        {
            if (value is decimal) return (decimal)value;
            if (value is bool) return (bool)value ? 1m : 0m;
            decimal parsed;
            if (value is string && decimal.TryParse((string)value, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed)) return parsed;
            throw new FormatException(Localization.Format("BiExpression.Error.NotNumber", ToText(value)));
        }

        private static bool ToBool(object value)
        {
            if (value is bool) return (bool)value;
            if (value is decimal) return (decimal)value != 0m;
            string text = ToText(value).Trim().ToLowerInvariant();
            if (text == "true" || text == "1" || text == "yes") return true;
            if (text == "false" || text == "0" || text == "no" || text.Length == 0) return false;
            throw new FormatException(Localization.Format("BiExpression.Error.NotBoolean", ToText(value)));
        }

        private static DateTime ToDate(object value)
        {
            if (value is DateTime) return (DateTime)value;
            DateTime parsed;
            if (value is string && DateTime.TryParse((string)value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out parsed)) return parsed;
            throw new FormatException(Localization.Format("BiExpression.Error.NotDate", ToText(value)));
        }

        public static string ToText(object value)
        {
            if (value == null) return string.Empty;
            if (value is decimal) return ((decimal)value).ToString("0.############################", CultureInfo.InvariantCulture);
            if (value is DateTime)
            {
                DateTime date = (DateTime)value;
                return date.TimeOfDay == TimeSpan.Zero ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            if (value is bool) return (bool)value ? "true" : "false";
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int Compare(object a, object b)
        {
            if (a is decimal || b is decimal)
            {
                decimal left, right;
                if (TryNumber(a, out left) && TryNumber(b, out right)) return left.CompareTo(right);
            }
            if (a is DateTime || b is DateTime)
            {
                return ToDate(a).CompareTo(ToDate(b));
            }
            if (a is bool && b is bool) return ((bool)a).CompareTo((bool)b);
            return string.Compare(ToText(a), ToText(b), StringComparison.Ordinal);
        }

        private static bool TryNumber(object value, out decimal number)
        {
            try
            {
                number = ToNumber(value);
                return true;
            }
            catch (FormatException)
            {
                number = 0m;
                return false;
            }
        }

        // ------------------------------------------------------------ Parsing

        private enum NodeKind
        {
            Number,
            String,
            Null,
            Boolean,
            Field,
            Unary,
            Binary,
            Call
        }

        private sealed class Node
        {
            public Node()
            {
                Children = new List<Node>();
            }

            public NodeKind Kind;
            public string Text;
            public decimal Number;
            public List<Node> Children;
        }

        private enum TokenKind
        {
            Number,
            String,
            Field,
            Word,
            Symbol,
            End
        }

        private sealed class Token
        {
            public TokenKind Kind;
            public string Text;
            public int Position;
        }

        private static List<Token> Tokenize(string text)
        {
            List<Token> tokens = new List<Token>();
            int index = 0;
            while (index < text.Length)
            {
                char c = text[index];
                if (char.IsWhiteSpace(c))
                {
                    index++;
                    continue;
                }
                int start = index;
                if (c == '[')
                {
                    int end = text.IndexOf(']', index + 1);
                    if (end < 0) throw new FormatException(Localization.Format("BiExpression.Error.UnclosedField", start + 1));
                    string name = text.Substring(index + 1, end - index - 1).Trim();
                    if (name.Length == 0) throw new FormatException(Localization.Format("BiExpression.Error.EmptyField", start + 1));
                    tokens.Add(new Token { Kind = TokenKind.Field, Text = name, Position = start });
                    index = end + 1;
                    continue;
                }
                if (c == '\'')
                {
                    StringBuilder value = new StringBuilder();
                    index++;
                    while (true)
                    {
                        if (index >= text.Length) throw new FormatException(Localization.Format("BiExpression.Error.UnclosedString", start + 1));
                        if (text[index] == '\'')
                        {
                            if (index + 1 < text.Length && text[index + 1] == '\'')
                            {
                                value.Append('\'');
                                index += 2;
                                continue;
                            }
                            index++;
                            break;
                        }
                        value.Append(text[index++]);
                    }
                    tokens.Add(new Token { Kind = TokenKind.String, Text = value.ToString(), Position = start });
                    continue;
                }
                if (char.IsDigit(c) || c == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1]))
                {
                    while (index < text.Length && (char.IsDigit(text[index]) || text[index] == '.')) index++;
                    tokens.Add(new Token { Kind = TokenKind.Number, Text = text.Substring(start, index - start), Position = start });
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                    tokens.Add(new Token { Kind = TokenKind.Word, Text = text.Substring(start, index - start).ToUpperInvariant(), Position = start });
                    continue;
                }
                string two = index + 1 < text.Length ? text.Substring(index, 2) : string.Empty;
                if (two == "<=" || two == ">=" || two == "<>" || two == "!=")
                {
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Text = two == "!=" ? "<>" : two, Position = start });
                    index += 2;
                    continue;
                }
                if ("+-*/%()=<>,".IndexOf(c) >= 0)
                {
                    tokens.Add(new Token { Kind = TokenKind.Symbol, Text = c.ToString(), Position = start });
                    index++;
                    continue;
                }
                throw new FormatException(Localization.Format("BiExpression.Error.Character", c, start + 1));
            }
            tokens.Add(new Token { Kind = TokenKind.End, Text = string.Empty, Position = text.Length });
            return tokens;
        }

        private sealed class Parser
        {
            private readonly List<Token> tokens;
            private int position;

            public Parser(List<Token> tokens)
            {
                this.tokens = tokens;
            }

            private Token Current { get { return tokens[position]; } }

            private static int Precedence(Token token)
            {
                if (token.Kind == TokenKind.Word)
                {
                    if (token.Text == "OR") return 1;
                    if (token.Text == "AND") return 2;
                    return 0;
                }
                if (token.Kind != TokenKind.Symbol) return 0;
                switch (token.Text)
                {
                    case "=": case "<>": case "<": case "<=": case ">": case ">=": return 3;
                    case "+": case "-": return 4;
                    case "*": case "/": case "%": return 5;
                    default: return 0;
                }
            }

            public Node ParseExpression(int minimum, int depth)
            {
                if (depth > MaximumDepth) throw new FormatException(Localization.T("BiExpression.Error.TooDeep"));
                Node left = ParseUnary(depth);
                while (true)
                {
                    Token op = Current;
                    int precedence = Precedence(op);
                    if (precedence == 0 || precedence <= minimum) break;
                    position++;
                    Node right = ParseExpression(precedence, depth + 1);
                    Node binary = new Node { Kind = NodeKind.Binary, Text = op.Text };
                    binary.Children.Add(left);
                    binary.Children.Add(right);
                    left = binary;
                }
                return left;
            }

            private Node ParseUnary(int depth)
            {
                Token token = Current;
                if (token.Kind == TokenKind.Symbol && (token.Text == "-" || token.Text == "+") ||
                    token.Kind == TokenKind.Word && token.Text == "NOT")
                {
                    position++;
                    Node unary = new Node { Kind = NodeKind.Unary, Text = token.Text };
                    unary.Children.Add(token.Text == "NOT" ? ParseExpression(2, depth + 1) : ParseUnary(depth + 1));
                    return unary;
                }
                return ParsePrimary(depth);
            }

            private Node ParsePrimary(int depth)
            {
                Token token = Current;
                switch (token.Kind)
                {
                    case TokenKind.Number:
                        position++;
                        decimal number;
                        if (!decimal.TryParse(token.Text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out number))
                        {
                            throw new FormatException(Localization.Format("BiExpression.Error.Number", token.Text));
                        }
                        return new Node { Kind = NodeKind.Number, Number = number, Text = token.Text };
                    case TokenKind.String:
                        position++;
                        return new Node { Kind = NodeKind.String, Text = token.Text };
                    case TokenKind.Field:
                        position++;
                        return new Node { Kind = NodeKind.Field, Text = token.Text };
                    case TokenKind.Word:
                        position++;
                        if (token.Text == "NULL") return new Node { Kind = NodeKind.Null };
                        if (token.Text == "TRUE" || token.Text == "FALSE") return new Node { Kind = NodeKind.Boolean, Text = token.Text };
                        KeyValuePair<int, int> arity;
                        if (!FunctionArity.TryGetValue(token.Text, out arity)) throw new FormatException(Localization.Format("BiExpression.Error.Function", token.Text));
                        Expect("(");
                        Node call = new Node { Kind = NodeKind.Call, Text = token.Text };
                        if (!(Current.Kind == TokenKind.Symbol && Current.Text == ")"))
                        {
                            do
                            {
                                call.Children.Add(ParseExpression(0, depth + 1));
                            }
                            while (Accept(","));
                        }
                        Expect(")");
                        if (call.Children.Count < arity.Key || call.Children.Count > arity.Value)
                        {
                            throw new FormatException(Localization.Format("BiExpression.Error.Arity", token.Text, arity.Key, arity.Value));
                        }
                        return call;
                    case TokenKind.Symbol:
                        if (token.Text == "(")
                        {
                            position++;
                            Node inner = ParseExpression(0, depth + 1);
                            Expect(")");
                            return inner;
                        }
                        break;
                }
                throw Unexpected();
            }

            public void ExpectEnd()
            {
                if (Current.Kind != TokenKind.End) throw Unexpected();
            }

            private bool Accept(string symbol)
            {
                if (Current.Kind != TokenKind.Symbol || Current.Text != symbol) return false;
                position++;
                return true;
            }

            private void Expect(string symbol)
            {
                if (!Accept(symbol)) throw Unexpected();
            }

            private FormatException Unexpected()
            {
                return new FormatException(Current.Kind == TokenKind.End
                    ? Localization.T("BiExpression.Error.UnexpectedEnd")
                    : Localization.Format("BiExpression.Error.Unexpected", Current.Text, Current.Position + 1));
            }
        }
    }
}
