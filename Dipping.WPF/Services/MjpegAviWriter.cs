using System.Text;
using System.IO;

namespace Dipping.WPF.Services
{
    /// <summary>Motion JPEG AVI. Frame rate is patched on Finish from wall-clock duration.</summary>
    internal sealed class MjpegAviWriter : IDisposable
    {
        private const int AvihBytes = 56;
        private const int StrhBytes = 56;
        private const int StrfBytes = 40;
        private const int AvifHasIndex = 0x10;
        private const int AvifIsInterleaved = 0x100;

        private FileStream? _stream;
        private readonly List<(int Offset, int Size)> _index = new();
        private int _width;
        private int _height;
        private int _maxChunk;
        private long _riffSizePos;
        private long _hdrlSizePos;
        private long _avihPos;
        private long _strhPos;
        private long _moviListSizePos;
        private long _moviFourCcPos;

        public int FrameCount => _index.Count;
        public int Width => _width;
        public int Height => _height;

        public void Start(string path, int width, int height)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentOutOfRangeException(nameof(width), "invalid image size");

            _width = width;
            _height = height;
            _stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            WriteFourCc("RIFF");
            _riffSizePos = _stream.Position;
            WriteInt(0);
            WriteFourCc("AVI ");

            WriteFourCc("LIST");
            _hdrlSizePos = _stream.Position;
            WriteInt(0);
            var hdrlData = _stream.Position;
            WriteFourCc("hdrl");
            WriteFourCc("avih");
            WriteInt(AvihBytes);
            _avihPos = _stream.Position;
            WriteZeros(AvihBytes);

            WriteFourCc("LIST");
            WriteInt(4 + 8 + StrhBytes + 8 + StrfBytes);
            WriteFourCc("strl");
            WriteFourCc("strh");
            WriteInt(StrhBytes);
            _strhPos = _stream.Position;
            WriteZeros(StrhBytes);
            WriteFourCc("strf");
            WriteInt(StrfBytes);
            WriteInt(40);
            WriteInt(_width);
            WriteInt(_height);
            WriteShort(1);
            WriteShort(24);
            WriteFourCc("MJPG");
            WriteInt(_width * _height * 3);
            WriteInt(0);
            WriteInt(0);
            WriteInt(0);
            WriteInt(0);

            var hdrlEnd = _stream.Position;
            PatchInt(_hdrlSizePos, (int)(hdrlEnd - hdrlData));

            WriteFourCc("LIST");
            _moviListSizePos = _stream.Position;
            WriteInt(0);
            _moviFourCcPos = _stream.Position;
            WriteFourCc("movi");
        }

        public void AddJpeg(byte[] jpeg)
        {
            if (_stream == null)
                throw new InvalidOperationException("writer not started");
            if (jpeg == null || jpeg.Length == 0)
                return;

            var offset = (int)(_stream.Position - _moviFourCcPos);
            WriteFourCc("00dc");
            WriteInt(jpeg.Length);
            _stream.Write(jpeg, 0, jpeg.Length);
            if ((jpeg.Length & 1) != 0)
                _stream.WriteByte(0);
            _index.Add((offset, jpeg.Length));
            if (jpeg.Length > _maxChunk)
                _maxChunk = jpeg.Length;
        }

        public void Finish(double durationSeconds)
        {
            if (_stream == null)
                return;

            var frames = _index.Count;
            var fps = frames <= 0 || durationSeconds < 0.05
                ? 10.0
                : Math.Clamp(frames / durationSeconds, 1.0, 30.0);
            var microSec = (int)Math.Round(1_000_000.0 / fps);
            if (microSec < 1)
                microSec = 1;
            var scale = 1000;
            var rate = Math.Max(1, (int)Math.Round(fps * scale));

            var moviEnd = _stream.Position;
            PatchInt(_moviListSizePos, (int)(moviEnd - _moviFourCcPos));

            WriteFourCc("idx1");
            WriteInt(_index.Count * 16);
            foreach (var (offset, size) in _index)
            {
                WriteFourCc("00dc");
                WriteInt(0x10);
                WriteInt(offset);
                WriteInt(size);
            }

            var fileEnd = _stream.Position;
            PatchInt(_riffSizePos, (int)(fileEnd - 8));

            _stream.Position = _avihPos;
            WriteInt(microSec);
            WriteInt(_maxChunk * Math.Max(1, (int)Math.Round(fps)));
            WriteInt(0);
            WriteInt(AvifHasIndex | AvifIsInterleaved);
            WriteInt(frames);
            WriteInt(0);
            WriteInt(1);
            WriteInt(Math.Max(_maxChunk, _width * _height * 3));
            WriteInt(_width);
            WriteInt(_height);
            WriteZeros(16);

            _stream.Position = _strhPos;
            WriteFourCc("vids");
            WriteFourCc("MJPG");
            WriteInt(0);
            WriteInt(0);
            WriteInt(0);
            WriteInt(scale);
            WriteInt(rate);
            WriteInt(0);
            WriteInt(frames);
            WriteInt(Math.Max(_maxChunk, 0));
            WriteInt(unchecked((int)0xFFFFFFFF));
            WriteInt(0);
            WriteShort(0);
            WriteShort(0);
            WriteShort((short)_width);
            WriteShort((short)_height);

            _stream.Flush();
            _stream.Dispose();
            _stream = null;
        }

        public void Dispose()
        {
            if (_stream == null)
                return;
            try { Finish(Math.Max(0.05, _index.Count / 10.0)); }
            catch { try { _stream.Dispose(); } catch { } _stream = null; }
        }

        private void WriteFourCc(string code)
        {
            var bytes = Encoding.ASCII.GetBytes(code);
            if (bytes.Length != 4)
                throw new ArgumentException(code);
            _stream!.Write(bytes, 0, 4);
        }

        private void WriteInt(int value)
        {
            _stream!.WriteByte((byte)value);
            _stream.WriteByte((byte)(value >> 8));
            _stream.WriteByte((byte)(value >> 16));
            _stream.WriteByte((byte)(value >> 24));
        }

        private void WriteShort(int value)
        {
            _stream!.WriteByte((byte)value);
            _stream.WriteByte((byte)(value >> 8));
        }

        private void WriteZeros(int count)
        {
            var zeros = new byte[count];
            _stream!.Write(zeros, 0, count);
        }

        private void PatchInt(long position, int value)
        {
            var here = _stream!.Position;
            _stream.Position = position;
            WriteInt(value);
            _stream.Position = here;
        }
    }
}
