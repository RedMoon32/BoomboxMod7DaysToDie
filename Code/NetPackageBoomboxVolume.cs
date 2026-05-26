using System.IO;
using UnityEngine;

namespace Boombox
{
    public class NetPackageBoomboxVolume : NetPackage
    {
        private const float MaxVolumeMultiplier = 5f;
        private float _volume = 1f;

        public NetPackageBoomboxVolume Setup(float volume)
        {
            _volume = Mathf.Clamp(volume, 0f, MaxVolumeMultiplier);
            return this;
        }

        public override NetPackageDirection PackageDirection => NetPackageDirection.ToClient;

        public override int GetLength()
        {
            return sizeof(float);
        }

        public override void read(PooledBinaryReader reader)
        {
            _volume = Mathf.Clamp(reader.ReadSingle(), 0f, MaxVolumeMultiplier);
        }

        public override void write(PooledBinaryWriter writer)
        {
            base.write(writer);
            ((BinaryWriter)writer).Write(Mathf.Clamp(_volume, 0f, MaxVolumeMultiplier));
        }

        public override void ProcessPackage(World world, GameManager callbacks)
        {
            if (GameManager.IsDedicatedServer)
            {
                return;
            }

            BoomboxAudioManager.ClientSetVolume(_volume);
        }
    }
}
