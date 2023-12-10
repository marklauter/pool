using System.Runtime.Serialization;

namespace Pool;

public sealed class NotReadyException
    : Exception
{
    public NotReadyException()
    {
    }

    public NotReadyException(string? message) : base(message)
    {
    }

    public NotReadyException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }

    public NotReadyException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
