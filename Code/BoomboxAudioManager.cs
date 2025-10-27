using System;
using System.Collections.Generic;
using Audio;
using UnityEngine;

namespace Boombox
{
    public static class BoomboxAudioManager
    {
        private const string SoundName = "boombox_music";
        private static readonly Dictionary<Vector3i, Handle> ActiveHandles = new();
        private static readonly object SyncRoot = new();

        private static bool IsClient => !GameManager.IsDedicatedServer;

        private static Vector3 ToWorld(Vector3i pos) => new(pos.x + 0.5f, pos.y + 0.5f, pos.z + 0.5f);

        public static bool IsWorldPlaying(Vector3i position)
        {
            if (!IsClient)
            {
                return false;
            }

            lock (SyncRoot)
            {
                if (!ActiveHandles.TryGetValue(position, out var handle))
                {
                    return false;
                }

                try
                {
                    return handle.IsPlaying();
                }
                catch (NullReferenceException)
                {
                    ActiveHandles.Remove(position);
                    return false;
                }
            }
        }

        public static void PlayAt(Vector3i position)
        {
            Debug.Log("playing music at position" + position.ToString());
            if (!IsClient)
            {
                return;
            }

            lock (SyncRoot)
            {
                StopInternal(position);
                var worldPos = ToWorld(position);
                var handle = Manager.Play(worldPos, SoundName, -1, true);
                ActiveHandles[position] = handle;
            }
        }

        public static void StopAt(Vector3i position)
        {
            if (!IsClient)
            {
                Debug.Log("dont stop as its client");
                return;
            }

            lock (SyncRoot)
            {
                StopInternal(position);
            }
        }

        public static void StopAll()
        {
            if (!IsClient)
            {
                return;
            }

            lock (SyncRoot)
            {
                foreach (var kvp in ActiveHandles)
                {
                    kvp.Value.Stop(-1);
                    Manager.Stop(ToWorld(kvp.Key), SoundName);
                }

                ActiveHandles.Clear();
            }
        }

        private static void StopInternal(Vector3i position)
        {
            Debug.Log("stopping music at position " + position.ToString());
            if (ActiveHandles.TryGetValue(position, out var handle))
            {
                ActiveHandles.Remove(position);
                StopHandle(handle);

                Manager.Stop(ToWorld(position), SoundName);
            }
            else
            {
                Debug.Log("no active handles by position to stop");
            }
        }

        private static void StopHandle(Handle handle)
        {
            if (handle == null)
            {
                return;
            }

            try
            {
                StopAudioSource(handle.nearSource);
                StopAudioSource(handle.farSource);
            }
            catch (MissingReferenceException)
            {
                // source already destroyed
            }
            catch (NullReferenceException)
            {
                // source already destroyed
            }
        }

        private static void StopAudioSource(AudioSource source)
        {
            if (source == null)
            {
                return;
            }

            source.Stop();
            var gameObject = source.gameObject;
            if (gameObject != null)
            {
                UnityEngine.Object.Destroy(gameObject);
            }
        }
    }
}
