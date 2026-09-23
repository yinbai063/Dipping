using System.Text;

namespace Dipping.Core.Triggers
{
    /// <summary>
    /// 从 TCP 字节流里拆行。自动识别 UTF-8/ASCII、UTF-16 LE、UTF-16 BE。
    /// 行尾按对方来的走：\r、\n 或 \r\n。
    /// </summary>
    internal static class TcpLineCodec
    {
        public static Encoding? TryDetect(IReadOnlyList<byte> data)
        {
            if (data.Count < 2)
                return null;

            if (data[0] == 0xFF && data[1] == 0xFE)
                return Encoding.Unicode;
            if (data[0] == 0xFE && data[1] == 0xFF)
                return Encoding.BigEndianUnicode;

            if (data[0] != 0 && data[1] == 0)
                return Encoding.Unicode;
            if (data[0] == 0 && data[1] != 0)
                return Encoding.BigEndianUnicode;
            if (data[0] != 0 && data[1] != 0)
                return Encoding.UTF8;

            return null;
        }

        public static bool TryTakeLine(List<byte> pending, Encoding encoding, out string line, out string newLine)
        {
            line = string.Empty;
            newLine = "\n";
            if (encoding.CodePage == 1200)
                return TryTakeUtf16Line(pending, littleEndian: true, out line, out newLine);
            if (encoding.CodePage == 1201)
                return TryTakeUtf16Line(pending, littleEndian: false, out line, out newLine);
            return TryTakeUtf8Line(pending, out line, out newLine);
        }

        public static byte[] EncodeLine(Encoding encoding, string text, string? newLine) =>
            encoding.GetBytes(text + (string.IsNullOrEmpty(newLine) ? "\n" : newLine));

        public static string NameOf(Encoding encoding) =>
            encoding.CodePage switch
            {
                1200 => "UTF-16 LE",
                1201 => "UTF-16 BE",
                _ => "UTF-8/ASCII"
            };

        private static bool TryTakeUtf8Line(List<byte> pending, out string line, out string newLine)
        {
            line = string.Empty;
            newLine = "\n";
            var cr = pending.IndexOf(0x0D);
            var lf = pending.IndexOf(0x0A);
            if (cr < 0 && lf < 0)
                return false;

            int textEnd;
            int consume;
            if (cr >= 0 && (lf < 0 || cr < lf))
            {
                textEnd = cr;
                if (cr + 1 < pending.Count && pending[cr + 1] == 0x0A)
                {
                    newLine = "\r\n";
                    consume = cr + 2;
                }
                else
                {
                    newLine = "\r";
                    consume = cr + 1;
                }
            }
            else
            {
                textEnd = lf;
                newLine = "\n";
                consume = lf + 1;
            }

            var start = 0;
            if (pending.Count >= 3 && pending[0] == 0xEF && pending[1] == 0xBB && pending[2] == 0xBF && textEnd >= 3)
                start = 3;
            line = Encoding.UTF8.GetString(pending.GetRange(start, textEnd - start).ToArray());
            pending.RemoveRange(0, consume);
            return true;
        }

        private static bool TryTakeUtf16Line(List<byte> pending, bool littleEndian, out string line, out string newLine)
        {
            line = string.Empty;
            newLine = "\n";
            var cr = FindUtf16Char(pending, littleEndian, 0x0D);
            var lf = FindUtf16Char(pending, littleEndian, 0x0A);
            if (cr < 0 && lf < 0)
                return false;

            int textEnd;
            int consume;
            if (cr >= 0 && (lf < 0 || cr < lf))
            {
                textEnd = cr;
                if (IsUtf16Char(pending, cr + 2, littleEndian, 0x0A))
                {
                    newLine = "\r\n";
                    consume = cr + 4;
                }
                else
                {
                    newLine = "\r";
                    consume = cr + 2;
                }
            }
            else
            {
                textEnd = lf;
                newLine = "\n";
                consume = lf + 2;
            }

            var start = 0;
            if (pending.Count >= 2
                && ((littleEndian && pending[0] == 0xFF && pending[1] == 0xFE)
                    || (!littleEndian && pending[0] == 0xFE && pending[1] == 0xFF)))
                start = 2;

            var raw = pending.GetRange(start, Math.Max(0, textEnd - start)).ToArray();
            line = (littleEndian ? Encoding.Unicode : Encoding.BigEndianUnicode).GetString(raw);
            pending.RemoveRange(0, consume);
            return true;
        }

        private static int FindUtf16Char(List<byte> pending, bool littleEndian, byte value)
        {
            for (var i = 0; i + 1 < pending.Count; i += 2)
            {
                if (IsUtf16Char(pending, i, littleEndian, value))
                    return i;
            }

            return -1;
        }

        private static bool IsUtf16Char(List<byte> pending, int index, bool littleEndian, byte value)
        {
            if (index < 0 || index + 1 >= pending.Count)
                return false;
            return littleEndian
                ? pending[index] == value && pending[index + 1] == 0
                : pending[index] == 0 && pending[index + 1] == value;
        }
    }
}
