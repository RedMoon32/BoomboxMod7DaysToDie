using UnityEngine;

namespace Boombox
{
    public class BoomboxModApi : IModApi
    {
        public void InitMod(Mod modInstance)
        {
            Debug.Log("[Boombox] Mod API initialized");
            BoomboxAudioManager.Initialize();
        }
    }
}
