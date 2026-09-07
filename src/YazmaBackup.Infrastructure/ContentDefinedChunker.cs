using System.Numerics;
using System.Runtime.CompilerServices;
using YazmaBackup.Application;

namespace YazmaBackup.Infrastructure;

public sealed class ContentDefinedChunker : IChunker
{
    private readonly int _minSize;
    private readonly int _averageSize;
    private readonly int _maxSize;
    private readonly ulong _mask;
    private static readonly ulong[] Gear = BuildGearTable();

    public ContentDefinedChunker(int minSize = 1 * 1024 * 1024, int averageSize = 4 * 1024 * 1024, int maxSize = 8 * 1024 * 1024)
    {
        if (minSize <= 0 || averageSize <= minSize || maxSize <= averageSize)
            throw new ArgumentOutOfRangeException(nameof(averageSize), "Chunk sizes must satisfy 0 < min < average < max.");
        if (!BitOperations.IsPow2((uint)averageSize))
            throw new ArgumentException("Average chunk size must be a power of two.", nameof(averageSize));

        _minSize = minSize;
        _averageSize = averageSize;
        _maxSize = maxSize;
        _mask = (ulong)averageSize - 1UL;
    }

    public string ChunkerId => $"gear-cdc-v1:{_minSize}:{_averageSize}:{_maxSize}";

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var readBuffer = GC.AllocateUninitializedArray<byte>(256 * 1024);
        using var chunk = new MemoryStream(_averageSize);
        ulong rolling = 0;

        while (true)
        {
            var read = await source.ReadAsync(readBuffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;

            var segmentStart = 0;
            for (var index = 0; index < read; index++)
            {
                rolling = (rolling << 1) + Gear[readBuffer[index]];
                var prospectiveLength = checked((int)chunk.Length + (index - segmentStart + 1));
                var boundary = prospectiveLength >= _maxSize ||
                    (prospectiveLength >= _minSize && (rolling & _mask) == 0);
                if (!boundary) continue;

                chunk.Write(readBuffer, segmentStart, index - segmentStart + 1);
                yield return chunk.ToArray();
                chunk.SetLength(0);
                rolling = 0;
                segmentStart = index + 1;
            }

            if (segmentStart < read)
                chunk.Write(readBuffer, segmentStart, read - segmentStart);
        }

        if (chunk.Length > 0)
            yield return chunk.ToArray();

    }

    private static ulong[] BuildGearTable()
    {
        var table = new ulong[256];
        ulong state = 0x9E3779B97F4A7C15UL;
        for (var i = 0; i < table.Length; i++)
        {
            state += 0x9E3779B97F4A7C15UL;
            var z = state;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            table[i] = z ^ (z >> 31);
        }
        return table;
    }
}
