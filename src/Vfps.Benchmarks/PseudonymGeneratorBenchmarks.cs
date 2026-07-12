using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Vfps.PseudonymGenerators;

namespace Vfps.Benchmarks;

[MemoryDiagnoser]
[JsonExporterAttribute.FullCompressed]
public class PseudonymGeneratorBenchmarks
{
    private readonly CryptoRandomBase64UrlEncodedGenerator crbase64Generator = new();
    private readonly HexEncodedSha256HashGenerator sha256HashGenerator = new();
    private readonly Uuid4Generator uuid4Generator = new();
    private readonly Uuid7Generator uuid7Generator = new();
    private readonly FullRandomHexEncodedGenerator fullRandomHexGenerator = new();
    private readonly FullRandomBase62EncodedGenerator fullRandomBase62Generator = new();

    [Benchmark]
    public string CryptoRandomBase64UrlEncodedGenerator() =>
        crbase64Generator.GeneratePseudonym("test", 64);

    [Benchmark]
    public string HexEncodedSha256HashGenerator() =>
        sha256HashGenerator.GeneratePseudonym("test", 64);

    [Benchmark]
    public string Uuid4Generator() => uuid4Generator.GeneratePseudonym("test", 36);

    [Benchmark]
    public string Uuid7Generator() => uuid7Generator.GeneratePseudonym("test", 36);

    [Benchmark]
    public string FullRandomHexEncodedGenerator() =>
        fullRandomHexGenerator.GeneratePseudonym("test", 64);

    [Benchmark]
    public string FullRandomBase62EncodedGenerator() =>
        fullRandomBase62Generator.GeneratePseudonym("test", 64);
}

public static class Program
{
    public static void Main()
    {
        BenchmarkRunner.Run(typeof(Program).Assembly);
    }
}
