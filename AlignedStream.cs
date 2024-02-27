using System;
using System.IO;

namespace XboxWinFsp
{
    // Kinda hacky stream that will only read the basestream from the given alignment
    // (eg. only reads in 512-byte blocks, at 512-byte offsets)
    //
    // This should be transparent to the user of the stream, eg. allowing a 3-byte read from any offset
    // Reading seems to work pretty well right now, hopefully support for writing can be added later
    public class AlignedStream : Stream
    {
        Stream Stream;
        int Alignment = 0x200;

        public AlignedStream(Stream stream, int alignment)
        {
            Stream = stream;
            Alignment = alignment;
            Position = stream.Position;
        }

        public override bool CanRead { get { return true; } }
        public override bool CanWrite { get { return true; } }
        public override bool CanSeek { get { return true; } }

        byte[] AlignmentBuffer;
        long AlignmentPosition = -1;
        private long position;
        bool FillBuffer = false;

        public override long Position
        {
            get
            {
                return position;
            }
            set
            {
                position = value;
                long alignedPosition = (position / Alignment) * Alignment;

                FillBuffer = false;
                if (alignedPosition != AlignmentPosition)
                {
                    Stream.Position = alignedPosition;
                    FillBuffer = true;
                }
            }
        }

        int BufferCurrentOffset
        {
            get
            {
                return (int)(Position % Alignment);
            }
        }

        int BufferBytesRemaining
        {
            get
            {
                return Alignment - BufferCurrentOffset;
            }
        }

        public override long Length => Stream.Length;

        public override void SetLength(long value)
        {
            Stream.SetLength(value);
        }

        public override void Flush()
        {
            Stream.Flush();
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            if (origin == SeekOrigin.Begin)
                Position = offset;
            else if (origin == SeekOrigin.Current)
                Position = Position + offset;
            else if (origin == SeekOrigin.End)
                Position = Length - offset;

            return Position;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (AlignmentBuffer == null)
                AlignmentBuffer = new byte[Alignment];

            if (FillBuffer)
            {
                AlignmentPosition = Stream.Position;
                Stream.Read(AlignmentBuffer, 0, Alignment);
                FillBuffer = false;
            }

            int bufferOffset = BufferCurrentOffset;

            // Do we have the data requested in the buffer?
            if (count <= BufferBytesRemaining)
            {
                Array.Copy(AlignmentBuffer, bufferOffset, buffer, offset, count);

                // Did we just finish reading all of the buffer?
                if (BufferCurrentOffset == 0)
                    FillBuffer = true; // Make it read a new buffer next turn

                Position += count;
            }
            else
            {
                // We don't have the data here 
                // Is the data bigger than our alignment?
                if (count > Alignment)
                {
                    // It is - read in each alignment block
                    int numBlocks = ((count + Alignment - 1) / Alignment);
                    for (int i = 0; i < numBlocks; i++)
                    {
                        int read = (i + 1) >= numBlocks ? (count % Alignment) : Alignment;
                        if (read == 0)
                            read = Alignment;
                        Read(buffer, offset + (i * Alignment), read);
                    }
                }
                else
                {
                    // It isn't - must mean the data spans over two alignment buffers
                    // Read in the remainder of this alignment
                    int remainder = BufferBytesRemaining;
                    Read(buffer, offset, remainder);
                    // Then read in anything afterward:
                    Read(buffer, offset + remainder, count - remainder);
                }
            }

            return count;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotImplementedException();
        }
    }
}
