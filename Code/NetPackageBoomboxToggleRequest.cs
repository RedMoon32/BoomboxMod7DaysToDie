using System.IO;
using UnityEngine;

namespace Boombox
{
    public class NetPackageBoomboxToggleRequest : NetPackage
    {
        private Vector3i _position;
        private int _clrIdx;
        private bool _pickup;

        public NetPackageBoomboxToggleRequest Setup(Vector3i position, int clrIdx, bool pickup)
        {
            _position = position;
            _clrIdx = clrIdx;
            _pickup = pickup;
            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToServer;

        public override int GetLength() => sizeof(int) * 4 + sizeof(bool);

        public override void read(PooledBinaryReader reader)
        {
            _position = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
            _clrIdx = reader.ReadInt32();
            _pickup = reader.ReadBoolean();
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            var binaryWriter = (BinaryWriter)writer;
            binaryWriter.Write(_position.x);
            binaryWriter.Write(_position.y);
            binaryWriter.Write(_position.z);
            binaryWriter.Write(_clrIdx);
            binaryWriter.Write(_pickup);
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            if (world == null)
            {
                return;
            }

            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (!GameManager.IsDedicatedServer && (connection == null || !connection.IsServer))
            {
                return;
            }

            var client = Sender;
            EntityPlayer player = null;
            if (client != null && client.entityId != -1)
            {
                player = world.GetEntity(client.entityId) as EntityPlayer;
            }

            BoomboxAudioManager.ServerHandleToggle(world, _clrIdx, _position, client, player, _pickup);
        }
    }
}
