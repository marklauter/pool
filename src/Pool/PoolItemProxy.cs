using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Pool;

[RequiresDynamicCode("Creating a proxy instance requires generating code at runtime")]
internal sealed class PoolItemProxy<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>
    : DispatchProxy
    , IDisposable
    where T : notnull, IDisposable
{
    private T item = default!;
    private IPool<T> pool = null!;

    private PoolItemProxy()
    {

    }

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP001:Dispose created", Justification = "analyzer doesn't know the item under analysis is being returned by the function")]
    internal static T Create(
        T item,
        IPool<T> pool)
    {
        var itemProxy = Create<T, PoolItemProxy<T>>();
        var proxy = itemProxy as PoolItemProxy<T>;

        if (proxy is not null)
        {
            proxy.item = item;
            proxy.pool = pool ?? throw new ArgumentNullException(nameof(pool));
        }

        return itemProxy;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Dispose()
    {
        pool.Release(item);
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        try
        {
            return targetMethod.Invoke(item, args);
        }
        catch (TargetInvocationException ex)
        {
            // todo: this doesn't work with async..
            // need to check method info for awaitable and check if the task is faulted after invocation
            throw ex?.InnerException ?? throw new ProxyItemInvocationException($"failed during invocation of {targetMethod.Name}", ex);
        }
    }
}
