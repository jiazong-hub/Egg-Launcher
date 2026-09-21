using System.Text;

namespace Launcher.Models.Scanning;

/// <summary>
/// Reads the model-declared context length from the bounded GGUF metadata header.
/// Tensor data is never read or loaded.
/// </summary>
public static class GgufContextMetadataReader
{
    private const uint GgufMagic = 0x46554747;
    private const int MaximumMetadataKeyBytes = 16 * 1024;
    private const int MaximumArchitectureBytes = 1024;
    private const ulong MaximumMetadataEntries = 1_000_000;
    private const ulong MaximumArrayElements = 10_000_000;

    public static int? ReadContextLength(
        string modelPath,
        CancellationToken cancellationToken = default) =>
        ReadMetadata(modelPath, cancellationToken).ContextLength;

    public static GgufModelMetadata ReadMetadata(
        string modelPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        using var stream = new FileStream(
            Path.GetFullPath(modelPath),
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != GgufMagic)
        {
            throw new InvalidDataException("模型不是有效的 GGUF 文件。");
        }

        var version = reader.ReadUInt32();
        if (version is not (2 or 3))
        {
            throw new InvalidDataException($"暂不支持读取 GGUF v{version} 元数据。");
        }

        _ = reader.ReadUInt64(); // tensor count
        var metadataCount = reader.ReadUInt64();
        if (metadataCount > MaximumMetadataEntries)
        {
            throw new InvalidDataException("GGUF 元数据条目数异常。");
        }

        string? architecture = null;
        var integerMetadata = new Dictionary<string, int>(StringComparer.Ordinal);
        var booleanMetadata = new Dictionary<string, bool>(StringComparer.Ordinal);

        for (ulong index = 0; index < metadataCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = ReadString(reader, MaximumMetadataKeyBytes, "GGUF 元数据键");
            var valueType = ReadValueType(reader);

            if (string.Equals(key, "general.architecture", StringComparison.Ordinal)
                && valueType == GgufValueType.String)
            {
                architecture = ReadString(reader, MaximumArchitectureBytes, "GGUF 模型架构");
            }
            else if ((key.EndsWith(".context_length", StringComparison.Ordinal)
                      || key.EndsWith(".expert_count", StringComparison.Ordinal)
                      || key.EndsWith(".expert_used_count", StringComparison.Ordinal)
                      || key.EndsWith(".nextn_predict_layers", StringComparison.Ordinal))
                     && IsIntegerValueType(valueType))
            {
                if (TryReadNonNegativeInt32(reader, valueType, out var integerValue))
                {
                    integerMetadata[key] = integerValue;
                }
            }
            else if ((string.Equals(key, "clip.has_vision_encoder", StringComparison.Ordinal)
                      || key.EndsWith(".has_vision_encoder", StringComparison.Ordinal))
                     && valueType == GgufValueType.Bool)
            {
                booleanMetadata[key] = reader.ReadByte() != 0;
            }
            else
            {
                SkipValue(reader, valueType, cancellationToken);
            }

        }

        var contextLength = ReadArchitectureValue(integerMetadata, architecture, ".context_length", positiveOnly: true);
        var expertCount = ReadArchitectureValue(integerMetadata, architecture, ".expert_count", positiveOnly: false);
        var expertUsedCount = ReadArchitectureValue(integerMetadata, architecture, ".expert_used_count", positiveOnly: false);
        var nextNPredictLayers = ReadArchitectureValue(
            integerMetadata,
            architecture,
            ".nextn_predict_layers",
            positiveOnly: true);
        return new GgufModelMetadata(
            architecture,
            contextLength,
            expertCount,
            expertUsedCount,
            nextNPredictLayers,
            booleanMetadata.Values.Any(value => value));
    }

