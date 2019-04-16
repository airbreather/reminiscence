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

                var ptr = this.pointers[idx];
                fixed (char* c = value)
                {
                    int neededByteLength = UTF8Encoding_NoBOM_ThrowOnInvalid.GetByteCount(c, value.Length);
                    if (neededByteLength > ptr.ByteLength)
                    {
                        throw new NotImplementedException("still need to write the code to find a free block in the data array.");
                    }
                    else
                    {
                        byte* dataStart = &this.data.HeadPointer[ptr.ByteOffset];
                        ptr.ByteLength = UTF8Encoding_NoBOM_ThrowOnInvalid.GetBytes(c, value.Length, dataStart, ptr.ByteLength);
                        this.pointers[idx] = ptr;
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

        public unsafe void Repack(NativeMemoryArrayBase<byte> scratch)
        {
            scratch.Resize(this.data.Length);
            scratch.CopyFrom(this.data);
            try
            {
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

                this.data.Resize(tgtPtr - this.data.HeadPointer);
            }
            catch
            {
                this.data.Resize(scratch.Length);
                this.data.CopyFrom(scratch);
                throw;
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
                using (var fileStream = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()), FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, 4096, FileOptions.DeleteOnClose))
                using (var scratch = new NativeMemoryMappedArray<byte>(fileStream))
                {
                    this.Repack(scratch);
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
            for (long i = 0; i < this.pointers.Length; i++)
            {
                var ptr = this.pointers[i];
                if (ptr.ByteOffset + ptr.ByteLength > this.data.Length)
                {
                    throw new ArgumentException($"Data for the string at index {i} is out-of-bounds (data is in range [{ptr.ByteOffset}, {ptr.ByteOffset + ptr.ByteLength}), but data length is only {data.Length} byte(s) long).");
                }

                // simply counting the chars is enough to engage the built-in validation logic
                UTF8Encoding_NoBOM_ThrowOnInvalid.GetCharCount(&this.data.HeadPointer[ptr.ByteOffset], ptr.ByteLength);
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
