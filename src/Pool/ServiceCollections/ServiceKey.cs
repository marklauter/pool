#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace Pool;
#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>
/// Encapsulates the concept of a service key.
/// </summary>
/// <typeparam name="TPoolItem"></typeparam>
/// <param name="name"></param>
public sealed class ServiceKey<TPoolItem>(string name)
    where TPoolItem : class
{
    /// <summary>
    /// Contains the service key value.
    /// </summary>
    public string Value => $"{name}.{typeof(TPoolItem).Name}.Pool";

    /// <summary>
    /// Performs an explicit conversion from <see cref="ServiceKey{TPoolItem}"/> to <see cref="String"/>.
    /// </summary>
    /// <param name="serviceKey">The service key to convert.</param>
    public static implicit operator string(ServiceKey<TPoolItem> serviceKey) => serviceKey.Value;

    /// <summary>
    /// Performs an explicit conversion from <see cref="String"/> to <see cref="ServiceKey{TPoolItem}"/>.
    /// </summary>
    /// <param name="value">The string value to convert.</param>
    public static explicit operator ServiceKey<TPoolItem>(string value) => new(value);
}
