// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection.ServiceLookup;

namespace Microsoft.Extensions.DependencyInjection
{
    /// <summary>
    /// The default IServiceProvider.
    /// </summary>
    public class ServiceProvider : IServiceProvider, IDisposable
    {
        private readonly ServiceProvider _root;
        private readonly ServiceTable _table;
        private bool _disposeCalled;

        private readonly Dictionary<IService, object> _resolvedServices = new Dictionary<IService, object>();
        private List<IDisposable> _transientDisposables;

        private static readonly Func<Type, ServiceProvider, Func<ServiceProvider, object>> _createServiceAccessor = CreateServiceAccessor;


        private static MethodInfo TrasientMethodInfo = GetMethodInfo<Func<ServiceProvider, object, object>>((a, b) => a.Transient(b));
        private static MethodInfo ScopedMethodInfo = GetMethodInfo<Func<ServiceProvider, IService, Func<object>, object>>((sp, key, factory) => sp.Scoped(key, factory));
        private static MethodInfo SingletonMethodInfo = GetMethodInfo<Func<ServiceProvider, IService, Func<object>, object>>((sp, key, factory) => sp.Singleton(key, factory));

        public ServiceProvider(IEnumerable<ServiceDescriptor> serviceDescriptors, IEnumerable<IService> builtServices = null)
        {
            _root = this;
            _table = new ServiceTable(serviceDescriptors, builtServices);

            _table.Add(typeof(IServiceProvider), new ServiceProviderService());
            _table.Add(typeof(IServiceScopeFactory), new ServiceScopeService());
            _table.Add(typeof(IEnumerable<>), new OpenIEnumerableService(_table));
        }

        // This constructor is called exclusively to create a child scope from the parent
        internal ServiceProvider(ServiceProvider parent)
        {
            _root = parent._root;
            _table = parent._table;
        }

        // Reusing _resolvedServices as an implementation detail of the lock
        private object SyncObject => _resolvedServices;

        /// <summary>
        /// Gets the service object of the specified type.
        /// </summary>
        /// <param name="serviceType"></param>
        /// <returns></returns>
        public object GetService(Type serviceType)
        {
            var realizedService = _table.RealizedServices.GetOrAdd(serviceType, _createServiceAccessor, this);
            return realizedService.Invoke(this);
        }

        public Expression GetServiceExpression(Type serviceType)
        {
            var callSite = GetServiceCallSite(serviceType, new HashSet<Type>());

            return GetExpression(callSite);
        }

        private static Func<ServiceProvider, object> CreateServiceAccessor(Type serviceType, ServiceProvider serviceProvider)
        {
            var callSite = serviceProvider.GetServiceCallSite(serviceType, new HashSet<Type>());
            if (callSite != null)
            {
                return RealizeService(serviceProvider._table, serviceType, callSite);
            }

            return _ => null;
        }

        internal static Func<ServiceProvider, object> RealizeService(ServiceTable table, Type serviceType, IServiceCallSite callSite)
        {
            var callCount = 0;
            return provider =>
            {
                if (Interlocked.Increment(ref callCount) == 2)
                {
                    Task.Run(() =>
                    {
                        var lambdaExpression = GetExpression(callSite);

                        table.RealizedServices[serviceType] = lambdaExpression.Compile();
                    });
                }

                return callSite.Invoke(provider);
            };
        }

        private static Expression<Func<ServiceProvider, object>> GetExpression(IServiceCallSite callSite)
        {
            var providerExpression = Expression.Parameter(typeof(ServiceProvider), "provider");

            var lambdaExpression = Expression.Lambda<Func<ServiceProvider, object>>(
                callSite.Build(providerExpression),
                providerExpression);
            return lambdaExpression;
        }

        public IServiceCallSite GetServiceCallSite(Type serviceType, ISet<Type> callSiteChain)
        {
            try
            {
                if (callSiteChain.Contains(serviceType))
                {
                    throw new InvalidOperationException(Resources.FormatCircularDependencyException(serviceType));
                }

                callSiteChain.Add(serviceType);

                ServiceEntry entry;
                if (_table.TryGetEntry(serviceType, out entry))
                {
                    return GetResolveCallSite(entry.Last, callSiteChain);
                }

                object emptyIEnumerableOrNull = GetEmptyIEnumerableOrNull(serviceType);
                if (emptyIEnumerableOrNull != null)
                {
                    return new EmptyIEnumerableCallSite(serviceType, emptyIEnumerableOrNull);
                }

                return null;
            }
            finally
            {
                callSiteChain.Remove(serviceType);
            }

        }

        internal IServiceCallSite GetResolveCallSite(IService service, ISet<Type> callSiteChain)
        {
            IServiceCallSite serviceCallSite = service.CreateCallSite(this, callSiteChain);
            if (service.Lifetime == ServiceLifetime.Transient)
            {
                return new TransientCallSite(serviceCallSite);
            }
            else if (service.Lifetime == ServiceLifetime.Scoped)
            {
                return new ScopedCallSite(service, serviceCallSite);
            }
            else
            {
                return new SingletonCallSite(service, serviceCallSite);
            }
        }

