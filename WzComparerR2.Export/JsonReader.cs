using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 极简 JSON 读取器：只覆盖本工程自己的产物（manifest.json / meta.json）。
    /// 手写而不是用 System.Text.Json，理由与写出侧一致：不引入额外依赖、行为可控。
    /// </summary>
    internal sealed class JsonReader
    {
        private readonly string text;
        private int position;

        private JsonReader(string text)
        {
            this.text = text;
            this.position = 0;
        }

        public static JsonNode Parse(string text)
        {
            var reader = new JsonReader(text);
            JsonNode root = reader.ReadValue();
            reader.SkipWhitespace();
            if (reader.position != reader.text.Length)
            {
                throw new FormatException($"JSON 解析后仍有余下内容（位置 {reader.position}）。");
            }
            return root;
        }

        private JsonNode ReadValue()
        {
            this.SkipWhitespace();
            if (this.position >= this.text.Length)
            {
                throw new FormatException("JSON 提前结束。");
            }

            char c = this.text[this.position];
            switch (c)
            {
                case '{':
                    return this.ReadObject();
                case '[':
                    return this.ReadArray();
                case '"':
                    return JsonNode.FromString(this.ReadString());
                case 't':
                    this.Expect("true");
                    return JsonNode.FromBool(true);
                case 'f':
                    this.Expect("false");
                    return JsonNode.FromBool(false);
                case 'n':
                    this.Expect("null");
                    return JsonNode.FromNull();
                default:
                    return this.ReadNumber();
            }
        }

        private JsonNode ReadObject()
        {
            this.position++; // {
            var properties = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
            var order = new List<string>();

            this.SkipWhitespace();
            if (this.Peek() == '}')
            {
                this.position++;
                return JsonNode.FromObject(properties, order);
            }

            while (true)
            {
                this.SkipWhitespace();
                string name = this.ReadString();
                this.SkipWhitespace();
                if (this.Peek() != ':')
                {
                    throw new FormatException($"JSON 对象缺少 ':'（位置 {this.position}）。");
                }
                this.position++;

                JsonNode value = this.ReadValue();
                properties[name] = value;
                order.Add(name);

                this.SkipWhitespace();
                char next = this.Peek();
                this.position++;
                if (next == '}')
                {
                    break;
                }
                if (next != ',')
                {
                    throw new FormatException($"JSON 对象缺少 ',' 或 '}}'（位置 {this.position}）。");
                }
            }

            return JsonNode.FromObject(properties, order);
        }

        private JsonNode ReadArray()
        {
            this.position++; // [
            var items = new List<JsonNode>();

            this.SkipWhitespace();
            if (this.Peek() == ']')
            {
                this.position++;
                return JsonNode.FromArray(items);
            }

            while (true)
            {
                items.Add(this.ReadValue());

                this.SkipWhitespace();
                char next = this.Peek();
                this.position++;
                if (next == ']')
                {
                    break;
                }
                if (next != ',')
                {
                    throw new FormatException($"JSON 数组缺少 ',' 或 ']'（位置 {this.position}）。");
                }
            }

            return JsonNode.FromArray(items);
        }

        private string ReadString()
        {
            if (this.Peek() != '"')
            {
                throw new FormatException($"JSON 字符串缺少起始引号（位置 {this.position}）。");
            }
            this.position++;

            var sb = new StringBuilder();
            while (this.position < this.text.Length)
            {
                char c = this.text[this.position++];
                if (c == '"')
                {
                    return sb.ToString();
                }
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }

                char escape = this.text[this.position++];
                switch (escape)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        string hex = this.text.Substring(this.position, 4);
                        sb.Append((char)int.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        this.position += 4;
                        break;
                    default:
                        throw new FormatException($"JSON 字符串含非法转义 '\\{escape}'。");
                }
            }

            throw new FormatException("JSON 字符串没有闭合。");
        }

        private JsonNode ReadNumber()
        {
            int start = this.position;
            while (this.position < this.text.Length)
            {
                char c = this.text[this.position];
                if ((c >= '0' && c <= '9') || c == '-' || c == '+' || c == '.' || c == 'e' || c == 'E')
                {
                    this.position++;
                }
                else
                {
                    break;
                }
            }

            string token = this.text.Substring(start, this.position - start);
            if (long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out long integer))
            {
                return JsonNode.FromNumber(integer, integer);
            }
            if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
            {
                return JsonNode.FromNumber(number, (long)number);
            }
            throw new FormatException($"JSON 数字无法解析：{token}");
        }

        private void Expect(string token)
        {
            if (string.CompareOrdinal(this.text, this.position, token, 0, token.Length) != 0)
            {
                throw new FormatException($"JSON 期望 '{token}'（位置 {this.position}）。");
            }
            this.position += token.Length;
        }

        private char Peek()
        {
            if (this.position >= this.text.Length)
            {
                throw new FormatException("JSON 提前结束。");
            }
            return this.text[this.position];
        }

        private void SkipWhitespace()
        {
            while (this.position < this.text.Length)
            {
                char c = this.text[this.position];
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                {
                    this.position++;
                }
                else
                {
                    break;
                }
            }
        }
    }

    internal enum JsonNodeKind
    {
        Object,
        Array,
        String,
        Number,
        Boolean,
        Null,
    }

    /// <summary>JSON 值。只为产物校验提供够用的取值方法。</summary>
    internal sealed class JsonNode
    {
        private Dictionary<string, JsonNode> properties;
        private List<string> propertyOrder;

        private JsonNode(JsonNodeKind kind)
        {
            this.Kind = kind;
        }

        public JsonNodeKind Kind { get; }

        public string StringValue { get; private set; }

        public double NumberValue { get; private set; }

        public long IntValue { get; private set; }

        public bool BooleanValue { get; private set; }

        public IReadOnlyList<JsonNode> Items { get; private set; }

        public IEnumerable<string> PropertyNames => this.propertyOrder ?? (IEnumerable<string>)Array.Empty<string>();

        public static JsonNode FromString(string value) => new JsonNode(JsonNodeKind.String) { StringValue = value };

        public static JsonNode FromNumber(double value, long intValue) => new JsonNode(JsonNodeKind.Number) { NumberValue = value, IntValue = intValue };

        public static JsonNode FromBool(bool value) => new JsonNode(JsonNodeKind.Boolean) { BooleanValue = value };

        public static JsonNode FromNull() => new JsonNode(JsonNodeKind.Null);

        public static JsonNode FromArray(List<JsonNode> items) => new JsonNode(JsonNodeKind.Array) { Items = items };

        public static JsonNode FromObject(Dictionary<string, JsonNode> values, List<string> order)
        {
            var node = new JsonNode(JsonNodeKind.Object);
            node.properties = values;
            node.propertyOrder = order;
            return node;
        }

        public JsonNode this[string name]
        {
            get
            {
                if (this.properties == null || name == null)
                {
                    return null;
                }
                return this.properties.TryGetValue(name, out JsonNode value) ? value : null;
            }
        }

        public bool Has(string name) => this[name] != null;

        public string GetString(string name) => this[name]?.StringValue;

        public int? GetInt(string name)
        {
            JsonNode node = this[name];
            if (node == null || node.Kind != JsonNodeKind.Number)
            {
                return null;
            }
            return (int)node.IntValue;
        }

        public bool? GetBool(string name)
        {
            JsonNode node = this[name];
            if (node == null || node.Kind != JsonNodeKind.Boolean)
            {
                return null;
            }
            return node.BooleanValue;
        }

        public IReadOnlyList<JsonNode> GetArray(string name) => this[name]?.Items ?? (IReadOnlyList<JsonNode>)Array.Empty<JsonNode>();
    }
}
