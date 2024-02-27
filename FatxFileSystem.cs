using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Fsp;

// TODO:
// - more invalid/deleted checks
// - better error handling
// - metadata.ini informing about deleted files/errors?
namespace XboxWinFsp
{
    public class FatxFileSystem : ReadOnlyFileSystem
    {
        public const int kSectorSize = 0x200;

        public const uint kMagicFatx = 0x58544146;
        public const uint kMagicFatxBE = 0x46415458;

        public const uint kMaxDirectoryEntries = 4096; // max num of entries per directory
        public const uint kMaxDirectorySize = kMaxDirectoryEntries * 0x40; // 0x40 = sizeof(FAT_DIRECTORY_ENTRY)
        public const int kMaxFilenameLength = 42;
        public const ushort kMaxPathLength = 240;

        public const uint kClusterFree = 0;
        public const uint kClusterReserved = 0xfffffff0;
        public const uint kClusterBad = 0xfffffff7;
        public const uint kClusterMedia = 0xfffffff8;
        public const uint kClusterLast = 0xffffffff;

        public const uint kCluster16Reserved = 0xfff0;
        public const uint kCluster16Bad = 0xfff7;
        public const uint kCluster16Media = 0xfff8;

        const uint kFatxPageSize = 0x1000; // size of a cache-page?

        FAT_VOLUME_METADATA FatHeader;
        bool IsBigEndian = false;
        bool IsOgXboxFatx = false;

        long PartitionSize = 0;

        long Position = 0;
        long MaxSize = 0;
        long ClusterCount = 0;
        long DataAddress = 0;
        uint[] ChainMap;

        // The earliest CreationTime in all the file entries
        DateTime CreationTime = DateTime.Now;

        BinaryReader reader;

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

        public FatxFileSystem(Stream stream, string inputPath, long partitionOffset = 0, long partitionSize = 0, bool ogXbox = false) : base(stream, inputPath, kSectorSize)
        {
            Position = partitionOffset;
            PartitionSize = partitionSize;
            IsOgXboxFatx = ogXbox;
            reader = new BinaryReader(stream);
        }

        public void FatxInit()
        {
            if (Position == 0)
                Position = Stream.Position;
            if (PartitionSize == 0)
                PartitionSize = Stream.Length;

            FatHeader = reader.ReadStruct<FAT_VOLUME_METADATA>();
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
            MaxSize = Math.Max(0x1000000, PartitionSize);
            ClusterCount = (MaxSize / ClusterSize);

            // Read in the chainmap...
            Stream.Position = Position + kFatxPageSize;

            ChainMap = new uint[ClusterCount];
            uint numFree = 0;

            int entrySize = (IsFatx32 ? 4 : 2);
            byte[] chainMapBytes = new byte[ClusterCount * entrySize];
            Stream.Read(chainMapBytes, 0, (int)ClusterCount * entrySize);
            for (uint i = 0; i < ClusterCount; i++)
            {
                if (IsBigEndian)
                    Array.Reverse(chainMapBytes, (int)(i * entrySize), entrySize);

                ChainMap[i] = IsFatx32 ? BitConverter.ToUInt32(chainMapBytes, (int)(i * entrySize)) : BitConverter.ToUInt16(chainMapBytes, (int)(i * entrySize));

                // Extend 16-bit end-of-chain values
                // TODO: BE 16-bit kCluster16XXX values seem messed up?
                if (!IsFatx32)
                    if ((ChainMap[i] & 0xFFF0) == 0xFFF0 || (ChainMap[i] & 0xF0FF) == 0xF0FF)
                        ChainMap[i] |= 0xFFFF0000;

                if (ChainMap[i] == 0)
                    numFree++;
            }

            if (ChainMap[0] != kClusterMedia && ChainMap[0] != 0xFFFFF8FF) // 0xFFFFF8FF = weird BE 16-bit value after swapping/extending...
                throw new FileSystemParseException($"Invalid reserved-chainmap-entry value");

            // Calculate byte totals
            BytesFree = numFree * ClusterSize;
            BytesInUse = ((ulong)ClusterCount * ClusterSize) - BytesFree;

            // Calculate address of data start
            long chainMapLength = ClusterCount * (IsFatx32 ? 4 : 2);
            chainMapLength = (long)Utility.RoundToPages((ulong)chainMapLength, kFatxPageSize) * kFatxPageSize;
            DataAddress = Position + kFatxPageSize + chainMapLength;

            // Finally, start reading in the direntries...
            RootFiles = FatxReadDirectory(FatHeader.RootDirFirstCluster, null);
        }

