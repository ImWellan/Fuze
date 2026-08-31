using System.IO;
using System.Text;

namespace FusePlayer.Services;

/// <summary>
/// Lit uniquement Segment/Tracks/TrackEntry dans un fichier Matroska.
/// Cette lecture ciblée rend LanguageIETF (BCP 47) disponible sans MKVToolNix.
/// </summary>
internal static class MatroskaLanguageReader
{
    private const ulong SegmentId = 0x18538067;
    private const ulong TracksId = 0x1654AE6B;
    private const ulong TrackEntryId = 0xAE;
    private const ulong LanguageIetfId = 0x22B59D;
    private const long MaximumScanBytes = 64L * 1024 * 1024;

    public static async Task<Dictionary<int, string>> ReadAsync(string mediaPath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(mediaPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var scanEnd = Math.Min(stream.Length, MaximumScanBytes);
        while (stream.Position < scanEnd &&
               await TryReadElementHeaderAsync(stream, cancellationToken) is { } header)
        {
            var elementEnd = ResolveEnd(stream, header.Size, scanEnd);
            if (header.Id == SegmentId)
            {
                var result = await ReadSegmentAsync(stream, elementEnd, cancellationToken);
                if (result.Count > 0)
                    return result;
            }
            stream.Position = elementEnd;
        }
        return [];
    }

    private static async Task<Dictionary<int, string>> ReadSegmentAsync(FileStream stream,
        long segmentEnd, CancellationToken cancellationToken)
    {
        var scanEnd = Math.Min(segmentEnd, MaximumScanBytes);
        while (stream.Position < scanEnd &&
               await TryReadElementHeaderAsync(stream, cancellationToken) is { } header)
        {
            var elementEnd = ResolveEnd(stream, header.Size, scanEnd);
            if (header.Id == TracksId)
                return await ReadTracksAsync(stream, elementEnd, cancellationToken);
            stream.Position = elementEnd;
        }
        return [];
    }

    private static async Task<Dictionary<int, string>> ReadTracksAsync(FileStream stream,
        long tracksEnd, CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, string>();
        var streamIndex = 0;
        while (stream.Position < tracksEnd &&
               await TryReadElementHeaderAsync(stream, cancellationToken) is { } header)
        {
            var elementEnd = ResolveEnd(stream, header.Size, tracksEnd);
            if (header.Id == TrackEntryId)
            {
                var language = await ReadTrackEntryAsync(stream, elementEnd, cancellationToken);
                // FFprobe numérote les flux selon l'ordre des TrackEntry, pas selon
                // TrackNumber, qui peut être non séquentiel dans un Matroska valide.
                if (!string.IsNullOrWhiteSpace(language))
                    result[streamIndex] = language;
                streamIndex++;
            }
            stream.Position = elementEnd;
        }
        return result;
    }

    private static async Task<string?> ReadTrackEntryAsync(
        FileStream stream, long entryEnd, CancellationToken cancellationToken)
    {
        string? regionalLanguage = null;

        while (stream.Position < entryEnd &&
               await TryReadElementHeaderAsync(stream, cancellationToken) is { } header)
        {
            var elementEnd = ResolveEnd(stream, header.Size, entryEnd);
            var contentLength = elementEnd - stream.Position;
            if (header.Id == LanguageIetfId && contentLength is > 0 and <= 64)
                regionalLanguage = await ReadStringAsync(stream, (int)contentLength,
                    cancellationToken);
            stream.Position = elementEnd;
        }

        // Le code ISO 639 historique est déjà fourni par FFprobe. Ici, ne
        // retourner que LanguageIETF afin de ne jamais masquer un repli VFF/VFQ.
        return regionalLanguage;
    }

    private static async ValueTask<ElementHeader?> TryReadElementHeaderAsync(FileStream stream,
        CancellationToken cancellationToken)
    {
        var id = await ReadVintAsync(stream, true, cancellationToken);
        if (id is null)
            return null;
        var size = await ReadVintAsync(stream, false, cancellationToken);
        if (size is null)
            return null;
        return new ElementHeader(id.Value.Value, size.Value.IsUnknown ? null : size.Value.Value);
    }

    private static async ValueTask<Vint?> ReadVintAsync(FileStream stream, bool keepMarker,
        CancellationToken cancellationToken)
    {
        var firstBuffer = new byte[1];
        if (await stream.ReadAsync(firstBuffer, cancellationToken) != 1)
            return null;
        var first = firstBuffer[0];
        if (first == 0)
            throw new InvalidDataException("VINT Matroska invalide.");

        var mask = 0x80;
        var length = 1;
        while ((first & mask) == 0)
        {
            mask >>= 1;
            length++;
            if (length > 8)
                throw new InvalidDataException("VINT Matroska trop long.");
        }

        ulong value = keepMarker ? first : (byte)(first & (mask - 1));
        var rest = new byte[length - 1];
        if (rest.Length > 0)
            await stream.ReadExactlyAsync(rest, cancellationToken);
        foreach (var next in rest)
            value = (value << 8) | next;

        var unknownValue = !keepMarker && value == ((1UL << (7 * length)) - 1);
        return new Vint(value, unknownValue);
    }

    private static async Task<string> ReadStringAsync(FileStream stream, int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
        return Encoding.UTF8.GetString(buffer).TrimEnd('\0').Trim();
    }

    private static long ResolveEnd(FileStream stream, ulong? size, long parentEnd)
    {
        if (size is null)
            return parentEnd;
        if (size > (ulong)Math.Max(0, parentEnd - stream.Position))
            throw new InvalidDataException("Taille d'élément Matroska invalide.");
        return stream.Position + (long)size.Value;
    }

    private readonly record struct ElementHeader(ulong Id, ulong? Size);
    private readonly record struct Vint(ulong Value, bool IsUnknown);
}
