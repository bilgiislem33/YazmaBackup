using System.Runtime.CompilerServices;
using YazmaBackup.Application;

namespace YazmaBackup.Infrastructure;

public sealed class FixedSizeChunker(int chunkSizeBytes = 4 * 1024 * 1024) : IChunker
{
    private readonly int _chunkSizeBytes = chunkSizeBytes > 0 ? chunkSizeBytes : throw new ArgumentOutOfRangeException(nameof(chunkSizeBytes));

    public string ChunkerId => $"fixed-v1:{_chunkSizeBytes}";

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
        Stream source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            var buffer = GC.AllocateUninitializedArray<byte>(_chunkSizeBytes);
            var readTotal = 0;
            while (readTotal < buffer.Length)
            {
                var read = await source.ReadAsync(buffer.AsMemory(readTotal), cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                readTotal += read;
            }

            if (readTotal == 0) yield break;
            yield return buffer.AsMemory(0, readTotal);
            if (readTotal < buffer.Length) yield break;
        }
    }
}
