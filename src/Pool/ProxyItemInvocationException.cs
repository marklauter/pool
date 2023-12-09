using System.Runtime.Serialization;

namespace Pool;

public sealed class ProxyItemInvocationException
    : Exception
{
    public ProxyItemInvocationException()
    {
    }

    public ProxyItemInvocationException(string? message) : base(message)
    {
    }

    public ProxyItemInvocationException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }

    public ProxyItemInvocationException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
