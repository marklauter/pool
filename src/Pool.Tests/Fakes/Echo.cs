using System.Runtime.CompilerServices;

namespace Pool.Tests.Fakes;

internal sealed class Echo
    : IEcho
    , IDisposable
{
    private bool disposed;

    public bool IsConnected { get; private set; }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        IsConnected = true;
        return Task.CompletedTask;
    }

    public string Shout(string message) => message;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void ThrowIfDisposed()
    {
#if NET7_0_OR_GREATER
#pragma warning disable IDE0022 // Use expression body for method
        ObjectDisposedException.ThrowIf(disposed, nameof(Echo));
#pragma warning restore IDE0022 // Use expression body for method
#elif NET6_0_OR_GREATER                                                      
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(Echo));
        }
#endif
    }
}
