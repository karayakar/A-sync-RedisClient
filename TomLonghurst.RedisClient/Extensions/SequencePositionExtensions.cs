using System;
using System.Buffers;

namespace TomLonghurst.RedisClient.Extensions
{
    public static class SequencePositionExtensions
    {
        internal static bool IsSamePositionOrGreaterThanBufferLength(this SequencePosition sequencePosition,
            ReadOnlySequence<byte> buffer)
        {
            var bufferLengthOfSequencePosition = buffer.Slice(0, sequencePosition).Length;
            var bufferLength = buffer.Length;

            return bufferLengthOfSequencePosition >= bufferLength;
        }
    }
}