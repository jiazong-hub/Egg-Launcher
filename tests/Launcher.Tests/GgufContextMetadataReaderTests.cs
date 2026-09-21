using System.Text;
using Launcher.Models.Scanning;
using Launcher.Models.Profiles;

namespace Launcher.Tests;

public sealed class GgufContextMetadataReaderTests
{
    [Fact]
    public void ReadContextLength_ReadsArchitectureSpecificMetadata()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "model.gguf");
            WriteGguf(
                path,
                ("general.architecture", GgufValueType.String, "qwen2"),
                ("qwen2.context_length", GgufValueType.UInt32, 131_072u));

            Assert.Equal(131_072, GgufContextMetadataReader.ReadContextLength(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadContextLength_HandlesContextBeforeArchitecture()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "model.gguf");
            WriteGguf(
                path,
                ("qwen2.context_length", GgufValueType.UInt64, 98_304ul),
                ("general.architecture", GgufValueType.String, "qwen2"));

            Assert.Equal(98_304, GgufContextMetadataReader.ReadContextLength(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadContextLength_ReturnsNullWhenMetadataIsAbsent()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "model.gguf");
            WriteGguf(path, ("general.architecture", GgufValueType.String, "qwen2"));

            Assert.Null(GgufContextMetadataReader.ReadContextLength(path));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadMetadata_DetectsMoeFromExpertCount()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "moe.gguf");
            WriteGguf(
                path,
                ("general.architecture", GgufValueType.String, "qwen3moe"),
                ("qwen3moe.expert_count", GgufValueType.UInt32, 128u),
                ("qwen3moe.expert_used_count", GgufValueType.UInt32, 8u));

            var metadata = GgufContextMetadataReader.ReadMetadata(path);

            Assert.Equal(ModelType.MoE, metadata.DetectModelType());
            Assert.Equal(128, metadata.ExpertCount);
            Assert.Equal(8, metadata.ExpertUsedCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadMetadata_DetectsDenseWhenArchitectureHasNoExperts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "dense.gguf");
            WriteGguf(path, ("general.architecture", GgufValueType.String, "qwen2"));

            Assert.Equal(ModelType.Dense, GgufContextMetadataReader.ReadMetadata(path).DetectModelType());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadMetadata_DetectsEmbeddedMtpFromNextNPredictLayers()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "mtp.gguf");
            WriteGguf(
                path,
                ("general.architecture", GgufValueType.String, "qwen35"),
                ("qwen35.nextn_predict_layers", GgufValueType.UInt32, 3u));

            var metadata = GgufContextMetadataReader.ReadMetadata(path);

            Assert.True(metadata.HasEmbeddedMtp);
            Assert.Equal(3, metadata.NextNPredictLayers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ReadMetadata_DetectsVisionEncoderFlag()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "mmproj.gguf");
            WriteGguf(
                path,
                ("general.architecture", GgufValueType.String, "clip"),
                ("clip.has_vision_encoder", GgufValueType.Bool, true));

            Assert.True(GgufContextMetadataReader.ReadMetadata(path).HasVisionEncoder);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteGguf(
        string path,
        params (string Key, GgufValueType Type, object Value)[] metadata)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(0x46554747u);
        writer.Write(3u);
        writer.Write(0ul);
        writer.Write((ulong)metadata.Length);
        foreach (var entry in metadata)
        {
            WriteString(writer, entry.Key);
            writer.Write((uint)entry.Type);
            switch (entry.Type)
            {
                case GgufValueType.UInt32:
                    writer.Write((uint)entry.Value);
                    break;
                case GgufValueType.UInt64:
                    writer.Write((ulong)entry.Value);
                    break;
                case GgufValueType.String:
                    WriteString(writer, (string)entry.Value);
                    break;
                case GgufValueType.Bool:
                    writer.Write((byte)((bool)entry.Value ? 1 : 0));
                    break;
                default:
                    throw new InvalidOperationException("Unsupported test metadata type.");
            }
        }
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        writer.Write((ulong)bytes.Length);
        writer.Write(bytes);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"launcher-gguf-context-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private enum GgufValueType : uint
    {
        UInt32 = 4,
        Bool = 7,
        String = 8,
        UInt64 = 10,
    }
}
