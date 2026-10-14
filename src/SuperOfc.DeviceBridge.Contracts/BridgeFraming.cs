using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SuperOfc.DeviceBridge.Contracts;

/// <summary>Length-prefixed frames: 4-byte little-endian length, then the payload. Shared by both ends of the pipe.</summary>
public static class BridgeFraming
{
    /// <summary>Largest accepted frame (a batch of 500 logs is about 60 KB; templates can be larger).</summary>
    public const int MaxFrameBytes = 16 * 1024 * 1024;

    public static string PipeName(string suffix) => "SuperOfc.DeviceBridge." + suffix;

    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var frame = new byte[4 + payload.Length];
        frame[0] = (byte)payload.Length;
        frame[1] = (byte)(payload.Length >> 8);
        frame[2] = (byte)(payload.Length >> 16);
        frame[3] = (byte)(payload.Length >> 24);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        await stream.WriteAsync(frame, 0, frame.Length, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns null when the other side closed the pipe cleanly.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        if (stream is null)
        {
            throw new ArgumentNullException(nameof(stream));
        }

        var header = new byte[4];
        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = header[0] | (header[1] << 8) | (header[2] << 16) | (header[3] << 24);
        if (length < 0 || length > MaxFrameBytes)
        {
            throw new InvalidDataException("Bridge frame has an invalid length: " + length);
        }

        var payload = new byte[length];
        if (!await FillAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("The bridge pipe closed in the middle of a message.");
        }

        return payload;
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (offset == 0)
                {
                    return false;
                }

                throw new EndOfStreamException("The bridge pipe closed in the middle of a message.");
            }

            offset += read;
        }

        return true;
    }
}