        List<IFileEntry> FatxReadDirectory(uint directoryCluster, FileEntry parent)
        {
            var entries = new List<IFileEntry>();

            // Work out a maximum number of clusters to read, in case volume has some kind of corruption
            int maxClusterCount = (int)(Utility.RoundToPages(kMaxDirectorySize, ClusterSize) + 1);

            var directoryChain = FatxGetClusterChain(directoryCluster, maxClusterCount);
            long addrStart = FatxClusterToAddress(directoryCluster);
            for (int i = 0; i < directoryChain.Length; i++)
            {
                var cluster = directoryChain[i];
                Stream.Seek(FatxClusterToAddress(cluster), SeekOrigin.Begin);

                bool noMoreEntries = false;
                for (int y = 0; y < (ClusterSize / 0x40); y++)
                {
                    var entry = new FileEntry(parent, this);
                    if (!entry.Read(reader))
                    {
                        noMoreEntries = true;
                        break;
                    }

                    if (entry.CreationTime < CreationTime)
                        CreationTime = entry.CreationTime;

                    if (!entry.DirEntry.IsDeleted)
                        entries.Add(entry);
                }

                if (noMoreEntries || entries.Count >= kMaxDirectoryEntries)
                    break;
            }

            // Go back through and read directories
            foreach(var entry in entries)
            {
                var fileEntry = (FileEntry)entry;
                if (fileEntry.IsDirectory && !fileEntry.DirEntry.IsDeleted)
                    try
                    {
                        fileEntry.Children = FatxReadDirectory(fileEntry.DirEntry.FirstCluster, fileEntry);
                    }
                    catch
                    {
                        fileEntry.Children = new List<IFileEntry>();
                        break;
                    }
            }

            entries.Sort((x, y) => x.Name.CompareTo(y.Name));

            return entries;
        }

        uint[] FatxGetClusterChain(uint cluster, int limit = int.MaxValue)
        {
            var chain = new List<uint>();
            while (cluster != kClusterLast && cluster != kClusterMedia && cluster != kClusterBad && limit > chain.Count)
            {
                chain.Add(cluster);
                cluster = ChainMap[cluster];
            }
            return chain.ToArray();
        }

        long FatxClusterToAddress(long cluster)
        {
            return DataAddress + ((cluster - 1) * ClusterSize);
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
                Host.MaxComponentLength = kMaxPathLength;
                Host.FileInfoTimeout = 1000;
                Host.CaseSensitiveSearch = false;
                Host.CasePreservedNames = true;
                Host.UnicodeOnDisk = false;
                Host.PersistentAcls = false;
                Host.PassQueryDirectoryPattern = false;
                Host.FlushAndPurgeOnCleanup = false;
                try
                {
                    Host.VolumeCreationTime = (ulong)CreationTime.ToFileTimeUtc();
                }
                catch
                {
                    Host.VolumeCreationTime = 0;
                }
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
                    return FileSystem.IsOgXboxFatx ? DirEntry.CreationTimeOgXbox : DirEntry.CreationTime;
                }
                set { throw new NotImplementedException(); }
            }

            public DateTime LastWriteTime
            {
                get
                {
                    return FileSystem.IsOgXboxFatx ? DirEntry.LastWriteTimeOgXbox : DirEntry.LastWriteTime;
                }
                set { throw new NotImplementedException(); }
            }

