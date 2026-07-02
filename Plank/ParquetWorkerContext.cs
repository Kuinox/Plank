namespace Plank;

public readonly record struct ParquetWorkerContext(int WorkerIndex, int WorkerCount, string Name);
