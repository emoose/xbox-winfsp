using System;
using System.Collections;
using System.IO;
using System.Runtime.InteropServices;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Fsp;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FileInfo = Fsp.Interop.FileInfo;
using System.Reflection;

namespace XboxWinFsp
{
    public class FatxFileSystem : FileSystemBase
    {
        public const uint kSectorSize = 0x200;
        public const uint kFatxMagic = 0x58544146;
        public const uint kFatxMagicBE = 0x46415458;

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

        string imagePath = "";
        Stream stream = null;
        Object streamLock = new object();

        internal FAT_VOLUME_METADATA header;
        bool isBigEndian = false;

        long partitionMaxSize = 0;
        long partitionClusterCount = 0;
        long partitionDataAddress = 0;
        uint[] chainMap;

        FatxEntry[] rootChildren;
        ulong totalBytesInUse = 0;
        
        bool IsFatx32
        {
            get
            {
                return (partitionClusterCount + 1) >= 0xFFF0;
            }
        }

        public FatxFileSystem(string imagePath)
        {
            this.imagePath = imagePath;
        }

        protected class FatxEntry
        {
            FatxFileSystem fileSystem;

            public FAT_DIRECTORY_ENTRY DirEntry;
            public FatxEntry[] Children;
            public FatxEntry Parent;

            internal uint[] ClusterChain; // Chain gets read in once a FileDesc is created for the entry

            public FatxEntry(FatxEntry parent, FatxFileSystem fileSystem)
            {
                Parent = parent;
                this.fileSystem = fileSystem;
            }

            public bool Read(Stream stream)
            {
                DirEntry = stream.ReadStruct<FAT_DIRECTORY_ENTRY>();
                if (fileSystem.isBigEndian)
                    DirEntry.EndianSwap();

                return DirEntry.IsValid;
            }
        }

        // An open instance of a file
        protected class FileDesc
        {
            FatxFileSystem fileSystem;

            internal FatxEntry FileEntry;

            internal FileDesc(FatxFileSystem fileSystem)
            {
                this.fileSystem = fileSystem;
            }

            internal FileDesc(FatxEntry entry, FatxFileSystem fileSystem)
            {
                FileEntry = entry;
                this.fileSystem = fileSystem;

                // We'll only fill the block chain once a FileDesc has been created for the file:
                if (entry != null)
                    lock (entry)
                        if (entry.ClusterChain == null)
                            entry.ClusterChain = fileSystem.FatxGetClusterChain(entry.DirEntry.FirstCluster);
            }

            public Int32 GetFileInfo(out FileInfo FileInfo)
            {
                FileInfo = new FileInfo();
                FileInfo.FileAttributes = (uint)FileAttributes.Directory;

                if (FileEntry != null)
                {
                    FileInfo.CreationTime = (ulong)FileEntry.DirEntry.CreationTime;//.ToFileTimeUtc();
                    FileInfo.LastWriteTime = FileInfo.ChangeTime = (ulong)FileEntry.DirEntry.LastWriteTime;//.ToFileTimeUtc();
                    FileInfo.FileSize = FileEntry.DirEntry.FileSize;
                    FileInfo.AllocationSize = (ulong)Utility.RoundToPages(FileEntry.DirEntry.FileSize, fileSystem.header.ClusterSize) * fileSystem.header.ClusterSize;
                    FileInfo.FileAttributes = FileEntry.DirEntry.IsDirectory ? (uint)FileAttributes.Directory : 0;
                }

                FileInfo.FileAttributes |= (uint)FileAttributes.ReadOnly;

                return STATUS_SUCCESS;
            }
        }

        public override Boolean ReadDirectoryEntry(
           Object FileNode,
           Object FileDesc0,
           String Pattern,
           String Marker,
           ref Object Context,
           out String FileName,
           out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            IEnumerator<FatxEntry> Enumerator = (IEnumerator<FatxEntry>)Context;

            if (Enumerator == null)
            {
                var childArr = rootChildren;
                if (FileDesc.FileEntry != null)
                    childArr = FileDesc.FileEntry.Children;

                Enumerator = new List<FatxEntry>(childArr).GetEnumerator();
                Context = Enumerator;
                int Index = 0;
                if (null != Marker)
                {
                    var testEntry = new FatxEntry(null, this);
                    testEntry.DirEntry.FileName = Marker;
                    Index = Array.BinarySearch(childArr, testEntry, _DirectoryEntryComparer);
                    if (0 <= Index)
                        Index++;
                    else
                        Index = ~Index;
                }

                if (Index > 0)
                    for (int i = 0; i < Index; i++)
                        Enumerator.MoveNext();
            }

            if (Enumerator.MoveNext())
            {
                var entry = Enumerator.Current;
                FileName = entry.DirEntry.FileName;
                var desc = new FileDesc(entry, this);
                desc.GetFileInfo(out FileInfo);
                return true;
            }
            else
            {
                FileName = default;
                FileInfo = default;
                return false;
            }
        }

