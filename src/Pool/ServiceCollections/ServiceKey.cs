#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Pool;
#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>
/// Creates service keys.
/// </summary>
public static class ServiceKey
{
    /// <summary>
    /// Creates a service key from the given name.
    /// </summary>
    /// <param name="name"></param>
    /// <returns>a service key of the form "{name}.{typeof(TPoolItem).Name}.Pool" </returns>
    public static string Create<TPoolItem>(string name)
        where TPoolItem : class
        => $"{name}.{Pool<TPoolItem>.PoolName}";
}
