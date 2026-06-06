using System.IO;
using System.Text;
using UnityEngine;

namespace Boombox
{
    public class NetPackageBoomboxCommandRequest : NetPackage
    {
        private static readonly Encoding Utf8 = Encoding.UTF8;

        private BoomboxCommandRequest _request = new BoomboxCommandRequest();

        public NetPackageBoomboxCommandRequest Setup(BoomboxCommandRequest request)
        {
            _request = request ?? new BoomboxCommandRequest();
            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToServer;

        public override int GetLength()
        {
            return sizeof(int) * 8 + sizeof(float) + StringLength(_request.Text);
        }

        public override void read(PooledBinaryReader reader)
        {
            _request = new BoomboxCommandRequest
            {
                Type = (BoomboxCommandType)reader.ReadInt32(),
                Source = (BoomboxCommandSource)reader.ReadInt32(),
                Text = ReadString(reader),
                Number = reader.ReadInt32(),
                Value = reader.ReadSingle(),
                BlockPosition = new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()),
                ClrIdx = reader.ReadInt32()
            };
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            var binaryWriter = (BinaryWriter)writer;
            binaryWriter.Write((int)_request.Type);
            binaryWriter.Write((int)_request.Source);
            WriteString(binaryWriter, _request.Text);
            binaryWriter.Write(_request.Number);
            binaryWriter.Write(_request.Value);
            binaryWriter.Write(_request.BlockPosition.x);
            binaryWriter.Write(_request.BlockPosition.y);
            binaryWriter.Write(_request.BlockPosition.z);
            binaryWriter.Write(_request.ClrIdx);
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            if (!GameManager.IsDedicatedServer && (connection == null || !connection.IsServer))
            {
                return;
            }

            var client = Sender;
            var senderEntityId = client?.entityId ?? -1;
            EntityPlayer player = null;
            if (world != null && senderEntityId != -1)
            {
                player = world.GetEntity(senderEntityId) as EntityPlayer;
            }

            BoomboxCommandService.ExecuteServer(_request, world, client, player, senderEntityId);
        }

        private static int StringLength(string value)
        {
            return sizeof(int) + Utf8.GetByteCount(value ?? string.Empty);
        }

        private static string ReadString(BinaryReader reader)
        {
            var length = reader.ReadInt32();
            return length > 0 ? Utf8.GetString(reader.ReadBytes(length)) : string.Empty;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Utf8.GetBytes(value ?? string.Empty);
            writer.Write(bytes.Length);
            if (bytes.Length > 0)
            {
                writer.BaseStream.Write(bytes, 0, bytes.Length);
            }
        }
    }
}
