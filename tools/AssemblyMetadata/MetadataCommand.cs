using System.Security.Cryptography;
using System.Text.Json;

namespace APIShared.AssemblyMetadata;

internal static class MetadataCommand
{
    // No artifact is opened until the entire selected report fits its byte bound.
    // CreateNew forbids overwriting input, old evidence or an existing output.
    internal static int Run(string[] args)
    {
        InspectionOptions options = InspectionOptions.Parse(args);
        byte[] input;
        using (var file = new FileStream(options.Input, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (file.Length == 0 || file.Length > InspectionOptions.MaxInputBytes)
                throw new InvalidDataException("Input must contain 1..16777216 bytes.");
            input = new byte[checked((int)file.Length)];
            file.ReadExactly(input);
            if (file.ReadByte() != -1) throw new InvalidDataException("Input grew during inspection.");
        }
        string hash = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        if (!StringComparer.Ordinal.Equals(hash, options.Sha256)) throw new InvalidDataException("Input SHA-256 differs from the expected value.");
        MetadataReport report = new MetadataInspector(options).Read(input, hash);
        byte[] serialized;
        using (var buffer = new BoundedJsonStream(options.MaxOutputBytes))
        {
            JsonSerializer.Serialize(buffer, report, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            serialized = buffer.ToArray();
        }
        // A failed write cannot leave a partial report under the requested name.
        // Move without overwrite also preserves a concurrent creator's evidence.
        string temporary = options.Output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool created = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { created = true; output.Write(serialized); }
            File.Move(temporary, options.Output, overwrite: false);
            created = false;
        }
        finally { if (created) File.Delete(temporary); }
        return 0;
    }
}

// JsonSerializer writes incrementally through this stream; it cannot first allocate
// an unbounded complete JSON string. Overflow is a refusal, never a truncated report.
internal sealed class BoundedJsonStream(int maximum) : Stream
{
    private readonly MemoryStream buffer = new();
    internal byte[] ToArray() => buffer.ToArray();
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => buffer.Length;
    public override long Position { get => buffer.Position; set => throw new NotSupportedException(); }
    public override void Flush() => buffer.Flush();
    public override void Write(byte[] bytes, int offset, int count) => Write(bytes.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > maximum - buffer.Length) throw new InvalidDataException("Complete JSON exceeds the requested output bound.");
        buffer.Write(bytes);
    }
    public override int Read(byte[] bytes, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) buffer.Dispose(); base.Dispose(disposing); }
}
