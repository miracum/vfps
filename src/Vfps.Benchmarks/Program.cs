using BenchmarkDotNet.Running;

BenchmarkRunner.Run(typeof(Benchmarks).Assembly);

class Benchmarks { }
