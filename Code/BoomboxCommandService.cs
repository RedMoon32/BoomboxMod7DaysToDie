using UnityEngine;

namespace Boombox
{
    public static class BoomboxCommandService
    {
        public static bool ExecuteServer(BoomboxCommandRequest request, World world, ClientInfo clientInfo, EntityPlayer player, int senderEntityId)
        {
            if (request == null || !IsServer())
            {
                return false;
            }

            switch (request.Type)
            {
                case BoomboxCommandType.PlayLocal:
                    return BoomboxRuntimeSongManager.ServerPlayLocal(request.Text, clientInfo, senderEntityId);
                case BoomboxCommandType.PlayOnline:
                    return BoomboxRuntimeSongManager.ServerPlayOnline(request.Text, clientInfo, senderEntityId);
                case BoomboxCommandType.SearchOnline:
                    return BoomboxRuntimeSongManager.ServerSearchOnline(request.Text, clientInfo, senderEntityId);
                case BoomboxCommandType.PlaySearchResult:
                    return BoomboxRuntimeSongManager.ServerPlaySearchResult(request.Number, clientInfo, senderEntityId);
                case BoomboxCommandType.QueueOnline:
                    return BoomboxRuntimeSongManager.ServerQueueOnline(request.Text, clientInfo, senderEntityId);
                case BoomboxCommandType.QueueSearchResult:
                    return BoomboxRuntimeSongManager.ServerQueueSearchResult(request.Number, clientInfo, senderEntityId);
                case BoomboxCommandType.SetVolume:
                    return BoomboxRuntimeSongManager.ServerSetVolume(request.Value, clientInfo, senderEntityId);
                case BoomboxCommandType.SetPreDelay:
                    return BoomboxRuntimeSongManager.ServerSetPreDelay(request.Value, clientInfo, senderEntityId);
                case BoomboxCommandType.ToggleBlock:
                    BoomboxAudioManager.ServerHandleToggle(world, request.ClrIdx, request.BlockPosition, clientInfo, player, false);
                    return true;
                case BoomboxCommandType.PickupBlock:
                    BoomboxAudioManager.ServerHandleToggle(world, request.ClrIdx, request.BlockPosition, clientInfo, player, true);
                    return true;
                case BoomboxCommandType.ClearQueue:
                    BoomboxRuntimeSongManager.ClearServerQueue("ui command");
                    BoomboxRuntimeSongManager.SendReply(clientInfo, senderEntityId, "Queue cleared");
                    return true;
                case BoomboxCommandType.Stop:
                    return BoomboxRuntimeSongManager.ServerStop(clientInfo, senderEntityId);
                default:
                    Debug.LogWarning($"[Boombox] Unsupported command type: {request.Type}");
                    return false;
            }
        }

        public static void SendToServerOrExecuteLocal(BoomboxCommandRequest request, EntityPlayerLocal player)
        {
            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            var world = GameManager.Instance?.World;
            var senderEntityId = player != null ? ((Entity)player).entityId : -1;

            if (connection != null && connection.IsClient)
            {
                connection.SendToServer(NetPackageManager.GetPackage<NetPackageBoomboxCommandRequest>().Setup(request), false);
                return;
            }

            if (IsServer())
            {
                ExecuteServer(request, world, null, player, senderEntityId);
            }
        }

        private static bool IsServer()
        {
            var connection = SingletonMonoBehaviour<ConnectionManager>.Instance;
            return GameManager.IsDedicatedServer || connection != null && connection.IsServer;
        }
    }
}
