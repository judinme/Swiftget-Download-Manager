using BenchmarkDotNet.Attributes;
using SwiftGet.Engine.Torrent;

namespace SwiftGet.Benchmarks;

[MemoryDiagnoser]
public class MagnetParserBenchmark
{
    private const string StandardHexV1Uri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=sample.iso";
    private const string Base32V1Uri = "magnet:?xt=urn:btih:4JMQ4K52W2SJSWLLZ3ZZKYQZSCXNRBYJ&dn=sample.iso";
    private const string ComplexMultiTrackerUri = "magnet:?xt=urn:btih:da39a3ee5e6b4b0d3255bfef95601890afd80709&dn=ubuntu-24.04-desktop-amd64.iso&tr=http%3A%2F%2Ftorrent.ubuntu.com%3A6969%2Fannounce&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce&ws=https%3A%2F%2Freleases.ubuntu.com%2F24.04%2Fubuntu-24.04-desktop-amd64.iso&so=0,1,3-5";
    private const string MultihashV2Uri = "magnet:?xt=urn:btmh:1220cafebabe1234567890abcdef1234567890abcdef1234567890abcdef12345678&dn=sample_v2.iso";

    [Benchmark(Baseline = true)]
    public ParsedMagnetUri ParseStandardHex()
    {
        return MagnetUriParser.Parse(StandardHexV1Uri);
    }

    [Benchmark]
    public ParsedMagnetUri ParseBase32()
    {
        return MagnetUriParser.Parse(Base32V1Uri);
    }

    [Benchmark]
    public ParsedMagnetUri ParseComplexMultiTracker()
    {
        return MagnetUriParser.Parse(ComplexMultiTrackerUri);
    }

    [Benchmark]
    public ParsedMagnetUri ParseMultihashV2()
    {
        return MagnetUriParser.Parse(MultihashV2Uri);
    }
}
