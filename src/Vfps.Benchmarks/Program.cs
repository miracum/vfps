using BenchmarkDotNet.Running;

var _ = BenchmarkRunner.Run(typeof(Benchmarks).Assembly);

class Benchmarks { }
