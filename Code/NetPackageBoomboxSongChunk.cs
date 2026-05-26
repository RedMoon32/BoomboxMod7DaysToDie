using System.IO;
using System.Text;

namespace Boombox
{
    public class NetPackageBoomboxSongChunk : NetPackage
    {
        private static readonly Encoding Utf8 = Encoding.UTF8;

        private string _songId = string.Empty;
        private int _chunkIndex;
        private byte[] _bytes = new byte[0];

        public NetPackageBoomboxSongChunk Setup(string songId, int chunkIndex, byte[] bytes)
        {
            _songId = songId ?? string.Empty;
            _chunkIndex = chunkIndex;
            _bytes = bytes ?? new byte[0];
            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength()
        {
            return sizeof(int) + Utf8.GetByteCount(_songId ?? string.Empty) + sizeof(int) + sizeof(int) + (_bytes?.Length ?? 0);
        }

        public override void read(PooledBinaryReader reader)
        {
            var songIdLength = reader.ReadInt32();
            _songId = songIdLength > 0 ? Utf8.GetString(reader.ReadBytes(songIdLength)) : string.Empty;
            _chunkIndex = reader.ReadInt32();
            var byteCount = reader.ReadInt32();
            _bytes = byteCount > 0 ? reader.ReadBytes(byteCount) : new byte[0];
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            var binaryWriter = (BinaryWriter)writer;
            var songIdBytes = Utf8.GetBytes(_songId ?? string.Empty);
            binaryWriter.Write(songIdBytes.Length);
            if (songIdBytes.Length > 0)
            {
                binaryWriter.BaseStream.Write(songIdBytes, 0, songIdBytes.Length);
            }

            binaryWriter.Write(_chunkIndex);
            binaryWriter.Write(_bytes?.Length ?? 0);
            if (_bytes != null && _bytes.Length > 0)
            {
                binaryWriter.BaseStream.Write(_bytes, 0, _bytes.Length);
            }
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            BoomboxRuntimeSongManager.ClientReceiveSongChunk(_songId, _chunkIndex, _bytes);
        }
    }
}
