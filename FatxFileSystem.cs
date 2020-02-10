using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Fsp;

namespace XboxWinFsp
{
    public class FatxFileSystem : ReadOnlyFileSystem
    {
        public const int kSectorSize = 0x200;
        public const uint kMagicFatx = 0x58544146;
        public const uint kMagicFatxBE = 0x46415458;

        public const uint kClusterFree = 0;
        public const uint kClusterReserved = 0xfffffff0;
        public const uint kClusterBad = 0xfffffff7;
        public const uint kClusterMedia = 0xfffffff8;
        public const uint kClusterLast = 0xffffffff;

        public const uint kCluster16Reserved = 0xfff0;
        public const uint kCluster16Bad = 0xfff7;
        public const uint kCluster16Media = 0xfff8;

        const uint kReservedChainMapEntries = 1; // first entry in chainmap is reserved (doesn't actually exist)
        const uint kFatxPageSize = 0x1000; // size of a cache-page?

        FAT_VOLUME_METADATA FatHeader;
        bool IsBigEndian = false;

        long Position = 0;
        long MaxSize = 0;
        long ClusterCount = 0; // is +1 of the actual count, because first cluster is kReservedChainMapEntries..
        long DataAddress = 0;
        uint[] ChainMap;

        // The earliest CreationTime in all the file entries
        DateTime CreationTime = DateTime.Now;

        object StreamLock = new object();

        bool IsFatx32
        {
            get
            {
                return ClusterCount >= 0xFFF0;
            }
        }

        public uint ClusterSize
        {
            get
            {
                return FatHeader.SectorsPerCluster * FatxFileSystem.kSectorSize;
            }
        }

        public FatxFileSystem(Stream stream, string inputPath) : base(stream, inputPath, kSectorSize)
        {

        }

        void FatxInit()
        {
            Position = Stream.Position;

            FatHeader = Stream.ReadStruct<FAT_VOLUME_METADATA>();
            if (FatHeader.Signature == kMagicFatxBE)
            {
                IsBigEndian = true;
                FatHeader.EndianSwap();
            }

            if (FatHeader.Signature != kMagicFatx)
                throw new FileSystemParseException("FATX magic invalid");

            switch (FatHeader.SectorsPerCluster)
            {
                case 0x2:
                case 0x4:
                case 0x8:
                case 0x10:
                case 0x20:
                case 0x40:
                case 0x80:
                    break;
                default:
                    throw new FileSystemParseException($"Invalid SectorsPerCluster value {FatHeader.SectorsPerCluster}");
            }

            // Work out a "maximum size" of the partition
            // (xlaunch.fdf can be tiny, 1MB or so, but the kernel treats it like it's 0x1000000 bytes, so that it can expand as needed)
            // (so we have to do the same in order for the chainMap calculations below to make sense)
            // 0x1000000 seems to be the smallest out there, so we'll use that
            MaxSize = Math.Max(0x1000000, Stream.Length);
            ClusterCount = (MaxSize / ClusterSize) + kReservedChainMapEntries;

            // Read in the chainmap...
            Stream.Position = Position + kFatxPageSize;

            ChainMap = new uint[ClusterCount];
            byte[] data = new byte[4];

            uint numFree = 0;
            for (uint i = 0; i < ClusterCount; i++)
            {
                Stream.Read(data, 0, IsFatx32 ? 4 : 2);
                if (IsBigEndian)
                    Array.Reverse(data, 0, IsFatx32 ? 4 : 2);

                ChainMap[i] = IsFatx32 ? BitConverter.ToUInt32(data, 0) : BitConverter.ToUInt16(data, 0);

                // Extend 16-bit end-of-chain values
                // TODO: BE 16-bit kCluster16XXX values seem messed up?
                if (!IsFatx32)
                    if ((ChainMap[i] & 0xFFF0) == 0xFFF0 || (ChainMap[i] & 0xF0FF) == 0xF0FF)
                        ChainMap[i] |= 0xFFFF0000;

                if (ChainMap[i] == 0)
                    numFree++;
            }

            if(ChainMap[0] != kClusterMedia && ChainMap[0] != 0xFFFFF8FF) // 0xFFFFF8FF = weird BE 16-bit value after swapping/extending...
                throw new FileSystemParseException($"Invalid reserved-chainmap-entry value");

            // Calculate byte totals
            BytesFree = numFree * ClusterSize;
            BytesInUse = ((ulong)(ClusterCount - kReservedChainMapEntries) * ClusterSize) - BytesFree;

            // Calculate address of data start
            long chainMapLength = ClusterCount * (IsFatx32 ? 4 : 2);
            chainMapLength = (long)Utility.RoundToPages((ulong)chainMapLength, kFatxPageSize) * kFatxPageSize;
            DataAddress = Position + kFatxPageSize + chainMapLength;

            // Finally, start reading in the direntries...
            RootFiles = FatxReadDirectory(FatHeader.RootDirFirstCluster, null);
        }

