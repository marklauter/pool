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

    public ProxyItemInvocationException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
