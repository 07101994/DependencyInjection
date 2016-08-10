// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq;
using Microsoft.Extensions.DependencyInjection.Abstractions.V2;

namespace Microsoft.Extensions.DependencyInjection.Ordered
{
    public static class ServiceCollectionOrderedExtensions
    {
        public static IServiceCollection2 AddOrdered<TService>(this IServiceCollection2 services)
            where TService : class
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            AddOrdered(services, typeof(TService));
            return services;
        }

        public static IServiceCollection2 AddOrdered(
            this IServiceCollection2 services,
            Type serviceType)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }
            GetOrderedDescriptor(services, serviceType);
            return services;
        }

        public static IServiceCollection2 AddOrdered<TService, TImplementation>(this IServiceCollection2 services)
            where TService : class
            where TImplementation : class, TService
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            return AddOrdered(services, (ServiceDescriptor2) ServiceDescriptor2.Transient(typeof(TService), typeof(TImplementation)));
        }

        public static IServiceCollection2 AddOrdered<TService>(
            this IServiceCollection2 services,
            TService implementationInstance)
            where TService : class
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (implementationInstance == null)
            {
                throw new ArgumentNullException(nameof(implementationInstance));
            }

            return AddOrdered(services, (ServiceDescriptor2) ServiceDescriptor2.Singleton(typeof(TService), implementationInstance));
        }

        public static IServiceCollection2 AddOrdered(
            this IServiceCollection2 services,
            Type serviceType,
            object implementationInstance)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (serviceType == null)
            {
                throw new ArgumentNullException(nameof(serviceType));
            }

            if (implementationInstance == null)
            {
                throw new ArgumentNullException(nameof(implementationInstance));
            }

            return AddOrdered(services, (ServiceDescriptor2) ServiceDescriptor2.Singleton(serviceType, implementationInstance));
        }

        public static IServiceCollection2 AddOrdered(
            this IServiceCollection2 services,
            Type serviceType,
            Type implementationType)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (serviceType == null)
            {
                throw new ArgumentNullException(nameof(serviceType));
            }

            if (implementationType == null)
            {
                throw new ArgumentNullException(nameof(implementationType));
            }

            return AddOrdered(services, (ServiceDescriptor2) ServiceDescriptor2.Transient(serviceType, implementationType));
        }

        public static IServiceCollection2 AddOrdered(
           this IServiceCollection2 services,
           Type serviceType,
           Func<IServiceProvider, object> implementationFactory)
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (serviceType == null)
            {
                throw new ArgumentNullException(nameof(serviceType));
            }

            if (implementationFactory == null)
            {
                throw new ArgumentNullException(nameof(implementationFactory));
            }

            return AddOrdered(services, (ServiceDescriptor2) ServiceDescriptor2.Transient(serviceType, implementationFactory));
        }

        public static IServiceCollection2 AddOrdered<TService, TImplementation>(
           this IServiceCollection2 services,
           Func<IServiceProvider, TImplementation> implementationFactory)
           where TService : class
           where TImplementation : class, TService
        {
            if (services == null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            if (implementationFactory == null)
            {
                throw new ArgumentNullException(nameof(implementationFactory));
            }

            return AddOrdered(services, (ServiceDescriptor2) ServiceDescriptor2.Transient(typeof(TService), implementationFactory));
        }

        public static IServiceCollection2 AddOrdered(
            this IServiceCollection2 collection,
            ServiceDescriptor2 descriptor)
        {
            if (collection == null)
            {
                throw new ArgumentNullException(nameof(collection));
            }

            if (descriptor == null)
            {
                throw new ArgumentNullException(nameof(descriptor));
            }

            var collectionDescriptor = GetOrderedDescriptor(collection, descriptor.ServiceType);
            collectionDescriptor.Descriptors.Add(descriptor);
            return collection;
        }

        private static OrderedEnumerableServiceDescriptor GetOrderedDescriptor(
            this IServiceCollection2 collection,
            Type serviceType)
        {
            var descriptor = collection
                .OfType<OrderedEnumerableServiceDescriptor>()
                .FirstOrDefault(d => d.ServiceType == serviceType);

            if (descriptor == null)
            {
                descriptor = new OrderedEnumerableServiceDescriptor(serviceType);
                collection.Add(descriptor);
                var containerType = typeof(OrderedEnumerableServiceDescriptorContainer<>).MakeGenericType(serviceType);
                collection.AddSingleton(containerType,
                    Activator.CreateInstance(containerType, descriptor));
                collection.TryAddTransient(
                    typeof(IOrdered<>).MakeGenericType(serviceType),
                    typeof(Ordered<>).MakeGenericType(serviceType));
            }
            return descriptor;
        }
    }
}