        List<IFileEntry> FatxReadDirectory(uint directoryCluster, FileEntry parent)
        {
            long curPos = Stream.Position;
            var entries = new List<IFileEntry>();

            var directoryChain = FatxGetClusterChain(directoryCluster);
            for (int i = 0; i < directoryChain.Length; i++)
            {
                var cluster = directoryChain[i];
                Stream.Seek(FatxClusterToAddress(cluster), SeekOrigin.Begin);

                bool noMoreEntries = false;
                for (int y = 0; y < (ClusterSize / 0x40); y++)
                {
                    var entry = new FileEntry(parent, this);
                    if (!entry.Read(Stream))
                    {
                        noMoreEntries = true;
                        break;
                    }

                    if (entry.CreationTime < CreationTime)
                        CreationTime = entry.CreationTime;

                    if (entry.IsDirectory)
                        entry.Children = FatxReadDirectory(entry.DirEntry.FirstCluster, entry);

                    entries.Add(entry);
                }

                if (noMoreEntries)
                    break;
            }

            entries.Sort((x, y) => x.Name.CompareTo(y.Name));

            Stream.Position = curPos;
            return entries;
        }

        uint[] FatxGetClusterChain(uint cluster)
        {
            var chain = new List<uint>();
            while (cluster != 0xFFFFFFFF)
            {
                chain.Add(cluster);
                cluster = ChainMap[cluster];
            }
            return chain.ToArray();
        }

        long FatxClusterToAddress(long cluster)
        {
            return DataAddress + ((cluster - kReservedChainMapEntries) * ClusterSize);
        }

        public override Int32 Init(Object Host0)
        {
            if (Stream == null)
                return STATUS_OPEN_FAILED;

            try
            {
                FatxInit();

                var Host = (FileSystemHost)Host0;
                Host.SectorSize = kSectorSize;
                Host.SectorsPerAllocationUnit = (ushort)FatHeader.SectorsPerCluster;
                Host.MaxComponentLength = 255;
                Host.FileInfoTimeout = 1000;
                Host.CaseSensitiveSearch = false;
                Host.CasePreservedNames = true;
                Host.UnicodeOnDisk = false;
                Host.PersistentAcls = false;
                Host.PassQueryDirectoryPattern = true;
                Host.FlushAndPurgeOnCleanup = true;
                Host.VolumeCreationTime = (ulong)CreationTime.ToFileTimeUtc();
                Host.VolumeSerialNumber = FatHeader.SerialNumber;
                Host.FileSystemName = $"FATX{(IsFatx32 ? "32" : "16")}";

                var volName = FatHeader.VolumeName;
                if (!string.IsNullOrEmpty(volName))
                    VolumeLabel = volName;

                return base.Init(Host0);
            }
            catch (FileSystemParseException)
            {
                return STATUS_OPEN_FAILED;
            }
        }

        // Info about a file stored inside the FATX image
        protected class FileEntry : IFileEntry
        {
            FatxFileSystem FileSystem;
            internal FAT_DIRECTORY_ENTRY DirEntry;

            uint[] ClusterChain = null;

            public string Name
            {
                get
                {
                    return DirEntry.FileName;
                }
                set
                {
                    DirEntry.FileName = value;
                }
            }

            public ulong Size
            {
                get
                {
                    return DirEntry.FileSize;
                }
                set
                {
                    DirEntry.FileSize = (uint)value;
                }
            }

            public bool IsDirectory
            {
                get
                {
                    return DirEntry.IsDirectory;
                }
                set
                {
                    // TODO
                    //DirEntry.IsDirectory = value;
                }
            }

            public DateTime CreationTime
            {
                get
                {
                    return DirEntry.CreationTime;
                }
                set { throw new NotImplementedException(); }
            }

            public DateTime LastWriteTime
            {
                get
                {
                    return DirEntry.LastWriteTime;
                }
                set { throw new NotImplementedException(); }
            }

            public DateTime LastAccessTime
            {
                get
                {
                    return DirEntry.LastAccessTime;
                }
                set { throw new NotImplementedException(); }
            }

            public List<IFileEntry> Children { get; set; }
            public IFileEntry Parent { get; set; }

            public FileEntry(FileEntry parent, FatxFileSystem fileSystem)
            {
                Parent = parent;
                FileSystem = fileSystem;
            }

            public bool Read(Stream stream)
            {
                DirEntry = stream.ReadStruct<FAT_DIRECTORY_ENTRY>();
                if (FileSystem.IsBigEndian)
                    DirEntry.EndianSwap();
                return DirEntry.IsValid;
            }

