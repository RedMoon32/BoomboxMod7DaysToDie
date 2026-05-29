using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Boombox
{
    public class NetPackageBoomboxChatMessage : NetPackage
    {
        private static readonly Encoding Utf8 = Encoding.UTF8;

        private string _message = string.Empty;

        public NetPackageBoomboxChatMessage Setup(string message)
        {
            _message = message ?? string.Empty;
            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength()
        {
            return sizeof(int) + Utf8.GetByteCount(_message ?? string.Empty);
        }

        public override void read(PooledBinaryReader reader)
        {
            var length = reader.ReadInt32();
            _message = length > 0 ? Utf8.GetString(reader.ReadBytes(length)) : string.Empty;
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            var binaryWriter = (BinaryWriter)writer;
            var bytes = Utf8.GetBytes(_message ?? string.Empty);
            binaryWriter.Write(bytes.Length);
            if (bytes.Length > 0)
            {
                binaryWriter.BaseStream.Write(bytes, 0, bytes.Length);
            }
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            if (GameManager.IsDedicatedServer || string.IsNullOrEmpty(_message))
            {
                return;
            }

            try
            {
                var ui = LocalPlayerUI.GetUIForPrimaryPlayer();
                var xui = ui?.xui;
                if (xui == null)
                {
                    Debug.LogWarning("[Boombox] Chat UI message skipped (xui missing)");
                    return;
                }

                XUiC_ChatOutput.AddMessage(
                    xui,
                    EnumGameMessages.Chat,
                    _message,
                    EChatType.Global,
                    EChatDirection.Inbound,
                    -1,
                    "Boombox",
                    string.Empty,
                    EMessageSender.Server,
                    GeneratedTextManager.TextFilteringMode.None,
                    GeneratedTextManager.BbCodeSupportMode.NotSupported);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Boombox] Failed to add chat UI message: {ex}");
            }
        }
    }
}
