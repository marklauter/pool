namespace Pool.Tests.Fakes;

internal sealed class Echo
    : IEcho
{
    public bool IsReady { get; private set; }

    public Task MakeReadyAsync(CancellationToken cancellationToken)
    {
        IsReady = true;
        return Task.CompletedTask;
    }

    public string Shout(string message)
    {
        return message;
    }
}
