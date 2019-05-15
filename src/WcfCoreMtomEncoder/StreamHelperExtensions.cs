﻿using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WcfCoreMtomEncoder
{
    public static class StreamHelperExtensions
    {
        private const int _maxReadBufferSize = 1024 * 4;

        public static Task DrainAsync(this Stream stream, CancellationToken cancellationToken)
        {
            return stream.DrainAsync(ArrayPool<byte>.Shared, null, cancellationToken);
        }

        public static Task DrainAsync(this Stream stream, long? limit, CancellationToken cancellationToken)
        {
            return stream.DrainAsync(ArrayPool<byte>.Shared, limit, cancellationToken);
        }

        public static async Task DrainAsync(this Stream stream, ArrayPool<byte> bytePool, long? limit, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var buffer = bytePool.Rent(_maxReadBufferSize);
            long total = 0;
            try
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                while (read > 0)
                {
                    // Not all streams support cancellation directly.
                    cancellationToken.ThrowIfCancellationRequested();
                    if (limit.HasValue && limit.GetValueOrDefault() - total < read)
                    {
                        throw new InvalidDataException($"The stream exceeded the data limit {limit.GetValueOrDefault()}.");
                    }
                    total += read;
                    read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                }
            }
            finally
            {
                bytePool.Return(buffer);
            }
        }
    }
}
