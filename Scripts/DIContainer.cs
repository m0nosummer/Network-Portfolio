using System;
using System.Collections.Generic;
using UnityEngine;

namespace Arena.Core.DependencyInjection
{
    public class DIContainer
    {
        private readonly Dictionary<Type, object> _singletons = new();
        private readonly Dictionary<Type, Func<DIContainer, object>> _lazySingletonFactories = new();
        private readonly HashSet<Type> _resolving = new();
        private readonly object _lock = new object();

        public void RegisterSingleton<TInterface, TImplementation>(TImplementation instance) 
            where TImplementation : class, TInterface
        {
            if (instance == null)
                throw new ArgumentNullException(nameof(instance));

            var interfaceType = typeof(TInterface);
            
            lock (_lock)
            {
                if (_singletons.ContainsKey(interfaceType) || _lazySingletonFactories.ContainsKey(interfaceType))
                    throw new InvalidOperationException($"Service already registered: {interfaceType.Name}");
                
                _singletons[interfaceType] = instance;
            }
            
            Debug.Log($"[DIContainer] Registered singleton: {interfaceType.Name}");
        }

        public void RegisterLazySingleton<TInterface>(Func<DIContainer, TInterface> factory) where TInterface : class
        {
            if (factory == null)
                throw new ArgumentNullException(nameof(factory));

            var interfaceType = typeof(TInterface);
            
            lock (_lock)
            {
                if (_singletons.ContainsKey(interfaceType) || _lazySingletonFactories.ContainsKey(interfaceType))
                    throw new InvalidOperationException($"Service already registered: {interfaceType.Name}");

                _lazySingletonFactories[interfaceType] = container => factory(container);
            }
            
            Debug.Log($"[DIContainer] Registered lazy singleton: {interfaceType.Name}");
        }

        public T Resolve<T>() where T : class
        {
            var type = typeof(T);
            
            lock (_lock)
            {
                if (_singletons.TryGetValue(type, out var singleton))
                {
                    return singleton as T;
                }

                // Check circular dependencies
                if (_resolving.Contains(type))
                {
                    throw new InvalidOperationException($"Circular dependency detected: {type.Name}");
                }

                // factory
                if (_lazySingletonFactories.TryGetValue(type, out var factory))
                {
                    _resolving.Add(type);
                    try
                    {
                        var instance = factory(this) as T;
                        if (instance == null)
                            throw new InvalidOperationException($"Service factory returned null: {type.Name}");

                        _singletons[type] = instance; // Cache as singleton
                        Debug.Log($"[DIContainer] Created lazy singleton: {type.Name}");
                        return instance;
                    }
                    finally
                    {
                        _resolving.Remove(type);
                    }
                }

                throw new InvalidOperationException($"Service not registered: {type.Name}");
            }
        }

        public bool IsRegistered<T>() where T : class
        {
            var type = typeof(T);
            
            lock (_lock)
            {
                return _singletons.ContainsKey(type) || _lazySingletonFactories.ContainsKey(type);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                _singletons.Clear();
                _lazySingletonFactories.Clear();
                _resolving.Clear();
            }
            
            Debug.Log("[DIContainer] All services cleared");
        }
    }
}
