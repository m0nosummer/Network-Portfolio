using System;
using System.Collections.Concurrent;
using UnityEngine;
using Arena.Core.DependencyInjection;

namespace Arena.Core.Utilities
{
    public class UnityMainThreadDispatcher : MonoBehaviour
    {
        private static UnityMainThreadDispatcher _instance;
        private static readonly ConcurrentQueue<Action> _executionQueue = new();
        private static readonly object _lock = new object();
        
        public static UnityMainThreadDispatcher Instance
        {
            get
            {
                if (_instance != null)
                    return _instance;

                lock (_lock)
                {
                    if (_instance != null)
                        return _instance;

                    // Try to get from DIContainer first
                    var container = GameInstaller.Container;
                    if (container != null && container.IsRegistered<UnityMainThreadDispatcher>())
                    {
                        _instance = container.Resolve<UnityMainThreadDispatcher>();
                        Debug.Log("[MainThreadDispatcher] Resolved from DIContainer");
                        return _instance;
                    }

                    // Fallback
                    Debug.LogWarning("[MainThreadDispatcher] Creating instance before DIContainer ready - this is OK during startup");
                    var go = new GameObject("MainThreadDispatcher");
                    _instance = go.AddComponent<UnityMainThreadDispatcher>();
                    DontDestroyOnLoad(go);
                    
                    return _instance;
                }
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Debug.LogWarning("[MainThreadDispatcher] Duplicate instance detected - destroying");
                Destroy(gameObject);
                return;
            }
            
            _instance = this;
            DontDestroyOnLoad(gameObject);
            Debug.Log("[MainThreadDispatcher] Initialized via Awake");
        }

        private void Update()
        {
            int processedCount = 0;
            const int maxActionsPerFrame = 100;
            
            while (processedCount < maxActionsPerFrame && _executionQueue.TryDequeue(out var action))
            {
                try
                {
                    action?.Invoke();
                    processedCount++;
                }
                catch (Exception ex)
                {
                    Debug.LogException(ex);
                }
            }

            if (processedCount >= maxActionsPerFrame)
            {
                Debug.LogWarning($"[MainThreadDispatcher] Hit action limit! {_executionQueue.Count} actions still queued.");
            }
            else if (processedCount > 50)
            {
                Debug.LogWarning($"[MainThreadDispatcher] Processed {processedCount} actions in one frame - consider optimizing");
            }
        }

        public static void Enqueue(Action action)
        {
            if (action == null)
            {
                Debug.LogWarning("[MainThreadDispatcher] Attempted to enqueue null action");
                return;
            }
            
            _executionQueue.Enqueue(action);
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                Debug.Log("[MainThreadDispatcher] Destroyed");
                _instance = null;
            }
        }
    }
}
