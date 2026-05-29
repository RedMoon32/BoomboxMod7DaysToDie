using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Boombox
{
    public class NetPackageBoomboxSongStart : NetPackage
    {
        private static readonly Encoding Utf8 = Encoding.UTF8;

        private string _songId = string.Empty;
        private string _songName = string.Empty;
        private string _extension = string.Empty;
        private long _totalBytes;
        private bool _notifyServerOnFinished;
        private string _finishedClipName = string.Empty;
        private readonly List<Vector3i> _positions = new List<Vector3i>();

        public NetPackageBoomboxSongStart Setup(string songId, string songName, string extension, long totalBytes, List<Vector3i> positions)
        {
            return Setup(songId, songName, extension, totalBytes, positions, false, string.Empty);
        }

        public NetPackageBoomboxSongStart Setup(string songId, string songName, string extension, long totalBytes, List<Vector3i> positions, bool notifyServerOnFinished, string finishedClipName)
        {
            _songId = songId ?? string.Empty;
            _songName = songName ?? string.Empty;
            _extension = extension ?? string.Empty;
            _totalBytes = totalBytes;
            _notifyServerOnFinished = notifyServerOnFinished;
            _finishedClipName = finishedClipName ?? string.Empty;
            _positions.Clear();
            if (positions != null)
            {
                _positions.AddRange(positions);
            }

            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength()
        {
            return StringLength(_songId) + StringLength(_songName) + StringLength(_extension) + sizeof(long) + sizeof(bool) + StringLength(_finishedClipName) + sizeof(int) + _positions.Count * sizeof(int) * 3;
        }

        public override void read(PooledBinaryReader reader)
        {
            _songId = ReadString(reader);
            _songName = ReadString(reader);
            _extension = ReadString(reader);
            _totalBytes = reader.ReadInt64();
            _notifyServerOnFinished = reader.ReadBoolean();
            _finishedClipName = ReadString(reader);
            _positions.Clear();
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                _positions.Add(new Vector3i(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32()));
            }
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            var binaryWriter = (BinaryWriter)writer;
            WriteString(binaryWriter, _songId);
            WriteString(binaryWriter, _songName);
            WriteString(binaryWriter, _extension);
            binaryWriter.Write(_totalBytes);
            binaryWriter.Write(_notifyServerOnFinished);
            WriteString(binaryWriter, _finishedClipName);
            binaryWriter.Write(_positions.Count);
            foreach (var position in _positions)
            {
                binaryWriter.Write(position.x);
                binaryWriter.Write(position.y);
                binaryWriter.Write(position.z);
            }
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            BoomboxRuntimeSongManager.ClientReceiveSongStart(_songId, _songName, _extension, _totalBytes, _positions, _notifyServerOnFinished, _finishedClipName);
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
