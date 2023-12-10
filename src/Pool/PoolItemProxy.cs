using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Pool;

[RequiresDynamicCode("Creating a proxy instance requires generating code at runtime")]
internal class PoolItemProxy<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] T>
    : DispatchProxy
    where T : notnull, IDisposable
{
    private readonly MethodInfo disposeMethodInfo = typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose))!;

    private T item = default!;
    private IPool<T> pool = null!;

    public PoolItemProxy()
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

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);

        if (targetMethod.DeclaringType == typeof(IDisposable))
        {
            if (targetMethod.HasSameMetadataDefinitionAs(disposeMethodInfo))
            {
                pool.Release(item);
                return null;
            }
        }

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
