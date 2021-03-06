using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using TomLonghurst.RedisClient.Exceptions;

namespace TomLonghurst.RedisClient.Extensions
{
    public static class BufferExtensions
    {
        internal static string AsString(this Memory<byte> buffer)
        {
            return buffer.Span.AsString();
        }

        internal static string AsString(this ReadOnlySequence<byte> buffer)
        {
            if (buffer.IsEmpty)
            {
                return null;
            }

            if (buffer.IsSingleSegment)
            {
                return buffer.First.Span.AsString();
            }

            var arr = ArrayPool<byte>.Shared.Rent(checked((int) buffer.Length));
            var span = new Span<byte>(arr, 0, (int) buffer.Length);
            buffer.CopyTo(span);
            var s = span.AsString();
            ArrayPool<byte>.Shared.Return(arr);
            return s;
        }

        internal static string AsString(this Span<byte> span)
        {
            return ((ReadOnlySpan<byte>) span).AsString();
        }

        internal static unsafe string AsString(this ReadOnlySpan<byte> span)
        {
            if (span.IsEmpty)
            {
                return null;
            }

#if  NETCORE
            return Encoding.UTF8.GetString(span);
#endif

            fixed (byte* ptr = span)
            {
                return Encoding.UTF8.GetString(ptr, span.Length);
            }
        }

        internal static async ValueTask<ReadResult> AdvanceToLineTerminator(this PipeReader pipeReader,
            ReadResult readResult)
        {
            if (readResult.IsCompleted || readResult.IsCanceled)
            {
                return readResult;
            }

            var endOfLinePosition = readResult.Buffer.GetEndOfLinePosition();
            if (endOfLinePosition == null)
            {
                var readResultWithEndOfLine = await pipeReader.ReadUntilEndOfLineFound(readResult);
                readResult = readResultWithEndOfLine.ReadResult;
                endOfLinePosition = readResultWithEndOfLine.EndOfLinePosition;
            }

            if (endOfLinePosition == null)
            {
                throw new RedisDataException("Can't find EOL");
            }

            if (readResult.Buffer.Start.GetInteger() != endOfLinePosition.Value.GetInteger())
            {
                pipeReader.AdvanceTo(endOfLinePosition.Value);
            }
            else
            {
                pipeReader.AdvanceTo(readResult.Buffer.Start, readResult.Buffer.End);
            }

            return readResult;
        }

        public class ReadResultWithEndOfLine
        {
            public ReadResult ReadResult { get; }
            public SequencePosition? EndOfLinePosition { get; }

            public ReadResultWithEndOfLine(ReadResult readResult, SequencePosition? endOfLinePosition)
            {
                ReadResult = readResult;
                EndOfLinePosition = endOfLinePosition;
            }
        }

        internal static async ValueTask<ReadResultWithEndOfLine> ReadUntilEndOfLineFound(this PipeReader pipeReader, ReadResult readResult)
        {
            var buffer = readResult.Buffer;

            SequencePosition? endOfLinePosition;
            while ((endOfLinePosition = buffer.GetEndOfLinePosition()) == null)
            {
                // We don't want to consume it yet - So don't advance past the start
                // But do tell it we've examined up until the end - But it's not enough and we need more
                // We need to call advance before calling another read though
                pipeReader.AdvanceTo(buffer.Start, buffer.End);

                if (readResult.IsCompleted || readResult.IsCanceled)
                {
                    break;
                }

                if (!pipeReader.TryRead(out readResult))
                {
                    readResult = await pipeReader.ReadAsync().ConfigureAwait(false);
                }

                buffer = readResult.Buffer;
            }

            return new ReadResultWithEndOfLine(readResult, endOfLinePosition);
        }

        internal static SequencePosition? GetEndOfLinePosition(this ReadOnlySequence<byte> buffer)
        {
            var endOfLine = buffer.PositionOf((byte) '\n');

            if (endOfLine == null)
            {
                return null;
            }
            
            return buffer.GetPosition(1, endOfLine.Value);
        }
        
        internal static ArraySegment<byte> GetArraySegment(this Memory<byte> buffer) => GetArraySegment((ReadOnlyMemory<byte>)buffer);

        internal static ArraySegment<byte> GetArraySegment(this ReadOnlyMemory<byte> buffer)
        {
            if (!MemoryMarshal.TryGetArray<byte>(buffer, out var segment))
            {
                throw new InvalidOperationException("MemoryMarshal.TryGetArray<byte> could not provide an array");
            }
            
            return segment;
        }
    }
}