        public override Int32 GetSecurityByName(
            String FileName,
            out UInt32 FileAttributes1/* or ReparsePointIndex */,
            ref Byte[] SecurityDescriptor)
        {
            FileAttributes1 = 0;
            if (FileName == "\\")
                FileAttributes1 = (uint)FileAttributes.Directory;
            else
            {
                var entry = FatxFindFile(FileName);
                if (entry != null)
                    FileAttributes1 = entry.DirEntry.IsDirectory ? (uint)FileAttributes.Directory : 0;
                else
                    return STATUS_NO_SUCH_FILE;
            }
            return STATUS_SUCCESS;
        }

        public override Int32 Open(
            String FileName,
            UInt32 CreateOptions,
            UInt32 GrantedAccess,
            out Object FileNode,
            out Object FileDesc0,
            out FileInfo FileInfo,
            out String NormalizedName)
        {
            FileNode = default;
            NormalizedName = default;

            if (string.IsNullOrEmpty(FileName) || FileName == "\\")
            {
                // No file path? Return some info about the root dir..
                var fileDesc = new FileDesc(this);
                FileDesc0 = fileDesc;
                return fileDesc.GetFileInfo(out FileInfo);
            }

            var foundEntry = FatxFindFile(FileName);

            if (foundEntry == null)
            {
                FileInfo = default;
                FileDesc0 = null;
                return STATUS_NO_SUCH_FILE;
            }

            var fileDesc2 = new FileDesc(foundEntry, this);
            FileDesc0 = fileDesc2;
            return fileDesc2.GetFileInfo(out FileInfo);
        }

        public override Int32 Read(
            Object FileNode,
            Object FileDesc0,
            IntPtr Buffer,
            UInt64 Offset,
            UInt32 Length,
            out UInt32 PBytesTransferred)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            if (Offset >= (UInt64)FileDesc.FileEntry.DirEntry.FileSize)
            {
                PBytesTransferred = 0;
                return STATUS_END_OF_FILE;
            }
            if (Offset + Length >= (UInt64)FileDesc.FileEntry.DirEntry.FileSize)
            {
                Length = (uint)(FileDesc.FileEntry.DirEntry.FileSize - Offset);
            }

            uint numBlocks = (Length + header.ClusterSize - 1) / header.ClusterSize;
            uint chainNum = (uint)(Offset / header.ClusterSize);
            uint blockOffset = (uint)(Offset % header.ClusterSize);

            uint blockRemaining = header.ClusterSize - blockOffset;
            uint lengthRemaining = Length;
            uint transferred = 0;

            byte[] bytes = new byte[header.ClusterSize];
            while (lengthRemaining > 0)
            {
                var blockNum = FileDesc.FileEntry.ClusterChain[chainNum];

                uint toRead = blockRemaining;
                if (toRead > lengthRemaining)
                    toRead = lengthRemaining;

                int read = 0;
                lock (streamLock)
                {
                    stream.Seek((long)FatxClusterToAddress(blockNum) + blockOffset, SeekOrigin.Begin);
                    read = stream.Read(bytes, 0, (int)toRead);
                }

                Marshal.Copy(bytes, 0, Buffer, read);
                transferred += (uint)read;

                if (blockOffset + read >= header.ClusterSize)
                    chainNum++;

                Buffer += read;
                blockRemaining = header.ClusterSize;
                blockOffset = 0;
                lengthRemaining -= (uint)read;
            }
            PBytesTransferred = transferred;

            return STATUS_SUCCESS;
        }

        public override Int32 GetFileInfo(
            Object FileNode,
            Object FileDesc0,
            out FileInfo FileInfo)
        {
            FileDesc FileDesc = (FileDesc)FileDesc0;
            return FileDesc.GetFileInfo(out FileInfo);
        }

        public override Int32 GetVolumeInfo(out VolumeInfo VolumeInfo)
        {
            VolumeInfo = default;
            VolumeInfo.FreeSize = (ulong)chainMap.Where(e => e == kClusterFree).Count() * header.ClusterSize;
            VolumeInfo.TotalSize = totalBytesInUse + VolumeInfo.FreeSize;

            VolumeInfo.SetVolumeLabel(Path.GetFileName(imagePath));

            return STATUS_SUCCESS;
        }

