
namespace Pool.Tests.Fakes;

internal sealed class EchoFactory
    : IItemFactory<IEcho>
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP005:Return type should indicate that the value should be disposed", Justification = "items created by the factory are disposed by the pool or the factory")]
    public IEcho CreateItem() => new Echo();
}
