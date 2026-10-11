using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Arena.Core.DependencyInjection;
using UnityEditor;
using UnityEngine;

namespace Arena.EditorTools
{
    public static class DependencyInjectionVerification
    {
        [MenuItem("Tools/Verification/Dependency Injection")]
        public static void RunBatch()
        {
            Action[] cases = { VerifySingleton, VerifyLazyDependencies, VerifyNullArguments,
                VerifyDuplicates, VerifyNullResult, VerifyFactoryRetry, VerifyCircularDependency,
                VerifyConcurrentAccess, VerifyClear };
            foreach (var test in cases)
            {
                test();
                Debug.Log($"[DependencyInjectionVerification] CASE_PASS {test.Method.Name}");
            }
            Debug.Log($"[DependencyInjectionVerification] PASS cases={cases.Length}");
        }

        private static void VerifySingleton()
        {
            var container = new DIContainer();
            Require(!container.IsRegistered<IProbe>(), "unregistered type reported as registered");
            Throws<InvalidOperationException>(() => container.Resolve<IProbe>(), "Service not registered");
            var instance = new Probe();
            container.RegisterSingleton<IProbe, Probe>(instance);
            Require(container.IsRegistered<IProbe>(), "singleton registration missing");
            Require(ReferenceEquals(instance, container.Resolve<IProbe>()), "singleton replaced");
        }

        private static void VerifyLazyDependencies()
        {
            var container = new DIContainer();
            int calls = 0;
            container.RegisterLazySingleton<Dependency>(_ => new Dependency());
            container.RegisterLazySingleton<IProbe>(c => { calls++; return new Probe(c.Resolve<Dependency>()); });
            Require(calls == 0 && container.IsRegistered<IProbe>(), "factory ran during registration");
            var first = (Probe)container.Resolve<IProbe>();
            Require(ReferenceEquals(first, container.Resolve<IProbe>()) && calls == 1, "lazy instance not cached");
            Require(ReferenceEquals(first.Dependency, container.Resolve<Dependency>()), "nested resolve failed");
        }

        private static void VerifyNullArguments()
        {
            var container = new DIContainer();
            Throws<ArgumentNullException>(() => container.RegisterSingleton<IProbe, Probe>(null));
            Throws<ArgumentNullException>(() => container.RegisterLazySingleton<IProbe>(null));
            Require(!container.IsRegistered<IProbe>(), "invalid registration changed container");
        }

        private static void VerifyDuplicates()
        {
            foreach (bool lazyFirst in new[] { false, true })
            foreach (bool lazySecond in new[] { false, true })
            foreach (bool resolveFirst in new[] { false, true })
            {
                var container = new DIContainer();
                var original = new Probe();
                if (lazyFirst) container.RegisterLazySingleton<IProbe>(_ => original);
                else container.RegisterSingleton<IProbe, Probe>(original);
                if (resolveFirst) container.Resolve<IProbe>();
                Throws<InvalidOperationException>(() =>
                {
                    if (lazySecond) container.RegisterLazySingleton<IProbe>(_ => new Probe());
                    else container.RegisterSingleton<IProbe, Probe>(new Probe());
                }, "Service already registered");
                Require(ReferenceEquals(original, container.Resolve<IProbe>()), "duplicate replaced original registration");
            }
        }

        private static void VerifyNullResult()
        {
            var container = new DIContainer();
            int calls = 0;
            container.RegisterLazySingleton<IProbe>(_ => ++calls == 1 ? null : new Probe());
            Throws<InvalidOperationException>(() => container.Resolve<IProbe>(), "Service factory returned null");
            var value = container.Resolve<IProbe>();
            Require(value != null && calls == 2, "null result cached or resolve guard not released");
            Require(ReferenceEquals(value, container.Resolve<IProbe>()) && calls == 2, "retry result not cached");
        }

        private static void VerifyFactoryRetry()
        {
            var container = new DIContainer();
            int calls = 0;
            container.RegisterLazySingleton<IProbe>(_ =>
            {
                if (++calls == 1) throw new ArgumentException("factory failure");
                return new Probe();
            });
            Throws<ArgumentException>(() => container.Resolve<IProbe>(), "factory failure");
            Require(container.Resolve<IProbe>() != null && calls == 2, "failed factory cannot retry");
        }

        private static void VerifyCircularDependency()
        {
            var container = new DIContainer();
            container.RegisterLazySingleton<IProbe>(c => new Probe(c.Resolve<Dependency>()));
            container.RegisterLazySingleton<Dependency>(c => { c.Resolve<IProbe>(); return new Dependency(); });
            Throws<InvalidOperationException>(() => container.Resolve<IProbe>(), "Circular dependency detected");
            Throws<InvalidOperationException>(() => container.Resolve<Dependency>(), "Circular dependency detected");
        }

        private static void VerifyConcurrentAccess()
        {
            var container = new DIContainer();
            int calls = 0;
            container.RegisterLazySingleton<IProbe>(_ => { Interlocked.Increment(ref calls); return new Probe(); });
            var values = new IProbe[64];
            var tasks = Enumerable.Range(0, values.Length).Select(i => Task.Run(() =>
            {
                Require(container.IsRegistered<IProbe>(), "concurrent lookup lost registration");
                values[i] = container.Resolve<IProbe>();
            })).ToArray();
            Require(Task.WaitAll(tasks, 5000), "concurrent resolve timed out");
            Require(calls == 1 && values.All(value => ReferenceEquals(value, values[0])), "factory ran more than once");

            Parallel.For(0, 128, i =>
            {
                if (i % 3 == 0) container.Clear();
                else if (i % 3 == 1)
                {
                    try { container.RegisterSingleton<IProbe, Probe>(new Probe()); }
                    catch (InvalidOperationException ex) when (ex.Message.StartsWith("Service already registered:")) { }
                }
                else
                {
                    container.IsRegistered<IProbe>();
                    try { Require(container.Resolve<IProbe>() != null, "concurrent resolve returned null"); }
                    catch (InvalidOperationException ex) when (ex.Message.StartsWith("Service not registered:")) { }
                }
            });
        }

        private static void VerifyClear()
        {
            var container = new DIContainer();
            container.RegisterSingleton<IProbe, Probe>(new Probe());
            container.RegisterLazySingleton<Dependency>(_ => new Dependency());
            container.Clear();
            Require(!container.IsRegistered<IProbe>() && !container.IsRegistered<Dependency>(), "clear left registrations");
            Throws<InvalidOperationException>(() => container.Resolve<IProbe>(), "Service not registered");
            container.RegisterLazySingleton<IProbe>(_ => new Probe());
            Require(container.Resolve<IProbe>() != null, "registration after clear failed");
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static void Throws<T>(Action action, string message = null) where T : Exception
        {
            try { action(); }
            catch (T ex)
            {
                Require(message == null || ex.Message.Contains(message), $"Unexpected error: {ex.Message}");
                return;
            }
            throw new InvalidOperationException($"Expected {typeof(T).Name}");
        }

        private interface IProbe { }
        private sealed class Dependency { }
        private sealed class Probe : IProbe
        {
            public readonly Dependency Dependency;
            public Probe(Dependency dependency = null) { Dependency = dependency; }
        }
    }
}
