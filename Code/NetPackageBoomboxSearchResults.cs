using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Boombox
{
    public class NetPackageBoomboxSearchResults : NetPackage
    {
        private const int MaxSearchResults = 10;
        private static readonly Encoding Utf8 = Encoding.UTF8;

        private string query = string.Empty;
        private string error = string.Empty;
        private readonly List<MusicSearchItem> items = new List<MusicSearchItem>();

        public NetPackageBoomboxSearchResults Setup(string query, IReadOnlyList<MusicSearchItem> items, string error)
        {
            this.query = query ?? string.Empty;
            this.error = error ?? string.Empty;
            this.items.Clear();
            if (items != null)
            {
                for (var i = 0; i < items.Count && i < MaxSearchResults; i++)
                {
                    this.items.Add(items[i]);
                }
            }

            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength()
        {
            var total = StringLength(query) + StringLength(error) + sizeof(int);
            foreach (var item in items)
            {
                total += StringLength(item?.Source);
                total += StringLength(item?.Id);
                total += StringLength(item?.Title);
                total += StringLength(item?.Artist);
                total += StringLength(item?.Duration);
                total += StringLength(item?.DownloadPath);
            }

            return total;
        }

        public override void read(PooledBinaryReader reader)
        {
            query = ReadString(reader);
            error = ReadString(reader);
            items.Clear();
            var count = reader.ReadInt32();
            for (var i = 0; i < count && i < MaxSearchResults; i++)
            {
                items.Add(new MusicSearchItem(
                    ReadString(reader),
                    ReadString(reader),
                    ReadString(reader),
                    ReadString(reader),
                    ReadString(reader),
                    ReadString(reader)));
            }

            for (var i = MaxSearchResults; i < count; i++)
            {
                ReadString(reader);
                ReadString(reader);
                ReadString(reader);
                ReadString(reader);
                ReadString(reader);
                ReadString(reader);
            }
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            var binaryWriter = (BinaryWriter)writer;
            WriteString(binaryWriter, query);
            WriteString(binaryWriter, error);
            binaryWriter.Write(items.Count);
            foreach (var item in items)
            {
                WriteString(binaryWriter, item?.Source);
                WriteString(binaryWriter, item?.Id);
                WriteString(binaryWriter, item?.Title);
                WriteString(binaryWriter, item?.Artist);
                WriteString(binaryWriter, item?.Duration);
                WriteString(binaryWriter, item?.DownloadPath);
            }
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            if (GameManager.IsDedicatedServer)
            {
                return;
            }

            XUiC_BoomboxControlWindow.ClientReceiveSearchResults(query, items, error);
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
