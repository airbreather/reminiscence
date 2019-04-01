#if SUPPORTS_NATIVE_MEMORY_ARRAY
using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Reminiscence.Arrays
{
    // data format is 8 bytes for little-endian i64 for the length n, then n 12-byte pairs of
    // (i64 offset, i32 length), each of which represents the location (relative to the start of the
    // data block, in bytes) of the data for the string at that index.  the remaining bytes contain
    // the data block.
    public sealed class NativeStringArray : ArrayBase<string>
    {
        private static readonly UTF8Encoding UTF8NoBOM = new UTF8Encoding();

        private readonly NativeMemoryArrayBase<byte> data;

        private readonly long byteOffsetToStringData;

        private bool disposed;

        public NativeStringArray(NativeMemoryArrayBase<byte> data)
            : this(data ?? throw new ArgumentNullException(nameof(data)), validate: true)
        {
        }

        private NativeStringArray(NativeMemoryArrayBase<byte> data, bool validate)
        {
            this.data = data;
            if (validate)
            {
                this.Validate();
            }
        }

        public unsafe override string this[long idx]
        {
            get
            {
                // handle non-negative and out-of-bounds with a single test
                long length = this.Length;
                if (unchecked((ulong)idx >= (ulong)length))
                {
                    ThrowArgumentOutOfRangeExceptionForIndex();
                }

                var ptrsStart = (StringPointer*)(this.data.HeadPointer + sizeof(long));
                var dataStart = (byte*)&ptrsStart[length];
                var stringPointer = Unsafe.ReadUnaligned<StringPointer>(&ptrsStart[idx]);
                return UTF8NoBOM.GetString(&dataStart[stringPointer.ByteOffset], stringPointer.ByteLength);
            }

            // TODO: tack onto the end of the data block, resizing if needed.
            set => throw new NotImplementedException();
        }

        /// <inheritdoc />
        public unsafe override long Length => BinaryPrimitives.ReadInt64LittleEndian(new ReadOnlySpan<byte>(this.data.HeadPointer, sizeof(long)));

        /// <inheritdoc />
        public override bool CanResize => true;

        /// <inheritdoc />
        public override void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.data.Dispose();
            this.disposed = true;
        }

        /// <inheritdoc />
        public override void Resize(long size) => throw new NotImplementedException();

        public unsafe void Repack(NativeMemoryArrayBase<byte> scratch)
        {
            scratch.Resize(this.data.Length);
            scratch.CopyFrom(this.data);
            try
            {
                long length = this.Length;
                var src = new NativeStringArray(this.data, false);
                var srcPointersStart = (StringPointer*)(src.data.HeadPointer + sizeof(long));
                var srcStringDataStart = (byte*)&srcPointersStart[length];
                var tgtPointersStart = (StringPointer*)(this.data.HeadPointer + sizeof(long));
                var tgtStringDataStart = (byte*)&tgtPointersStart[length];
                var nextStringDataStart = tgtStringDataStart;
                for (long i = 0; i < length; i++)
                {
                    var srcPointer = srcPointersStart[i];
                    var srcStringData = new ReadOnlySpan<byte>(&srcStringDataStart[srcPointer.ByteOffset], srcPointer.ByteLength);
                    var tgtStringData = new Span<byte>(nextStringDataStart, srcPointer.ByteLength);
                    srcStringData.CopyTo(tgtStringData);
                    tgtPointersStart[i].ByteOffset = nextStringDataStart - tgtStringDataStart;
                    nextStringDataStart += srcPointer.ByteLength;
                }

                this.data.Resize(nextStringDataStart - this.data.HeadPointer);
            }
            catch
            {
                this.data.Resize(scratch.Length);
                this.data.CopyFrom(scratch);
            }
        }

        private unsafe void Validate()
        {
            if (!this.data.CanResize)
            {
                throw new NotSupportedException("Even a native string array with a fixed length still needs the underlying block to be resizable, to ensure that we can replace the string at a particular index with a longer one.");
            }

            if (this.data.Length == 0)
            {
                return;
            }

            if (this.data.Length < sizeof(long))
            {
                throw new ArgumentException();
            }

            var head = this.data.HeadPointer;
            if (this.Length < 0)
            {
                throw new ArgumentException();
            }

            var ptrsStart = (StringPointer*)(head + sizeof(long));
            var ptrsEnd = ptrsStart + this.Length;
            if (this.Length < (ptrsEnd - ptrsStart))
            {
                throw new ArgumentException();
            }

            long endOfStringData = 0;
            for (StringPointer* cur = ptrsStart; cur < ptrsEnd; cur++)
            {
                if (cur->ByteOffset < 0 || cur->ByteLength < 0)
                {
                    throw new ArgumentException();
                }

                long thisStringDataEnd = cur->ByteOffset + cur->ByteLength;
                if (thisStringDataEnd > endOfStringData)
                {
                    endOfStringData = thisStringDataEnd;
                }
            }

            var dataStart = (byte*)ptrsEnd;
            var dataEnd = dataStart + endOfStringData;
            if (this.data.Length < (dataEnd - head))
            {
                throw new ArgumentException();
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowArgumentOutOfRangeExceptionForIndex() => throw new ArgumentOutOfRangeException("idx", "Must be non-negative and less than the size of the array.");

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct StringPointer
        {
            public long ByteOffset;

            public int ByteLength;
        }
    }
}
#endif
