#if SUPPORTS_NATIVE_MEMORY_ARRAY
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Reminiscence.Arrays
{
    public sealed class NativeStringArray : ArrayBase<string>
    {
        private static readonly UTF8Encoding UTF8Encoding_NoBOM_ThrowOnInvalid = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

        private readonly NativeMemoryArrayBase<StringPointer> pointers;

        private readonly NativeMemoryArrayBase<byte> data;

        private bool disposed;

        private bool repackWouldMakeChanges;

        public NativeStringArray(NativeMemoryArrayBase<StringPointer> pointers, NativeMemoryArrayBase<byte> data)
            : this(pointers ?? throw new ArgumentNullException(nameof(pointers)),
                   data ?? throw new ArgumentNullException(nameof(data)),
                   validate: true)
        {
        }

        private unsafe NativeStringArray(NativeMemoryArrayBase<StringPointer> pointers, NativeMemoryArrayBase<byte> data, bool validate)
        {
            this.pointers = pointers;
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
                if (unchecked((ulong)idx >= (ulong)this.Length))
                {
                    ThrowArgumentOutOfRangeExceptionForIndex();
                }

                var ptr = this.pointers[idx];
                byte* data = &this.data.HeadPointer[ptr.ByteOffset];
                int charCount = UTF8Encoding_NoBOM_ThrowOnInvalid.GetCharCount(data, ptr.ByteLength);
                if (charCount == 0)
                {
                    return string.Empty;
                }

                string str = new string('\0', charCount);
                fixed (char* c = str)
                {
                    UTF8Encoding_NoBOM_ThrowOnInvalid.GetChars(data, ptr.ByteLength, c, charCount);
                }

                return str;
            }

            set
            {
                if (value is null)
                {
                    value = string.Empty;
                }

                // handle non-negative and out-of-bounds with a single test
                if (unchecked((ulong)idx >= (ulong)this.Length))
                {
                    ThrowArgumentOutOfRangeExceptionForIndex();
                }

                int neededByteLength;
                var ptr = this.pointers[idx];
                fixed (char* c = value)
                {
                    byte* dataStart = &this.data.HeadPointer[ptr.ByteOffset];
                    neededByteLength = UTF8Encoding_NoBOM_ThrowOnInvalid.GetByteCount(c, value.Length);

                    // easiest case: encoded replacement is the same length as what we're replacing.
                    if (neededByteLength == ptr.ByteLength)
                    {
                        UTF8Encoding_NoBOM_ThrowOnInvalid.GetBytes(c, value.Length, dataStart, neededByteLength);
                        return;
                    }

                    // almost as easy: encoded replacement is shorter than what we're replacing.
                    if (neededByteLength < ptr.ByteLength)
                    {
                        this.repackWouldMakeChanges = true;
                        ptr.ByteLength = UTF8Encoding_NoBOM_ThrowOnInvalid.GetBytes(c, value.Length, dataStart, ptr.ByteLength);
                        this.pointers[idx] = ptr;
                        return;
                    }

                    // unpin it for now... I don't really want the string to be pinned for the whole
                    // time we scan all the pointers, let alone the entire time we call Repack().
                }

                // hardest case by far: encoded replacement is longer than what we're replacing.
                long dataEnd = 0;
                long dataEndIdx = -1;
                bool dataEndIsOverlapped = false;
                for (long i = 0; i < this.pointers.Length; i++)
                {
                    var ptr2 = this.pointers[i];
                    if (dataEnd <= ptr2.ByteOffset + ptr2.ByteLength)
                    {
                        dataEndIsOverlapped = dataEnd > ptr2.ByteOffset;
                        dataEnd = ptr2.ByteOffset + ptr2.ByteLength;
                        dataEndIdx = i;
                    }
                }

                // if we happen to be setting the data for the string whose data is already at the
                // very end, then we can safely reuse its old space as long as it's not something
                // that's already been optimized to reuse runs of string data.
                if (dataEndIdx == idx && !dataEndIsOverlapped)
                {
                    dataEnd -= ptr.ByteLength;
                }

                if (dataEnd + neededByteLength > this.data.Length)
                {
                    if (this.repackWouldMakeChanges)
                    {
                        dataEnd = this.Repack();
                    }

                    this.data.EnsureMinimumSize(dataEnd + neededByteLength);
                }

                ptr.ByteOffset = dataEnd;

                fixed (char* c = value)
                {
                    UTF8Encoding_NoBOM_ThrowOnInvalid.GetBytes(c, value.Length, this.data.HeadPointer + ptr.ByteOffset, neededByteLength);
                }

                this.pointers[idx] = ptr;

                if (!this.repackWouldMakeChanges && idx > 0)
                {
                    var prevPtr = this.pointers[idx - 1];
                    if (prevPtr.ByteOffset + prevPtr.ByteLength != ptr.ByteOffset)
                    {
                        this.repackWouldMakeChanges = true;
                    }
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowArgumentOutOfRangeExceptionForIndex() => throw new ArgumentOutOfRangeException("idx", "Must be non-negative and less than the size of the array.");

        /// <inheritdoc />
        public override long Length => this.pointers.Length;

        /// <inheritdoc />
        public override bool CanResize => true;

        public bool RepackBeforeSaving { get; set; }

        /// <inheritdoc />
        public override void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            this.pointers.Dispose();
            this.data.Dispose();
            this.disposed = true;
        }

        /// <inheritdoc />
        public override void Resize(long size) => this.pointers.Resize(size);

        // this method rewrites the string data so each string's data is located immediately after
        // the data for the string before it (and, of course, rewrites the pointers accordingly).
        // this will get rid of all fragmentation in the string data, but it's also possible that
        // we will actually increase the size of the data, in cases where the data is optimized such
        // that, e.g., "abcde" and "cdefg" point to sections of string data "abcdefg".  Such cases
        // will be handled *correctly*, but without such optimizations.
        public unsafe long Repack()
        {
            long neededDataLength = 0;
            for (long i = 0; i < this.pointers.Length; i++)
            {
                neededDataLength += this.pointers[i].ByteLength;
            }

            NativeMemoryMappedArray<byte> scratch;

            var fileStream = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.DeleteOnClose);
            try
            {
                fileStream.SetLength(this.data.Length);
                scratch = new NativeMemoryMappedArray<byte>(fileStream, this.data.Length);
            }
            catch
            {
                fileStream.Dispose();
                throw;
            }

            try
            {
                scratch.CopyFrom(this.data);

                this.data.EnsureMinimumSize(neededDataLength);

                byte* tgtPtr = this.data.HeadPointer;
                for (long i = 0, cnt = this.Length; i < cnt; i++)
                {
                    var ptr = this.pointers[i];
                    var src = new ReadOnlySpan<byte>(scratch.HeadPointer + ptr.ByteOffset, ptr.ByteLength);
                    var tgt = new Span<byte>(tgtPtr, ptr.ByteLength);
                    src.CopyTo(tgt);

                    ptr.ByteOffset = tgtPtr - this.data.HeadPointer;
                    this.pointers[i] = ptr;

                    tgtPtr += ptr.ByteLength;
                }

                this.repackWouldMakeChanges = false;
                return tgtPtr - this.data.HeadPointer;
            }
            catch
            {
                this.data.CopyFrom(scratch);
                throw;
            }
            finally
            {
                scratch.Dispose();
            }
        }

        /// <inheritdoc />
        public override long CopyTo(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            if (this.RepackBeforeSaving)
            {
                long newDataEnd = this.Repack();
                if (newDataEnd != this.data.Length)
                {
                    this.data.Resize(newDataEnd);
                }
            }

            long result = 0;

            result += this.pointers.CopyTo(stream);

            byte[] dataLengthBytes = BitConverter.GetBytes(this.data.Length);
            stream.Write(dataLengthBytes, 0, sizeof(long));
            result += sizeof(long);

            result += this.data.CopyTo(stream);

            return result;
        }

        /// <inheritdoc />
        public override void CopyFrom(Stream stream)
        {
            if (stream == null)
            {
                throw new ArgumentNullException(nameof(stream));
            }

            this.pointers.CopyFrom(stream);

            byte[] dataLengthBytes = new byte[sizeof(long)];
            int off = 0;
            while (off < dataLengthBytes.Length)
            {
                int cur = stream.Read(dataLengthBytes, off, dataLengthBytes.Length - off);
                if (cur == 0)
                {
                    throw new EndOfStreamException();
                }

                off += cur;
            }

            this.data.Resize(BitConverter.ToInt64(dataLengthBytes, 0));
            this.data.CopyFrom(stream);
            this.Validate();
        }

        private unsafe void Validate()
        {
            var prev = default(StringPointer);
            for (long i = 0; i < this.pointers.Length; i++)
            {
                var ptr = this.pointers[i];
                if (ptr.ByteOffset < 0 || ptr.ByteLength < 0)
                {
                    throw new ArgumentException("Offsets and lengths must be non-negative.", "pointers");
                }

                if (ptr.ByteOffset + ptr.ByteLength > this.data.Length)
                {
                    throw new ArgumentException($"Data for the string at index {i} is out-of-bounds (data is in range [{ptr.ByteOffset}, {ptr.ByteOffset + ptr.ByteLength}), but data length is only {data.Length} byte(s) long).");
                }

                // simply counting the chars is enough to engage the built-in validation logic
                UTF8Encoding_NoBOM_ThrowOnInvalid.GetCharCount(&this.data.HeadPointer[ptr.ByteOffset], ptr.ByteLength);

                // check if this string isn't snugly located immediately after the previous one.
                this.repackWouldMakeChanges = this.repackWouldMakeChanges || ptr.ByteOffset != prev.ByteOffset + prev.ByteLength;
                prev = ptr;
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct StringPointer
        {
            public long ByteOffset;

            public int ByteLength;
        }
    }
}
#endif
