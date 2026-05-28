using System.IO;
using System.Text;

namespace Boombox
{
    public class NetPackageBoomboxSongComplete : NetPackage
    {
        private static readonly Encoding Utf8 = Encoding.UTF8;

        private string _songId = string.Empty;
        private long _scheduledStartUtcTicks;

        public NetPackageBoomboxSongComplete Setup(string songId, long scheduledStartUtcTicks)
        {
            _songId = songId ?? string.Empty;
            _scheduledStartUtcTicks = scheduledStartUtcTicks;
            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength()
        {
            return sizeof(int) + Utf8.GetByteCount(_songId ?? string.Empty) + sizeof(long);
        }

        public override void read(PooledBinaryReader reader)
        {
            var length = reader.ReadInt32();
            _songId = length > 0 ? Utf8.GetString(reader.ReadBytes(length)) : string.Empty;
            _scheduledStartUtcTicks = reader.ReadInt64();
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            var binaryWriter = (BinaryWriter)writer;
            var bytes = Utf8.GetBytes(_songId ?? string.Empty);
            binaryWriter.Write(bytes.Length);
            if (bytes.Length > 0)
            {
                binaryWriter.BaseStream.Write(bytes, 0, bytes.Length);
            }

            binaryWriter.Write(_scheduledStartUtcTicks);
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            BoomboxRuntimeSongManager.ClientReceiveSongComplete(_songId, _scheduledStartUtcTicks);
        }
    }
}
