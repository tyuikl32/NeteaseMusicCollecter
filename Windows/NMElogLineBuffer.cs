using System;
using System.Collections.Generic;
using System.Text;

namespace NeteaseMusicCollecter.Windows
{
    /// <summary>
    /// 把已反混淆的 UTF-8 字节流切成完整日志行。
    /// <para>
    /// 必须按字节缓冲的原因：真实日志里单行最长可达约 600 KB（<c>【persistentState】</c> 之类），
    /// 一次 64 KB 的读取一定会把一行切成几段；同时 UTF-8 多字节字符也可能跨块，
    /// 因此只按 <c>\n</c> 切分、绝不按读取块切分。
    /// </para>
    /// </summary>
    internal sealed class NMElogLineBuffer
    {
        private const int InitialCapacity = 64 * 1024;
        private const int MaxPendingBytes = 4 * 1024 * 1024;

        private byte[] _buffer = new byte[InitialCapacity];
        private int _length;

        /// <summary>未成行的残留字节数。</summary>
        public int PendingBytes => _length;

        /// <summary>追加一段已反混淆的字节。</summary>
        public void Append(ReadOnlySpan<byte> decoded)
        {
            if (decoded.IsEmpty)
                return;

            EnsureCapacity(_length + decoded.Length);
            decoded.CopyTo(_buffer.AsSpan(_length));
            _length += decoded.Length;
        }

        /// <summary>
        /// 取出所有已完整（以 <c>\n</c> 结尾）的行，并把不完整的尾部留在缓冲里。
        /// </summary>
        public List<string> DrainLines()
        {
            List<string> lines = new List<string>();

            int start = 0;

            while (start < _length)
            {
                int newline = _buffer.AsSpan(start, _length - start).IndexOf((byte)'\n');

                if (newline < 0)
                    break;

                newline += start;
                AppendLine(lines, start, newline - start);
                start = newline + 1;
            }

            if (start > 0)
            {
                int remaining = _length - start;

                if (remaining > 0)
                    Buffer.BlockCopy(_buffer, start, _buffer, 0, remaining);

                _length = remaining;
            }

            // 兜底：真实日志单行最长约 600 KB，正常远达不到上限。一旦残留超过上限，
            // 说明数据源异常（例如读到被破坏的文件），丢弃残留避免内存无限增长；
            // 已经切出来的完整行不受影响。
            if (_length > MaxPendingBytes)
                _length = 0;

            return lines;
        }

        /// <summary>丢弃所有残留（日志被清空或重置时使用）。</summary>
        public void Reset()
        {
            _length = 0;
        }

        private void AppendLine(List<string> lines, int offset, int count)
        {
            if (count <= 0)
                return;

            // 行内可能带 Windows 换行（真实日志里 http_file 之类的模块会写 \r\n）。
            string line = Encoding.UTF8.GetString(_buffer, offset, count).Trim();

            if (line.Length > 0)
                lines.Add(line);
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length)
                return;

            int capacity = _buffer.Length;

            while (capacity < required)
                capacity *= 2;

            byte[] grown = new byte[capacity];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _length);
            _buffer = grown;
        }
    }
}
