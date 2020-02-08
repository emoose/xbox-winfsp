using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Fsp;

namespace XboxWinFsp
{
    public class GdfxFileSystem : ReadOnlyFileSystem
    {
        const int kSectorSize = 0x800;

        const int kXgd2HeaderAddress = 0x10000; // Address of XGD1/XGD2 header (depends on Version field value)
        const int kXgd25HeaderAddress = 0xFDA0000; // Address of XGD2.5 header?
        const int kXgd3HeaderAddress = 0x2090000; // Address of XGD3 header?

        GDF_VOLUME_DESCRIPTOR GdfHeader;

        ulong HeaderAddress = 0;
        ulong BaseAddress = 0;

        object StreamLock = new object();

        public GdfxFileSystem(Stream stream, string inputPath) : base(stream, inputPath, kSectorSize)
        {
        }

        void GdfxInit()
        {
            // First try reading GDF header from some standard addresses:
            int[] standardAddresses = new int[] { kXgd2HeaderAddress, kXgd25HeaderAddress, kXgd3HeaderAddress };
            long headerAddress = -1;
            foreach (var addr in standardAddresses)
            {
                Stream.Position = addr;
                GdfHeader = Stream.ReadStruct<GDF_VOLUME_DESCRIPTOR>();
                if (GdfHeader.IsValid)
                {
                    headerAddress = addr;
                    break;
                }
            }

            // If that failed, try scanning the file for it:
            if (!GdfHeader.IsValid)
            {
                Stream.Position = kXgd2HeaderAddress;
                while (Stream.Length > Stream.Position)
                {
                    long pos = Stream.Position;
                    GdfHeader = Stream.ReadStruct<GDF_VOLUME_DESCRIPTOR>();
                    if (GdfHeader.IsValid)
                    {
                        headerAddress = pos;
                        break;
                    }

                    // Try checking the next sector...
                    Stream.Position = pos + kSectorSize;
                }
            }

            // If that still failed then this probably isn't a GDFX file :(
            if (!GdfHeader.IsValid)
                throw new FileSystemParseException("Failed to find GDFX volume descriptor");

            HeaderAddress = (ulong)headerAddress;
            BaseAddress = HeaderAddress - (kSectorSize * 32);

            RootFiles = GdfReadDirectory(GdfHeader.RootSector, GdfHeader.RootSize, null);
        }

        // Reads GDFX directory entries into a list
        List<IFileEntry> GdfReadDirectory(ulong sectorNum, ulong dirSize, FileEntry parent)
        {
            long curPos = Stream.Position;
            var entries = new List<IFileEntry>();

            Stream.Position = (long)GdfSectorToAddress(sectorNum);
            byte[] padBytes = new byte[4];
            long sectorRemain = 0x800;
            while (dirSize > 0)
            {
                if (sectorRemain > 0)
                    Stream.Read(padBytes, 0, 4);

                if (sectorRemain <= 0 || padBytes[0] == 0xFF && padBytes[1] == 0xFF && padBytes[2] == 0xFF && padBytes[3] == 0xFF)
                {
                    sectorNum++;
                    dirSize -= kSectorSize;
                    Stream.Position = (long)GdfSectorToAddress(sectorNum);
                    sectorRemain = 0x800;
                    continue;
                }

                Stream.Position -= 4;

                long entPos = Stream.Position;
                var entry = new FileEntry(parent, this);
                if (!entry.Read(Stream))
                    break;

                if (entry.IsDirectory)
                    entry.Children = GdfReadDirectory(entry.DirEntry.FirstSector, entry.Size, entry);
                else
                    BytesInUse += entry.DirEntry.FileSize;

                entries.Add(entry);

                long entSize = Stream.Position - entPos;
                sectorRemain -= entSize;
            }

            entries.Sort((x, y) => x.Name.CompareTo(y.Name));

            Stream.Position = curPos;
            return entries;
        }

        ulong GdfSectorToAddress(ulong sector)
        {
            return (sector * kSectorSize) + BaseAddress;
        }