    private static int? ReadArchitectureValue(
        IReadOnlyDictionary<string, int> metadata,
        string? architecture,
        string suffix,
        bool positiveOnly)
    {
        if (!string.IsNullOrWhiteSpace(architecture)
            && metadata.TryGetValue(architecture + suffix, out var exact)
            && (!positiveOnly || exact > 0))
        {
            return exact;
        }

        var matches = metadata
            .Where(pair => pair.Key.EndsWith(suffix, StringComparison.Ordinal)
                           && (!positiveOnly || pair.Value > 0))
            .Select(pair => pair.Value)
            .Distinct()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool TryReadNonNegativeInt32(
        BinaryReader reader,
        GgufValueType valueType,
        out int value)
    {
        long rawValue;
        switch (valueType)
        {
            case GgufValueType.UInt32:
                rawValue = reader.ReadUInt32();
                break;
            case GgufValueType.Int32:
                rawValue = reader.ReadInt32();
                break;
            case GgufValueType.UInt64:
                var unsignedValue = reader.ReadUInt64();
                if (unsignedValue > int.MaxValue)
                {
                    value = 0;
                    return false;
                }

                rawValue = (long)unsignedValue;
                break;
            case GgufValueType.Int64:
                rawValue = reader.ReadInt64();
                break;
            default:
                value = 0;
                return false;
        }

        if (rawValue is < 0 or > int.MaxValue)
        {
            value = 0;
            return false;
        }

        value = (int)rawValue;
        return true;
    }

    private static bool IsIntegerValueType(GgufValueType valueType) =>
        valueType is GgufValueType.UInt32
            or GgufValueType.Int32
            or GgufValueType.UInt64
            or GgufValueType.Int64;

    private static GgufValueType ReadValueType(BinaryReader reader)
    {
        var raw = reader.ReadUInt32();
        if (!Enum.IsDefined(typeof(GgufValueType), raw))
        {
            throw new InvalidDataException($"GGUF 元数据类型无效：{raw}。");
        }

        return (GgufValueType)raw;
    }

    private static string ReadString(BinaryReader reader, int maximumBytes, string fieldName)
    {
        var byteCount = reader.ReadUInt64();
        if (byteCount > (ulong)maximumBytes)
        {
            throw new InvalidDataException($"{fieldName}超过允许长度。");
        }

        EnsureAvailable(reader.BaseStream, byteCount);
        var bytes = reader.ReadBytes(checked((int)byteCount));
        if ((ulong)bytes.Length != byteCount)
        {
            throw new EndOfStreamException($"{fieldName}被截断。");
        }

        return Encoding.UTF8.GetString(bytes);
    }

    private static void SkipValue(
        BinaryReader reader,
        GgufValueType valueType,
        CancellationToken cancellationToken)
    {
        if (valueType == GgufValueType.String)
        {
            SkipString(reader);
            return;
        }

        if (valueType == GgufValueType.Array)
        {
            var elementType = ReadValueType(reader);
            if (elementType == GgufValueType.Array)
            {
                throw new InvalidDataException("GGUF 元数据不允许嵌套数组。");
            }

            var elementCount = reader.ReadUInt64();
            if (elementCount > MaximumArrayElements)
            {
                throw new InvalidDataException("GGUF 元数据数组过大。");
            }

            if (elementType == GgufValueType.String)
            {
                for (ulong index = 0; index < elementCount; index++)
                {
                    if ((index & 1023) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    SkipString(reader);
                }

                return;
            }

            SeekForward(reader.BaseStream, checked(elementCount * (ulong)ScalarSize(elementType)));
            return;
        }

        SeekForward(reader.BaseStream, (ulong)ScalarSize(valueType));
    }

    private static void SkipString(BinaryReader reader)
    {
        var byteCount = reader.ReadUInt64();
        SeekForward(reader.BaseStream, byteCount);
    }

    private static int ScalarSize(GgufValueType valueType) => valueType switch
    {
        GgufValueType.UInt8 or GgufValueType.Int8 or GgufValueType.Bool => 1,
        GgufValueType.UInt16 or GgufValueType.Int16 => 2,
        GgufValueType.UInt32 or GgufValueType.Int32 or GgufValueType.Float32 => 4,
        GgufValueType.UInt64 or GgufValueType.Int64 or GgufValueType.Float64 => 8,
        _ => throw new InvalidDataException($"GGUF 标量类型无效：{valueType}。"),
    };

    private static void SeekForward(Stream stream, ulong byteCount)
    {
        EnsureAvailable(stream, byteCount);
        stream.Seek(checked((long)byteCount), SeekOrigin.Current);
    }

    private static void EnsureAvailable(Stream stream, ulong byteCount)
    {
        if (stream.Position < 0 || stream.Position > stream.Length)
        {
            throw new EndOfStreamException("GGUF 元数据读取位置无效。");
        }

        var remaining = checked((ulong)(stream.Length - stream.Position));
        if (byteCount > remaining)
        {
            throw new EndOfStreamException("GGUF 元数据被截断。");
        }
    }

    private enum GgufValueType : uint
    {
        UInt8 = 0,
        Int8 = 1,
        UInt16 = 2,
        Int16 = 3,
        UInt32 = 4,
        Int32 = 5,
        Float32 = 6,
        Bool = 7,
        String = 8,
        Array = 9,
        UInt64 = 10,
        Int64 = 11,
        Float64 = 12,
    }
}