            public uint ReadBytes(IntPtr buffer, ulong fileOffset, uint length)
            {
                if (fileOffset >= Size)
                    return 0;

                if (fileOffset + length >= Size)
                    length = (uint)(Size - fileOffset);

                // Lock so that two threads can't try updating chain at once...
                lock(this)
                    if (ClusterChain == null)
                        ClusterChain = FileSystem.FatxGetClusterChain(DirEntry.FirstCluster);

                int chainIndex = (int)(fileOffset / FileSystem.ClusterSize);
                int clusterOffset = (int)(fileOffset % FileSystem.ClusterSize);

                int clusterRemaining = (int)(FileSystem.ClusterSize - clusterOffset);
                uint lengthRemaining = length;

                byte[] bytes = new byte[FileSystem.ClusterSize];
                uint transferred = 0;
                while (lengthRemaining > 0)
                {
                    int readAmt = clusterRemaining;
                    if ((uint)readAmt > lengthRemaining)
                        readAmt = (int)lengthRemaining;

                    var clusterNum = ClusterChain[chainIndex];

                    int numRead = 0;
                    lock (FileSystem.StreamLock)
                    {
                        FileSystem.Stream.Seek(FileSystem.FatxClusterToAddress(clusterNum) + clusterOffset, SeekOrigin.Begin);
                        numRead = FileSystem.Stream.Read(bytes, 0, readAmt);
                    }

                    Marshal.Copy(bytes, 0, buffer, numRead);
                    transferred += (uint)numRead;

                    if (clusterOffset + readAmt >= FileSystem.ClusterSize)
                        chainIndex++;

                    buffer += readAmt;
                    clusterRemaining = (int)FileSystem.ClusterSize;
                    clusterOffset = 0;
                    lengthRemaining -= (uint)readAmt;
                }
                return transferred;
            }
        }
    }

    public struct FAT_VOLUME_METADATA
    {
        public uint Signature;
        public uint SerialNumber;
        public uint SectorsPerCluster;
        public uint RootDirFirstCluster;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] VolumeNameBytes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)]
        public byte[] OnlineData;

        public string VolumeName
        {
            get
            {
                return Encoding.Unicode.GetString(VolumeNameBytes).Trim(new char[] { '\0' });
            }
        }

        public void EndianSwap()
        {
            Signature = Signature.EndianSwap();
            SerialNumber = SerialNumber.EndianSwap();
            SectorsPerCluster = SectorsPerCluster.EndianSwap();
            RootDirFirstCluster = RootDirFirstCluster.EndianSwap();

            for (int i = 0; i < VolumeNameBytes.Length; i += 2)
                Array.Reverse(VolumeNameBytes, i, 2);
        }
    }

    public struct FAT_DIRECTORY_ENTRY
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

        public byte FileNameLength;
        public byte Attributes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 42)]
        public byte[] FileNameBytes;
        public uint FirstCluster;
        public uint FileSize;
        public int CreationTimeRaw;
        public int LastWriteTimeRaw;
        public int LastAccessTimeRaw;

        public bool IsDirectory
        {
            get
            {
                return (Attributes & (byte)Attribs.Directory) == (byte)Attribs.Directory;
            }
        }

        bool IsValidFileNameLength
        {
            get
            {
                if (IsDeleted)
                    return true;
                return FileNameLength > 0 && FileNameLength <= 42;
            }
        }

        public bool IsValid
        {
            get
            {
                return IsValidFileNameLength && FirstCluster > 0; // TODO: more checks?
            }
        }

        public bool IsDeleted
        {
            get
            {
                return FileNameLength == 0xE5;
            }
        }

        public DateTime CreationTime
        {
            get
            {
                return Utility.DecodeMSTime(CreationTimeRaw);
            }
        }

        public DateTime LastWriteTime
        {
            get
            {
                return Utility.DecodeMSTime(LastWriteTimeRaw);
            }
        }

        public DateTime LastAccessTime
        {
            get
            {
                return Utility.DecodeMSTime(LastAccessTimeRaw);
            }
        }

        public string FileName
        {
            get
            {
                if(IsDeleted) // TODO: maybe we should hide deleted files?
                    return "_" + Encoding.ASCII.GetString(FileNameBytes).Trim(new char[] { '\0', (char)0xff });
                else
                    return Encoding.ASCII.GetString(FileNameBytes, 0, FileNameLength).Trim(new char[] { '\0', (char)0xff });
            }
            set
            {
                FileNameLength = (byte)value.Length;
                FileNameBytes = Encoding.ASCII.GetBytes(value);
            }
        }

        public void EndianSwap()
        {
            FirstCluster = FirstCluster.EndianSwap();
            FileSize = FileSize.EndianSwap();
            CreationTimeRaw = CreationTimeRaw.EndianSwap();
            LastWriteTimeRaw = LastWriteTimeRaw.EndianSwap();
            LastAccessTimeRaw = LastAccessTimeRaw.EndianSwap();
        }
    }
}
