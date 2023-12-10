namespace Pool.Tests.Fakes;

internal sealed class EchoFactory
    : IFactory<IEcho>
{
    public IEcho Create()
    {
        return new Echo();
    }
}
