using UnityEngine;
using Arena.Network;
using Arena.Logging;
using Arena.Core.Utilities;

namespace Arena.Core.DependencyInjection
{
    public class GameInstaller : MonoBehaviour
    {
        [SerializeField] private NetworkConfig networkConfig;
        
        public static DIContainer Container { get; private set; }
        private DIContainer _ownedContainer;

        private void Awake()
        {
            if (Container != null)
            {
                Debug.LogWarning("[GameInstaller] Container already initialized!");
                Destroy(gameObject);
                return;
            }
            
            Container = new DIContainer();
            _ownedContainer = Container;
            
            RegisterCoreServices();
            RegisterLoggingServices();
            RegisterNetworkServices();
            
            DontDestroyOnLoad(gameObject);
            
            Debug.Log("[GameInstaller] Initialization complete");
        }

        private void RegisterCoreServices()
        {
            var dispatcher = UnityMainThreadDispatcher.Instance;
            Container.RegisterSingleton<UnityMainThreadDispatcher, UnityMainThreadDispatcher>(dispatcher);
    
            if (networkConfig == null)
            {
                Debug.LogError("[GameInstaller] NetworkConfig not assigned!");
                networkConfig = ScriptableObject.CreateInstance<NetworkConfig>();
            }
            Container.RegisterSingleton<NetworkConfig, NetworkConfig>(networkConfig);
    
            Debug.Log("[GameInstaller] Core services registered");
        }

        private void RegisterLoggingServices()
        {
            Container.RegisterLazySingleton<IGameLogger>(c => new UnityLogger());
            
            Debug.Log("[GameInstaller] Logging services registered");
        }

        private void RegisterNetworkServices()
        {
            if (networkConfig == null)
            {
                Debug.LogError("[GameInstaller] NetworkConfig not assigned!");
                networkConfig = ScriptableObject.CreateInstance<NetworkConfig>();
            }
            
            var logger = Container.Resolve<IGameLogger>();

            Container.RegisterLazySingleton<IPacketProcessor>(c => new PacketProcessor(logger));
            Container.RegisterLazySingleton<IConnectionManager>(c => new ConnectionManager(networkConfig, logger));
            Container.RegisterLazySingleton<IMessageRouter>(c => new MessageRouter(logger));

            var networkService = gameObject.AddComponent<NetworkService>();
            networkService.Initialize(networkConfig, logger);
            Container.RegisterSingleton<INetworkService, NetworkService>(networkService);
            
            Debug.Log("[GameInstaller] Network services registered");
        }

        private void OnDestroy()
        {
            if (_ownedContainer == null || !ReferenceEquals(Container, _ownedContainer))
                return;

            if (Container != null)
            {
                if (Container.IsRegistered<INetworkService>())
                {
                    var networkService = Container.Resolve<INetworkService>();
                    networkService?.Disconnect();
                }
                
                Container.Clear();
                Container = null;
                _ownedContainer = null;
            }
            
            Debug.Log("[GameInstaller] Shutdown complete");
        }
    }
}
