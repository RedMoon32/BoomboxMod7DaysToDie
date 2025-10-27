using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Boombox
{
    public class NetPackageBoomboxSync : NetPackage
    {
        private readonly List<BoomboxAudioManager.BoomboxStateSnapshot> _states = new List<BoomboxAudioManager.BoomboxStateSnapshot>();
        private static readonly Encoding Utf8 = Encoding.UTF8;

        public NetPackageBoomboxSync Setup(IReadOnlyCollection<BoomboxAudioManager.BoomboxStateSnapshot> states)
        {
            _states.Clear();
            if (states != null)
            {
                _states.AddRange(states);
            }

            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength()
        {
            var total = sizeof(int);
            foreach (var entry in _states)
            {
                var clipLength = string.IsNullOrEmpty(entry.ClipName) ? 0 : entry.ClipName.Length;
                var byteCount = clipLength == 0 ? 0 : Utf8.GetByteCount(entry.ClipName);
                total += sizeof(int) * 4 + byteCount;
            }

            return total;
        }

        public override void read(PooledBinaryReader reader)
        {
            _states.Clear();
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var position = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                var length = reader.ReadInt32();
                string clip;
                if (length > 0)
                {
                    var buffer = reader.ReadBytes(length);
                    clip = Utf8.GetString(buffer, 0, buffer.Length);
                }
                else
                {
                    clip = string.Empty;
                }
                _states.Add(new BoomboxAudioManager.BoomboxStateSnapshot(position, clip));
            }
        }

        public override void write(PooledBinaryWriter writer)
        {
            var binaryWriter = (BinaryWriter)writer;
            binaryWriter.Write(_states.Count);
            foreach (var entry in _states)
            {
                binaryWriter.Write(entry.Position.x);
                binaryWriter.Write(entry.Position.y);
                binaryWriter.Write(entry.Position.z);
                var clip = entry.ClipName ?? string.Empty;
                var bytes = Utf8.GetBytes(clip);
                binaryWriter.Write(bytes.Length);
                if (bytes.Length > 0)
                {
                    binaryWriter.BaseStream.Write(bytes, 0, bytes.Length);
                }
            }
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            if (GameManager.IsDedicatedServer)
            {
                return;
            }

            BoomboxAudioManager.ClientSync(_states);
        }
    }
}
