using BenchmarkDotNet.Attributes;
using Vfps.PseudonymGenerators;

namespace Vfps.Benchmarks;

[MemoryDiagnoser]
public class PseudonymGeneratorBenchmarks
{
    private readonly CryptoRandomBase64UrlEncodedGenerator crbase64Generator = new();
    private readonly HexEncodedSha256HashGenerator sha256HashGenerator = new();

    [Benchmark]
    public string CryptoRandomBase64UrlEncodedGenerator() => crbase64Generator.GeneratePseudonym("test", 16);

    [Benchmark]
    public string HexEncodedSha256HashGenerator() => sha256HashGenerator.GeneratePseudonym("test", 64);
}
