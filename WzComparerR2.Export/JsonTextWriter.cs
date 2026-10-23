using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WzComparerR2.Export
{
    /// <summary>
    /// 极简 JSON 写入器：2 空格缩进、'\n' 换行、UTF-8 无 BOM。
    /// 不用 System.Text.Json 是为了保证同一输入在不同机器上产出的字节完全一致
    /// （缩进/换行/转义策略都由这里固定）。
    /// </summary>
    internal sealed class JsonTextWriter
    {
        private readonly StringBuilder builder;
        private readonly List<bool> hasItems;
        private bool afterName;

        public JsonTextWriter()
        {
            this.builder = new StringBuilder(4096);
            this.hasItems = new List<bool>(8);
        }

        public void StartObject()
        {
            this.BeforeValue();
            this.builder.Append('{');
            this.hasItems.Add(false);
        }

        public void EndObject()
        {
            this.CloseContainer('}');
        }

        public void StartArray()
        {
            this.BeforeValue();
            this.builder.Append('[');
            this.hasItems.Add(false);
        }

        public void EndArray()
        {
            this.CloseContainer(']');
        }

        public void Name(string name)
        {
            this.BeforeValue();
            AppendEscaped(this.builder, name);
            this.builder.Append(':').Append(' ');
            this.afterName = true;
        }

        public void Value(string value)
        {
            this.BeforeValue();
            if (value == null)
            {
                this.builder.Append("null");
            }
            else
            {
                AppendEscaped(this.builder, value);
            }
        }

        public void Value(int value)
        {
            this.BeforeValue();
            this.builder.Append(value);
        }

        public void Value(bool value)
        {
            this.BeforeValue();
            this.builder.Append(value ? "true" : "false");
        }

        public void Null()
        {
            this.BeforeValue();
            this.builder.Append("null");
        }

        /// <summary>写入一段已经是合法 JSON 的文本（用于把 [x, y] 这类短数组压成一行）。</summary>
        public void ValueRaw(string rawJson)
        {
            this.BeforeValue();
            this.builder.Append(rawJson);
        }

        public override string ToString()
        {
            return this.builder.ToString() + "\n";
        }

        /// <summary>写文件：UTF-8 无 BOM，'\n' 换行。</summary>
        public void WriteTo(string path)
        {
            File.WriteAllText(path, this.ToString(), new UTF8Encoding(false));
        }

        public byte[] ToBytes()
        {
            return new UTF8Encoding(false).GetBytes(this.ToString());
        }

        private void CloseContainer(char closeChar)
        {
            int index = this.hasItems.Count - 1;
            bool hadItems = this.hasItems[index];
            this.hasItems.RemoveAt(index);

            if (hadItems)
            {
                this.builder.Append('\n');
                this.AppendIndent(this.hasItems.Count);
            }
            this.builder.Append(closeChar);
        }

        private void BeforeValue()
        {
            if (this.afterName)
            {
                this.afterName = false;
                return;
            }

            if (this.hasItems.Count == 0)
            {
                return;
            }

            int index = this.hasItems.Count - 1;
            if (this.hasItems[index])
            {
                this.builder.Append(',');
            }
            else
            {
                this.hasItems[index] = true;
            }

            this.builder.Append('\n');
            this.AppendIndent(this.hasItems.Count);
        }

        private void AppendIndent(int depth)
        {
            this.builder.Append(' ', depth * 2);
        }

        private static void AppendEscaped(StringBuilder sb, string value)
        {
            sb.Append('"');
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                switch (c)
                {
                    case '"':
                        sb.Append("\\\"");
                        break;
                    case '\\':
                        sb.Append("\\\\");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    default:
                        if (c < 0x20 || c == '\u2028' || c == '\u2029')
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
