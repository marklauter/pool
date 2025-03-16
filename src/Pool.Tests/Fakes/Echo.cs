using System.Runtime.CompilerServices;

namespace Pool.Tests.Fakes;

internal sealed class Echo
    : IEcho
{
    private bool disposed;
    public bool IsDisposed() => disposed;

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
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(disposed, nameof(Echo));
}
