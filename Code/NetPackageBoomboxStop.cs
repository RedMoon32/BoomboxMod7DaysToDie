using UnityEngine;

namespace Boombox
{
    public class NetPackageBoomboxStop : NetPackage
    {
        private Vector3i _position;

        public NetPackageBoomboxStop Setup(Vector3i position)
        {
            _position = position;
            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength() => sizeof(int) * 3;

        public override void read(PooledBinaryReader reader)
        {
            _position = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
        }

        public override void write(PooledBinaryWriter writer)
        {
            writer.Write(_position.x);
            writer.Write(_position.y);
            writer.Write(_position.z);
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            if (GameManager.IsDedicatedServer)
            {
                return;
            }

            BoomboxAudioManager.ClientStop(_position);
        }
    }
}