        public void Dispose()
        {
            lock (SyncObject)
            {
                if (_disposeCalled)
                {
                    return;
                }

                _disposeCalled = true;

                if (_transientDisposables != null)
                {
                    foreach (var disposable in _transientDisposables)
                    {
                        disposable.Dispose();
                    }

                    _transientDisposables.Clear();
                }

                // PERF: We've enumerating the dictionary so that we don't allocate to enumerate.
                // .Values allocates a KeyCollection on the heap, enumerating the dictionary allocates
                // a struct enumerator
                foreach (var entry in _resolvedServices)
                {
                    (entry.Value as IDisposable)?.Dispose();
                }

                _resolvedServices.Clear();
            }
        }

        private object Transient(object service)
        {
            if (!object.ReferenceEquals(this, service))
            {
                var disposable = service as IDisposable;
                if (disposable != null)
                {
                    lock (SyncObject)
                    {
                        if (_transientDisposables == null)
                        {
                            _transientDisposables = new List<IDisposable>();
                        }

                        _transientDisposables.Add(disposable);
                    }
                }
            }
            return service;
        }

        private object Singleton(IService key, Func<object> getter)
        {
            return _root.Scoped(key, getter);
        }

        private object Scoped(IService key, Func<object> getter)
        {
            object resolved;
            lock (_resolvedServices)
            {
                if (!_resolvedServices.TryGetValue(key, out resolved))
                {
                    resolved = getter();
                    _resolvedServices.Add(key, resolved);
                }
            }
            return resolved;
        }


        private object GetEmptyIEnumerableOrNull(Type serviceType)
        {
            var typeInfo = serviceType.GetTypeInfo();

            if (typeInfo.IsGenericType &&
                serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                var itemType = typeInfo.GenericTypeArguments[0];
                return Array.CreateInstance(itemType, 0);
            }

            return null;
        }

        private static MethodInfo GetMethodInfo<T>(Expression<T> expr)
        {
            var mc = (MethodCallExpression)expr.Body;
            return mc.Method;
        }

        private class EmptyIEnumerableCallSite : IServiceCallSite
        {
            private readonly object _serviceInstance;
            private readonly Type _serviceType;

            public EmptyIEnumerableCallSite(Type serviceType, object serviceInstance)
            {
                _serviceType = serviceType;
                _serviceInstance = serviceInstance;
            }

            public object Invoke(ServiceProvider provider)
            {
                return _serviceInstance;
            }

            public Expression Build(Expression provider)
            {
                return Expression.Constant(_serviceInstance, _serviceType);
            }
        }

        private class TransientCallSite : IServiceCallSite
        {
            private readonly IServiceCallSite _service;

            public TransientCallSite(IServiceCallSite service)
            {
                _service = service;
            }

            public object Invoke(ServiceProvider provider)
            {
                return provider.Transient(_service.Invoke(provider));
            }

            public Expression Build(Expression provider)
            {
                return Expression.Call(
                    provider,
                    TrasientMethodInfo,
                    _service.Build(provider));
            }
        }

        private class ScopedCallSite : IServiceCallSite
        {
            private readonly IService _key;
            private readonly IServiceCallSite _serviceCallSite;

            public ScopedCallSite(IService key, IServiceCallSite serviceCallSite)
            {
                _key = key;
                _serviceCallSite = serviceCallSite;
            }

            public object Invoke(ServiceProvider provider)
            {
                return provider.Scoped(_key, () => _serviceCallSite.Invoke(provider));
            }

            public Expression Build(Expression providerExpression)
            {
                var keyExpression = Expression.Constant(
                    _key,
                    typeof(IService));

                var factoryExpression = Expression.Lambda(_serviceCallSite.Build(providerExpression));

                return Expression.Call(providerExpression,
                    ScopedMethodInfo,
                    keyExpression,
                    factoryExpression);
            }
        }

        private class SingletonCallSite : IServiceCallSite
        {
            private readonly IService _key;
            private readonly IServiceCallSite _serviceCallSite;

            public SingletonCallSite(IService key, IServiceCallSite serviceCallSite)
            {
                _key = key;
                _serviceCallSite = serviceCallSite;
            }

            public virtual object Invoke(ServiceProvider provider)
            {
                return provider.Singleton(_key, () => _serviceCallSite.Invoke(provider));
            }

            public virtual Expression Build(Expression providerExpression)
            {
                var keyExpression = Expression.Constant(
                    _key,
                    typeof(IService));

                var factoryExpression = Expression.Lambda(_serviceCallSite.Build(providerExpression));

                return Expression.Call(providerExpression,
                    SingletonMethodInfo,
                    keyExpression,
                    factoryExpression);
            }
        }
    }
}