        public override Int32 Init(Object Host0)
        {
            try
            {
                GdfxInit();

                var Host = (FileSystemHost)Host0;
                Host.SectorSize = kSectorSize;
                Host.SectorsPerAllocationUnit = 1;
                Host.MaxComponentLength = 255;
                Host.FileInfoTimeout = 1000;
                Host.CaseSensitiveSearch = false;
                Host.CasePreservedNames = true;
                Host.UnicodeOnDisk = false;
                Host.PersistentAcls = false;
                Host.PassQueryDirectoryPattern = true;
                Host.FlushAndPurgeOnCleanup = true;
                Host.VolumeCreationTime = GdfHeader.TimeStamp;
                Host.VolumeSerialNumber = GdfHeader.RootSector;

                string gdfType = "XGD1";
                if (GdfHeader.Version != 0) // Only Xbox360 disks have Version set?
                {
                    gdfType = "XGD2";
                    if (HeaderAddress == kXgd25HeaderAddress)
                        gdfType = "XGD2.5";
                    else if (HeaderAddress == kXgd3HeaderAddress)
                        gdfType = "XGD3";
                }
                Host.FileSystemName = $"GDFX ({gdfType})";

                return base.Init(Host0);
            }
            catch (FileSystemParseException)
            {
                return STATUS_OPEN_FAILED;
            }
        }

        // Info about a file stored inside the GDFX image
        protected class FileEntry : IFileEntry
        {
            GdfxFileSystem FileSystem;
            internal GDF_DIRECTORY_ENTRY DirEntry;
            string fileName;

            public long DirEntryAddr;

            public string Name
            {
                get
                {
                    return fileName;
                }
                set { throw new NotImplementedException(); }
            }

            public ulong Size
            {
                get
                {
                    return DirEntry.FileSize;
                }
                set { throw new NotImplementedException(); }
            }

            public bool IsDirectory
            {
                get
                {
                    return DirEntry.IsDirectory;
                }
                set { throw new NotImplementedException(); }
            }

            public ulong LastWriteTime
            {
                get
                {
                    return FileSystem.GdfHeader.TimeStamp;
                }
                set { throw new NotImplementedException(); }
            }
            public ulong CreationTime
            {
                get
                {
                    return FileSystem.GdfHeader.TimeStamp;
                }
                set { throw new NotImplementedException(); }
            }

            public List<IFileEntry> Children { get; set; }
            public IFileEntry Parent { get; set; }

            public FileEntry(FileEntry parent, GdfxFileSystem fileSystem)
            {
                Parent = parent;
                FileSystem = fileSystem;
            }

            public bool Read(Stream stream)
            {
                DirEntryAddr = stream.Position;
                DirEntry = stream.ReadStruct<GDF_DIRECTORY_ENTRY>();
                if (!DirEntry.IsValid)
                    return false;

                byte[] fileNameBytes = new byte[DirEntry.FileNameLength];
                stream.Read(fileNameBytes, 0, DirEntry.FileNameLength);
                fileName = Encoding.ASCII.GetString(fileNameBytes);

                // Align code from Velocity, seems to work great
                stream.Position = (long)((ulong)(DirEntryAddr + DirEntry.FileNameLength + 0x11) & 0xFFFFFFFFFFFFFFFC);
                return true;
            }

            public uint ReadBytes(IntPtr buffer, ulong fileOffset, uint length)
            {
                if (fileOffset >= Size)
                    return 0;

                if (fileOffset + length >= Size)
                    length = (uint)(Size - fileOffset);

                byte[] bytes = new byte[length];
                lock (FileSystem.StreamLock)
                {
                    FileSystem.Stream.Seek((long)(FileSystem.GdfSectorToAddress(DirEntry.FirstSector) + fileOffset), SeekOrigin.Begin);
                    int read = FileSystem.Stream.Read(bytes, 0, (int)length);
                    Marshal.Copy(bytes, 0, buffer, read);
                    return (uint)read;
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct GDF_VOLUME_DESCRIPTOR
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] Magic;

        public uint RootSector;
        public uint RootSize;
        public ulong TimeStamp;
        public uint Version;

        public bool IsValid
        {
            get
            {
                return Magic.SequenceEqual(Encoding.ASCII.GetBytes("MICROSOFT*XBOX*MEDIA"));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct GDF_DIRECTORY_ENTRY
    {
        // mostly the same values as System.IO.FileAttributes... we'll keep them here just in case
        enum Attribs : byte
        {
            ReadOnly = 1,
            Hidden = 2,
            System = 4,
            Directory = 0x10,
            Archive = 0x20,
            Device = 0x40,
            Normal = 0x80
        }

        public ushort LeftEntryIndex;
        public ushort RightEntryIndex;
        public uint FirstSector;
        public uint FileSize;
        public byte FileAttributes;
        public byte FileNameLength;

        public bool IsDirectory
        {
            get
            {
                return (FileAttributes & (byte)Attribs.Directory) == (byte)Attribs.Directory;
            }
        }

        public bool IsValid
        {
            get
            {
                return !(LeftEntryIndex == 0xFFFF && RightEntryIndex == 0xFFFF && FirstSector == 0xFFFFFFFF);
            }
        }
        // name follows
    }
}
