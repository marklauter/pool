namespace Pool;

public interface IFactory<T>
    where T : notnull
{
    T Create();
}
