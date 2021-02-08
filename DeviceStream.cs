using System;
using System.IO;

using Fsp;

namespace XboxWinFsp
{
	public class DeviceStream : Stream
	{
		AlignedStream AlignedStream;
        long DriveSize;

		public DeviceStream(String inputPath)
		{
            var sfh = Natives.CreateFile(inputPath,
                EFileAccess.GenericRead | EFileAccess.GenericAll,
                EFileShare.Read | EFileShare.Write,
                IntPtr.Zero, ECreationDisposition.OpenExisting,
                EFileAttributes.Normal, IntPtr.Zero);

            if (sfh.IsInvalid)
            {
                throw new IOException("Cannot open device.");
            }

            // Get our device size
            uint high = 0;
            uint low = Natives.GetFileSize(sfh, ref high);
            DriveSize = (high << 32) | low;

            if (DriveSize == 0xffffffff)
                DriveSize = Natives.GetDriveSize(sfh);

            // Create a stream to read with
            var stream = new FileStream(sfh, FileAccess.ReadWrite, 0x200 * 0x200, false);
            AlignedStream = new AlignedStream(stream, 0x200 * 0x200);
        }

        public override bool CanRead => AlignedStream.CanRead;

        public override bool CanSeek => AlignedStream.CanSeek;

        public override bool CanWrite => AlignedStream.CanWrite;

        public override long Length => DriveSize;

        public override long Position { get => AlignedStream.Position; set => AlignedStream.Position = value; }

        public override void Flush()
        {
            AlignedStream.Flush();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return AlignedStream.Read(buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            return AlignedStream.Seek(offset, origin);
        }

        public override void SetLength(long value)
        {
            DriveSize = (value >= 0) ? value : 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            AlignedStream.Write(buffer, offset, count);
        }
    }
}


