// SPDX-License-Identifier: AGPL-3.0-or-later
// SPDX-FileCopyrightText: 2026 guccidonut and Project KONGOR V Contributors
// Project KONGOR V (https://github.com/guccidonut/PKV)

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

namespace Kongor.Core.Services.Events;

/// <summary>
///     High-performance zero-allocation compiled expression delegate cache for domain event dispatch.
///     Eliminates runtime <see cref="MethodInfo.Invoke(object, object[])" /> overhead and parameter array allocations.
/// </summary>
internal static class DomainEventHandlerInvokerCache
{
    private static readonly ConcurrentDictionary<Type, Func<object, IDomainEvent, CancellationToken, Task>> s_invokers =
        new();

    /// <summary>
    ///     Gets the count of currently compiled invoker delegates in cache.
    /// </summary>
    public static int CachedCount => s_invokers.Count;

    /// <summary>
    ///     Gets or compiles a high-speed invocation delegate for the specified domain event type.
    /// </summary>
    /// <param name="eventType">The runtime <see cref="Type" /> of the <see cref="IDomainEvent" />.</param>
    /// <returns>A compiled delegate that invokes <see cref="IEventHandler{TEvent}.HandleAsync" /> directly.</returns>
    public static Func<object, IDomainEvent, CancellationToken, Task> GetInvoker(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);

        return s_invokers.GetOrAdd(eventType, static type => CompileInvoker(type));
    }

    /// <summary>
    ///     Clears the compiled invoker cache (primarily for unit tests and dynamic reloading).
    /// </summary>
    public static void Clear()
    {
        s_invokers.Clear();
    }

    private static Func<object, IDomainEvent, CancellationToken, Task> CompileInvoker(Type eventType)
    {
        Type handlerInterfaceType = typeof(IEventHandler<>).MakeGenericType(eventType);
        MethodInfo handleMethod = handlerInterfaceType.GetMethod(
                                      nameof(IEventHandler<IDomainEvent>.HandleAsync),
                                      [eventType, typeof(CancellationToken)])
                                  ?? throw new InvalidOperationException(
                                      $"HandleAsync method not found on {handlerInterfaceType.FullName} for event {eventType.FullName}");

        ParameterExpression handlerParam = Expression.Parameter(typeof(object), "handler");
        ParameterExpression eventParam = Expression.Parameter(typeof(IDomainEvent), "evt");
        ParameterExpression ctParam = Expression.Parameter(typeof(CancellationToken), "ct");

        UnaryExpression castHandler = Expression.Convert(handlerParam, handlerInterfaceType);
        UnaryExpression castEvent = Expression.Convert(eventParam, eventType);

        MethodCallExpression callExpr = Expression.Call(castHandler, handleMethod, castEvent, ctParam);

        Expression<Func<object, IDomainEvent, CancellationToken, Task>> lambda =
            Expression.Lambda<Func<object, IDomainEvent, CancellationToken, Task>>(
                callExpr,
                handlerParam,
                eventParam,
                ctParam);

        return lambda.Compile();
    }
}
