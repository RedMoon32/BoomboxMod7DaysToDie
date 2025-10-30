using System.IO;
using System.Text;
using UnityEngine;

namespace Boombox
{
    public class NetPackageBoomboxPlay : NetPackage
    {
        private Vector3i _position;
        private string _clipName = string.Empty;
        private static readonly Encoding Utf8 = Encoding.UTF8;

        public NetPackageBoomboxPlay Setup(Vector3i position, string clipName)
        {
            _position = position;
            _clipName = clipName ?? string.Empty;
            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength()
        {
            var clip = _clipName ?? string.Empty;
            var byteCount = Utf8.GetByteCount(clip);
            return sizeof(int) * 4 + byteCount;
        }

        public override void read(PooledBinaryReader reader)
        {
            _position = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            var length = reader.ReadInt32();
            if (length > 0)
            {
                var buffer = reader.ReadBytes(length);
                _clipName = Utf8.GetString(buffer, 0, buffer.Length);
            }
            else
            {
                _clipName = string.Empty;
            }
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            var binaryWriter = (BinaryWriter)writer;
            binaryWriter.Write(_position.x);
            binaryWriter.Write(_position.y);
            binaryWriter.Write(_position.z);

            var clip = _clipName ?? string.Empty;
            var bytes = Utf8.GetBytes(clip);
            binaryWriter.Write(bytes.Length);
            if (bytes.Length > 0)
            {
                binaryWriter.BaseStream.Write(bytes, 0, bytes.Length);
            }
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            if (GameManager.IsDedicatedServer)
            {
                return;
            }

            BoomboxAudioManager.ClientPlay(_position, _clipName);
        }
    }
}
