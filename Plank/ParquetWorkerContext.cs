namespace Plank;

public sealed class ParquetWorkerContext
{
    internal ParquetWorkerContext(int workerIndex, int workerCount, string name)
    {
        WorkerIndex = workerIndex;
        WorkerCount = workerCount;
        Name = name;
    }

    public int WorkerIndex { get; }

    public int WorkerCount { get; }

    public string Name { get; }
}