            public DateTime LastAccessTime
            {
                get
                {
                    return FileSystem.IsOgXboxFatx ? DirEntry.LastAccessTimeOgXbox : DirEntry.LastAccessTime;
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

            public bool Read(BinaryReader reader)
            {
                DirEntry = reader.ReadStruct<FAT_DIRECTORY_ENTRY>();
                if (FileSystem.IsBigEndian)
                    DirEntry.EndianSwap();
                return FileSystem.IsOgXboxFatx ? DirEntry.IsValidOgXbox : DirEntry.IsValid;
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

                    Marshal.Copy(bytes, 0, buffer, readAmt);
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

            public override string ToString()
            {
                return $"{(IsDirectory ? "D" : "F")} {Name} 0x{Size:X}";
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct FAT_VOLUME_METADATA
    {
        public uint Signature;
        public uint SerialNumber;
        public uint SectorsPerCluster;
        public uint RootDirFirstCluster;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] VolumeNameBytes;
      //  [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)]
      //  public byte[] OnlineData;

        public bool IsValid
        {
            get
            {
                return Signature == FatxFileSystem.kMagicFatx || Signature == FatxFileSystem.kMagicFatxBE;
            }
        }

        public string VolumeName
        {
            get
            {
                return Encoding.Unicode.GetString(VolumeNameBytes).Trim(new char[] { '\0', (char)0xff, (char)0xffff });
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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
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
                return FileNameLength > 0 && FileNameLength <= FatxFileSystem.kMaxFilenameLength;
            }
        }

        public bool IsValid
        {
            get
            {
                if (!IsValidFileNameLength || FirstCluster <= 0)
                    return false;
                try
                {
                    // ToFileTimeUtc will throw exception if time is invalid
                    // Just hope these don't get optimized out..
                    bool test1 = CreationTime.ToFileTimeUtc() == CreationTime.ToFileTimeUtc();
                    bool test2 = LastWriteTime.ToFileTimeUtc() == LastWriteTime.ToFileTimeUtc();
                    bool test3 = LastAccessTime.ToFileTimeUtc() == LastAccessTime.ToFileTimeUtc();
                    return test1 && test2 && test3; // TODO: more checks?
                }
                catch
                {
                    return false;
                }
            }
        }

        public bool IsValidOgXbox
        {
            get
            {
                if (!IsValidFileNameLength || FirstCluster <= 0)
                    return false;
                try
                {
                    // ToFileTimeUtc will throw exception if time is invalid
                    // Just hope these don't get optimized out..
                    bool test1 = CreationTimeOgXbox.ToFileTimeUtc() == CreationTimeOgXbox.ToFileTimeUtc();
                    bool test2 = LastWriteTimeOgXbox.ToFileTimeUtc() == LastWriteTimeOgXbox.ToFileTimeUtc();
                    bool test3 = LastAccessTimeOgXbox.ToFileTimeUtc() == LastAccessTimeOgXbox.ToFileTimeUtc();
                    return test1 && test2 && test3; // TODO: more checks?
                }
                catch
                {
                    return false;
                }
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

        public DateTime CreationTimeOgXbox
        {
            get
            {
                return Utility.DecodeMSTime(CreationTimeRaw, true);
            }
        }

        public DateTime LastWriteTime
        {
            get
            {
                return Utility.DecodeMSTime(LastWriteTimeRaw);
            }
        }

        public DateTime LastWriteTimeOgXbox
        {
            get
            {
                return Utility.DecodeMSTime(LastWriteTimeRaw, true);
            }
        }

        public DateTime LastAccessTime
        {
            get
            {
                return Utility.DecodeMSTime(LastAccessTimeRaw);
            }
        }

        public DateTime LastAccessTimeOgXbox
        {
            get
            {
                return Utility.DecodeMSTime(LastAccessTimeRaw, true);
            }
        }

        public string FileName
        {
            get
            {
                if (IsDeleted)
                    return "_" + Encoding.ASCII.GetString(FileNameBytes).Trim(new char[] { '\0', (char)0xff });
                else
                    return Encoding.ASCII.GetString(FileNameBytes, 0, FileNameLength).Trim(new char[] { '\0', (char)0xff });
            }
            set
            {
                if(!IsDeleted)
                    FileNameLength = (byte)value.Length;

                FileNameBytes = Encoding.ASCII.GetBytes(value);

                if (FileNameBytes.Length > FatxFileSystem.kMaxFilenameLength)
                    Array.Resize(ref FileNameBytes, FatxFileSystem.kMaxFilenameLength);
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
