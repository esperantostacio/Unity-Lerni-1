using System;
using System.Collections.Generic;
using UnityEngine;

namespace MedicalExam
{
    /// <summary>
    /// Dispatches actions from background threads (e.g. Android Java callbacks)
    /// onto Unity's main thread. Attach this to a persistent GameObject in your scene,
    /// or it will auto-create itself as a singleton.
    /// </summary>
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher instance;
        private static readonly Queue<Action> actionQueue = new Queue<Action>();
        private static readonly object queueLock = new object();

        private void Awake()
        {
            if (instance != null && instance != this)
            {
                Destroy(gameObject);
                return;
            }
            instance = this;
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            lock (queueLock)
            {
                while (actionQueue.Count > 0)
                {
                    var action = actionQueue.Dequeue();
                    try
                    {
                        action?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Debug.LogError($"[MainThreadDispatcher] Exception: {ex}");
                    }
                }
            }
        }

        /// <summary>
        /// Queue an action to run on Unity's main thread next frame.
        /// Safe to call from any thread (Android Java callbacks, etc.).
        /// </summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;

            // Auto-create if needed (must happen on main thread; 
            // if called from background thread before instance exists, just queue it)
            lock (queueLock)
            {
                actionQueue.Enqueue(action);
            }

            // Ensure instance exists (lazy singleton on main thread)
            EnsureInstance();
        }

        private static void EnsureInstance()
        {
            // Can only create GameObjects on main thread; 
            // if we're on a background thread, the queued action will still
            // execute once the Update loop picks it up (assuming instance exists).
            if (instance != null) return;

            try
            {
                var go = new GameObject("[UnityMainThreadDispatcher]");
                instance = go.AddComponent<UnityMainThreadDispatcher>();
                DontDestroyOnLoad(go);
            }
            catch
            {
                // Called from background thread — can't create GO here.
                // The queue will be processed once a dispatcher is manually added to the scene.
            }
        }
    }
}