        public override Int32 Init(Object Host0)
        {
            stream = File.OpenRead(imagePath);
            if (stream == null)
                return STATUS_OPEN_FAILED;

            try
            {
                header = stream.ReadStruct<FAT_VOLUME_METADATA>();
                if (header.Signature == kFatxMagicBE)
                {
                    isBigEndian = true;
                    header.EndianSwap();
                }

                if (header.Signature != kFatxMagic)
                    throw new FileSystemParseException("Invalid FATX signature");

                switch (header.SectorsPerCluster)
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
                        throw new FileSystemParseException($"Invalid SectorsPerCluster value {header.SectorsPerCluster}");
                }

                // Work out a "maximum size" of the partition
                // (xlaunch.fdf can be tiny, 1MB or so, but the kernel treats it like it's 0x1000000 bytes, so that it can expand as needed)
                // (we have to do the same, in order for the chainMap calculations below to make sense)
                // 0x1000000 seems to be the smallest out there, so we'll use that
                partitionMaxSize = Math.Max(0x1000000, stream.Length);

                partitionClusterCount = partitionMaxSize / header.ClusterSize;

                stream.Position = 0x1000;

                chainMap = new uint[partitionClusterCount];
                byte[] data = new byte[4];
                for(uint i = 0; i < partitionClusterCount; i++)
                {
                    stream.Read(data, 0, IsFatx32 ? 4 : 2);
                    if (isBigEndian)
                        Array.Reverse(data, 0, IsFatx32 ? 4 : 2);

                    chainMap[i] = IsFatx32 ? BitConverter.ToUInt32(data, 0) : BitConverter.ToUInt16(data, 0);
                }

                long chainMapLength = ((partitionClusterCount + 1) * (IsFatx32 ? 4 : 2));
                chainMapLength = Utility.RoundToPages(chainMapLength, kFatxPageSize) * kFatxPageSize;

                partitionDataAddress = kFatxPageSize + chainMapLength;

                // Extend 16-bit end-of-chain values
                // TODO: BE 16-bit kCluster16XXX values seem messed up?
                if (!IsFatx32)
                    for(uint i = 0; i < partitionClusterCount; i++)
                        if ((chainMap[i] & 0xFFF0) == 0xFFF0 || (chainMap[i] & 0xF0FF) == 0xF0FF)
                            chainMap[i] |= 0xFFFF0000;

                // Finally, start reading in the direntries...

                rootChildren = FatxReadDirectory(header.RootDirFirstCluster, null);

                return STATUS_SUCCESS;
            }
            catch(FileSystemParseException e)
            {
                stream.Close();
                return STATUS_OPEN_FAILED;
            }
        }

        FatxEntry[] FatxReadDirectory(uint directoryCluster, FatxEntry parent)
        {
            long curPos = stream.Position;

            var directoryChain = FatxGetClusterChain(directoryCluster);
            var entries = new List<FatxEntry>();
            for (int i = 0; i < directoryChain.Length; i++)
            {
                var cluster = directoryChain[i];
                stream.Seek(FatxClusterToAddress(cluster), SeekOrigin.Begin);

                bool noMoreEntries = false;
                for (int y = 0; y < (header.ClusterSize / 0x40); y++)
                {
                    var entry = new FatxEntry(parent, this);
                    if (!entry.Read(stream))
                    {
                        noMoreEntries = true;
                        break;
                    }

                    if (entry.DirEntry.IsDirectory)
                        entry.Children = FatxReadDirectory(entry.DirEntry.FirstCluster, entry);
                    else
                        totalBytesInUse += entry.DirEntry.FileSize;

                    entries.Add(entry);
                }

                if (noMoreEntries)
                {
                    break;
                }
            }

            stream.Position = curPos;
            return entries.ToArray();
        }

        // Navigates a given path string to return the GdfxEntry of the path given
        // TODO: GDFX supports binary tree searching, need to figure out how to use that instead...
        FatxEntry FatxFindFile(string path)
        {
            FatxEntry foundEntry = null;

            if (path.StartsWith("\\", StringComparison.InvariantCultureIgnoreCase))
                path = path.Substring(1);

            string[] filePath = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            // Try going through the split path to try and find the file entry
            var searchList = rootChildren;
            for (int i = 0; i < filePath.Length; i++)
            {
                var entry = Array.Find(searchList, s => s.DirEntry.FileName == filePath[i]);
                if (entry == null)
                    break;
                else
                {
                    // If this is the last section then we've found the file!
                    // Otherwise we'll try searching the children of the current entry instead...
                    if (i + 1 == filePath.Length)
                        foundEntry = entry;
                    else
                        searchList = entry.Children;
                }
            }

            return foundEntry;
        }

        uint[] FatxGetClusterChain(uint cluster)
        {
            var chain = new List<uint>();
            while(cluster != 0xFFFFFFFF)
            {
                chain.Add(cluster);
                cluster = chainMap[cluster];
            }
            return chain.ToArray();
        }

        long FatxClusterToAddress(long cluster)
        {
            return partitionDataAddress + ((cluster - kReservedChainMapEntries) * header.ClusterSize);
        }

        class DirectoryEntryComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                return String.Compare(((FatxEntry)x).DirEntry.FileName, ((FatxEntry)y).DirEntry.FileName);
            }
        }

        static DirectoryEntryComparer _DirectoryEntryComparer = new DirectoryEntryComparer();
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

        public uint ClusterSize
        {
            get
            {
                return SectorsPerCluster * FatxFileSystem.kSectorSize;
            }
        }

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
        public uint CreationTime;
        public uint LastWriteTime;
        public uint LastAccessTime;

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

        public string FileName
        {
            get
            {
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
            CreationTime = CreationTime.EndianSwap();
            LastWriteTime = LastWriteTime.EndianSwap();
            LastAccessTime = LastAccessTime.EndianSwap();
        }
    }
}
