using BenchmarkDotNet.Attributes;
using Vfps.PseudonymGenerators;

namespace Vfps.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class PseudonymGeneratorBenchmarks
{
    private readonly CryptoRandomBase64UrlEncodedGenerator crbase64Generator = new();
    private readonly HexEncodedSha256HashGenerator sha256HashGenerator = new();
    private readonly Uuid4Generator uuid4Generator = new();

    [Benchmark]
    public string CryptoRandomBase64UrlEncodedGenerator() =>
        crbase64Generator.GeneratePseudonym("test", 64);

    [Benchmark]
    public string HexEncodedSha256HashGenerator() =>
        sha256HashGenerator.GeneratePseudonym("test", 64);

    [Benchmark]
    public string Uuid4HashGenerator() => uuid4Generator.GeneratePseudonym("test", 36);
}
