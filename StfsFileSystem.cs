using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Runtime.InteropServices;
using System.IO;

using Fsp;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FileInfo = Fsp.Interop.FileInfo;
using System.Reflection;

namespace XboxWinFsp
{
    public class StfsFileSystem : FileSystemBase
    {
        const uint kSectorSize = 0x1000;

        const uint kMagicConBE = 0x434F4E20;
        const uint kMagicLiveBE = 0x4C495645;
        const uint kMagicPirsBE = 0x50495253;

        static readonly int[] kDataBlocksPerHashLevel = new int[] { 0xAA, 0x70E4, 0x4AF768 };

        string imagePath = "";
        Stream stream;
        Object streamLock = new object();

        XCONTENT_HEADER header;
        XCONTENT_METADATA metadata;

        StfsEntry[] children; // all files in the package
        StfsEntry[] rootChildren; // only files that are in the root directory
        ulong totalBytesInUse = 0;

        // Values used in some block calculations, inited by StfsInit();
        long stfsSizeOfHeaders = 0;
        int stfsBlocksPerHashTable = 1;
        int[] stfsBlockStep = new[] { 0xAB, 0x718F };

        // Cached hash blocks
        List<long> invalidTables = new List<long>();
        Dictionary<long, STF_HASH_BLOCK> cachedTables = new Dictionary<long, STF_HASH_BLOCK>();

        // Info about a file stored inside the STFS image
        protected class StfsEntry
        {
            StfsFileSystem fileSystem;

            public STF_DIRECTORY_ENTRY DirEntry;
            public long DirEntryAddr;
            public StfsEntry[] Children;
            public StfsEntry Parent;

            internal int[] BlockChain; // Chain gets read in once a FileDesc is created for the entry

            public StfsEntry(StfsFileSystem fileSystem)
            {
                this.fileSystem = fileSystem;
            }

            public bool Read(Stream stream)
            {
                DirEntryAddr = stream.Position;
                DirEntry = stream.ReadStruct<STF_DIRECTORY_ENTRY>();
                DirEntry.EndianSwap();
                return DirEntry.IsValid;
            }

            public override string ToString()
            {
                return $"{DirEntry.FileName}" + (Children != null ? $" ({Children.Length} children)" : "");
            }
        }

        // An open instance of a file
        protected class FileDesc
        {
            StfsFileSystem fileSystem;

            internal StfsEntry FileEntry;

            internal FileDesc(StfsFileSystem fileSystem)
            {
                this.fileSystem = fileSystem;
            }

            internal FileDesc(StfsEntry entry, StfsFileSystem fileSystem)
            {
                FileEntry = entry;
                this.fileSystem = fileSystem;

                // We'll only fill the block chain once a FileDesc has been created for the file:
                if (entry != null && entry.BlockChain == null)
                    lock(entry)
                        entry.BlockChain = fileSystem.StfsGetDataBlockChain(entry.DirEntry.FirstBlockNumber);
            }

            public Int32 GetFileInfo(out FileInfo FileInfo)
            {
                FileInfo = new FileInfo();
                FileInfo.FileAttributes = (uint)FileAttributes.Directory;

                if (FileEntry != null)
                {
                    FileInfo.CreationTime = (ulong)FileEntry.DirEntry.CreationTime.ToFileTimeUtc();
                    FileInfo.LastWriteTime = FileInfo.ChangeTime = (ulong)FileEntry.DirEntry.LastWriteTime.ToFileTimeUtc();
                    FileInfo.FileSize = FileEntry.DirEntry.FileSize;
                    FileInfo.AllocationSize = ((FileEntry.DirEntry.FileSize + StfsFileSystem.kSectorSize - 1) / StfsFileSystem.kSectorSize) * StfsFileSystem.kSectorSize;
                    FileInfo.FileAttributes = FileEntry.DirEntry.IsDirectory ? (uint)FileAttributes.Directory : 0;
                }

                FileInfo.FileAttributes |= (uint)FileAttributes.ReadOnly;

                return STATUS_SUCCESS;
            }
        }

        public StfsFileSystem(string filePath)
        {
            imagePath = filePath;
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
            IEnumerator<StfsEntry> Enumerator = (IEnumerator<StfsEntry>)Context;

            if (Enumerator == null)
            {
                var childArr = rootChildren;
                if(FileDesc.FileEntry != null)
                    childArr = FileDesc.FileEntry.Children;

                Enumerator = new List<StfsEntry>(childArr).GetEnumerator();
                Context = Enumerator;
                int Index = 0;
                if (null != Marker)
                {
                    var testEntry = new StfsEntry(this);
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
                var entryIdx = StfsFindFile(FileName);
                if (entryIdx != -1)
                    FileAttributes1 = children[entryIdx].DirEntry.IsDirectory ? (uint)FileAttributes.Directory : 0;
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

            short foundEntry = StfsFindFile(FileName);

            if (foundEntry == -1)
            {
                FileInfo = default;
                FileDesc0 = null;
                return STATUS_NO_SUCH_FILE;
            }

            var ent = children[foundEntry];

            var fileDesc2 = new FileDesc(ent, this);
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

            uint numBlocks = (Length + kSectorSize - 1) / kSectorSize;
            uint startChainIdx = (uint)(Offset / kSectorSize);
            uint blockDone = (uint)(Offset % kSectorSize);
            uint blockRemaining = kSectorSize - blockDone;
            uint lengthRemaining = Length;
            uint transferred = 0;
            for(uint i = 0; i < numBlocks; i++)
            {
                var blockNum = FileDesc.FileEntry.BlockChain[startChainIdx + i];
                uint toRead = blockRemaining;
                if (toRead > lengthRemaining)
                    toRead = lengthRemaining;

                byte[] bytes = new byte[toRead];
                lock (streamLock)
                {
                    stream.Seek((long)StfsDataBlockToOffset(blockNum) + blockDone, SeekOrigin.Begin);
                    transferred += (uint)stream.Read(bytes, 0, bytes.Length);
                }
                Marshal.Copy(bytes, 0, Buffer, bytes.Length);
                Buffer += bytes.Length;
                blockRemaining = kSectorSize;
                blockDone = 0;
                lengthRemaining -= toRead;
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
            VolumeInfo.FreeSize = 0;
            VolumeInfo.TotalSize = totalBytesInUse;
            if (!string.IsNullOrEmpty(metadata.DisplayName[0].String))
                VolumeInfo.SetVolumeLabel(metadata.DisplayName[0].String);
            else if(!string.IsNullOrEmpty(metadata.TitleName.String))
                VolumeInfo.SetVolumeLabel(metadata.TitleName.String);
            else
                VolumeInfo.SetVolumeLabel(Path.GetFileName(imagePath));

            return STATUS_SUCCESS;
        }

        public override Int32 Init(Object Host0)
        {
            try
            {
                stream = File.OpenRead(imagePath);
                if (stream == null)
                    throw new FileSystemParseException("Failed to open file for reading");

                header = stream.ReadStruct<XCONTENT_HEADER>();
                header.EndianSwap();

                if (header.SignatureType != kMagicConBE && header.SignatureType != kMagicLiveBE && header.SignatureType != kMagicPirsBE)
                    throw new FileSystemParseException("File has invalid header magic");

                stream.Position = 0x344;
                metadata = stream.ReadStruct<XCONTENT_METADATA>();
                metadata.EndianSwap();
                if (metadata.StfsVolumeDescriptor.DescriptorLength != 0x24)
                    throw new FileSystemParseException("File has invalid descriptor length");

                StfsInit();

                int directoryBlock = metadata.StfsVolumeDescriptor.DirectoryFirstBlockNumber;
                var entries = new List<StfsEntry>();
                for (int i = 0; i < metadata.StfsVolumeDescriptor.DirectoryAllocationBlocks; i++)
                {
                    if (directoryBlock == 0xFFFFFF)
                    {
                        Console.WriteLine("Premature directory exit 1!!!");
                        break;
                    }

                    stream.Position = StfsDataBlockToOffset(directoryBlock);

                    bool noMoreEntries = false;
                    for (int ent = 0; ent < (0x1000 / 0x40); ent++)
                    {
                        StfsEntry entry = new StfsEntry(this);
                        if (!entry.Read(stream))
                        {
                            noMoreEntries = true;
                            break;
                        }
                        totalBytesInUse += entry.DirEntry.FileSize;
                        entries.Add(entry);
                    }


                    // Find next directory block...
                    var blockHashEntry = StfsGetLevel0HashEntry(directoryBlock);
                    directoryBlock = blockHashEntry.Level0NextBlock;

                    if (noMoreEntries)
                    {
                        if (i + 1 < metadata.StfsVolumeDescriptor.DirectoryAllocationBlocks)
                            Console.WriteLine("Premature directory exit 2!!!");
                        break;
                    }
                }

                // Connect entries up with their parents/children
                var rootEntries = new List<StfsEntry>();
                for (int i = 0; i < entries.Count; i++)
                {
                    var dir = entries[i];
                    if (dir.DirEntry.DirectoryIndex == -1)
                        rootEntries.Add(dir);

                    if (!dir.DirEntry.IsDirectory)
                        continue;

                    var children = new List<StfsEntry>();
                    foreach (var ent in entries)
                        if (ent.DirEntry.DirectoryIndex == i)
                        {
                            children.Add(ent);
                            ent.Parent = dir;
                        }

                    dir.Children = children.ToArray();
                }

                children = entries.ToArray();
                rootChildren = rootEntries.ToArray();

                FileSystemHost Host = (FileSystemHost)Host0;
                Host.SectorSize = (ushort)kSectorSize;
                Host.SectorsPerAllocationUnit = 1;
                Host.MaxComponentLength = 255;
                Host.FileInfoTimeout = 1000;
                Host.CaseSensitiveSearch = false;
                Host.CasePreservedNames = true;
                Host.UnicodeOnDisk = true;
                Host.PersistentAcls = false;
                Host.PassQueryDirectoryPattern = true;
                Host.FlushAndPurgeOnCleanup = true;
                Host.VolumeCreationTime = 0;
                Host.VolumeSerialNumber = BitConverter.ToUInt32(header.ContentId, 0);

                string type = "STFS";
                if (header.SignatureType == kMagicConBE)
                    type += " (CON)";
                else if (header.SignatureType == kMagicLiveBE)
                    type += " (LIVE)";
                else if (header.SignatureType == kMagicPirsBE)
                    type += " (PIRS)";

                Host.FileSystemName = type;

                // Hack for us to set ReadOnlyVolume flag...
                var paramsField = Host.GetType().GetField("_VolumeParams", BindingFlags.NonPublic | BindingFlags.Instance);
                object paramsVal = paramsField.GetValue(Host);
                var flagsField = paramsVal.GetType().GetField("Flags", BindingFlags.NonPublic | BindingFlags.Instance);
                uint value = (uint)flagsField.GetValue(paramsVal);
                flagsField.SetValue(paramsVal, value | 0x200);
                paramsField.SetValue(Host, paramsVal);

                return STATUS_SUCCESS;
            }
            catch(FileSystemParseException e)
            {
                stream.Close();
                return STATUS_OPEN_FAILED;
            }
        }

        void StfsInit()
        {
            // Precalculate some things
            stfsSizeOfHeaders = ((header.SizeOfHeaders + kSectorSize - 1) / kSectorSize) * kSectorSize;
            stfsBlocksPerHashTable = 1;
            stfsBlockStep[0] = 0xAB;
            stfsBlockStep[1] = 0x718F;
            if (!metadata.StfsVolumeDescriptor.ReadOnlyFormat)
            {
                stfsBlocksPerHashTable = 2;
                stfsBlockStep[0] = 0xAC;
                stfsBlockStep[1] = 0x723A;
            }
        }

        int StfsComputeBackingDataBlockNumber(int BlockNumber)
        {
            int blockBase = 0xAA;
            int block = BlockNumber;

            for (int i = 0; i < 3; i++)
            {
                block += stfsBlocksPerHashTable * ((BlockNumber + blockBase) / blockBase);
                if (BlockNumber < blockBase)
                    break;

                blockBase *= 0xAA;
            }

            return block;
        }

        int StfsComputeLevelNBackingHashBlockNumber(int blockNum, int level)
        {
            int num = 0;
            if (level == 0)
            {
                num = (blockNum / 0xAA) * stfsBlockStep[0];
                if (blockNum / 0xAA == 0)
                    return num;

                num = num + ((blockNum / 0x70E4) + 1) * stfsBlocksPerHashTable;
                if (blockNum / 0x70E4 == 0)
                    return num;
            }
            else if (level == 1)
            {
                num = (blockNum / 0x70E4) * stfsBlockStep[1];
                if (blockNum / 0x70E4 == 0)
                    return num + stfsBlockStep[0];
            }
            else
            {
                return stfsBlockStep[1];
            }
            return num + stfsBlocksPerHashTable;
        }

        long StfsBackingBlockToOffset(int BlockNumber)
        {
            return stfsSizeOfHeaders + (BlockNumber * 0x1000);
        }

        long StfsDataBlockToOffset(int BlockNumber)
        {
            return StfsBackingBlockToOffset(StfsComputeBackingDataBlockNumber(BlockNumber));
        }

        STF_HASH_ENTRY StfsGetLevelNHashEntry(int BlockNumber, int Level, ref byte[] ExpectedHash, bool UseSecondaryBlock)
        {
            int record = BlockNumber;
            for (int i = 0; i < Level; i++)
                record /= kDataBlocksPerHashLevel[0];

            record %= kDataBlocksPerHashLevel[0];

            long hashOffset = StfsBackingBlockToOffset(StfsComputeLevelNBackingHashBlockNumber(BlockNumber, Level));

            if (UseSecondaryBlock && !metadata.StfsVolumeDescriptor.ReadOnlyFormat)
                hashOffset += kSectorSize;

            bool isInvalidTable = invalidTables.Contains(hashOffset);
            if (!cachedTables.ContainsKey(hashOffset))
            {
                // Cache the table in memory, since it's likely to be needed again;
                byte[] blockData = new byte[kSectorSize];
                lock (streamLock)
                {
                    stream.Seek(hashOffset, SeekOrigin.Begin);
                    stream.Read(blockData, 0, (int)kSectorSize);
                }

                STF_HASH_BLOCK block = Utility.BytesToStruct<STF_HASH_BLOCK>(blockData);
                block.EndianSwap();
                cachedTables.Add(hashOffset, block);
                if (!isInvalidTable)
                {
                    // It's not cached and not in the invalid table array yet... lets check it
                    byte[] hash = System.Security.Cryptography.SHA1.Create().ComputeHash(blockData);
                    if (!hash.BytesMatch(ExpectedHash))
                    {
                        isInvalidTable = true;
                        invalidTables.Add(hashOffset);
                    }
                }
            }

            if (isInvalidTable)
            {
                // If table is corrupt there's no use reading invalid data, lets try
                // salvaging things by providing next block as block + 1, should work fine
                // for LIVE/PIRS packages hopefully.
                var entry2 = new STF_HASH_ENTRY();
                entry2.Level0NextBlock = BlockNumber + 1;
                return entry2;
            }

            var table = cachedTables[hashOffset];
            var entry = table.Entries[record];
            // Copy hash from entry into hash parameter...
            Array.Copy(entry.Hash, 0, ExpectedHash, 0, 0x14);
            return table.Entries[record];
        }

        STF_HASH_ENTRY StfsGetLevel0HashEntry(int BlockNumber)
        {
            bool useSecondaryBlock = false;
            // Use secondary block for root table if RootActiveIndex flag is set
            if (metadata.StfsVolumeDescriptor.RootActiveIndex)
                useSecondaryBlock = true;

            byte[] hash = new byte[0x14];
            Array.Copy(metadata.StfsVolumeDescriptor.RootHash, 0, hash, 0, 0x14);

            uint numBlocks = metadata.StfsVolumeDescriptor.NumberOfTotalBlocks;
            if (numBlocks > kDataBlocksPerHashLevel[1])
            {
                // Get the L2 entry for this block
                var l2_entry = StfsGetLevelNHashEntry(BlockNumber, 2, ref hash, useSecondaryBlock);
                useSecondaryBlock = l2_entry.LevelNActiveIndex;
            }

            if (numBlocks > kDataBlocksPerHashLevel[0])
            {
                // Get the L1 entry for this block
                var l1_entry = StfsGetLevelNHashEntry(BlockNumber, 1, ref hash, useSecondaryBlock);
                useSecondaryBlock = l1_entry.LevelNActiveIndex;
            }

            return StfsGetLevelNHashEntry(BlockNumber, 0, ref hash, useSecondaryBlock);
        }

        public int[] StfsGetDataBlockChain(int BlockNumber)
        {
            var blockList = new List<int>();
            while (BlockNumber != 0xFFFFFF)
            {
                blockList.Add(BlockNumber);
                var hashEntry = StfsGetLevel0HashEntry(BlockNumber);
                BlockNumber = hashEntry.Level0NextBlock;
            }
            return blockList.ToArray();
        }

        private short StfsFindFile(string fileName, short directoryIndex)
        {
            for (short i = 0; i < children.Length; i++)
                if (children[i].DirEntry.FileName == fileName && children[i].DirEntry.DirectoryIndex == directoryIndex)
                    return i;

            return -1;
        }

        private short StfsFindFile(string filePath)
        {
            string[] split = filePath.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            short currentDirectory = -1;

            for (int i = 0; i < split.Length; i++)
                currentDirectory = StfsFindFile(split[i], currentDirectory);

            return currentDirectory;
        }

        public static DateTime StfsDateTime(int dateTime)
        {
            if (dateTime == 0)
                return DateTime.MinValue;

            int second = (dateTime & 0x1F) << 1;
            int minute = (dateTime >> 5) & 0x3F;
            int hour = (dateTime >> 11) & 0x1F;
            int day = (dateTime >> 16) & 0x1F;
            int month = (dateTime >> 21) & 0x0F;
            int year = ((dateTime >> 25) & 0x7F) + 1980;

            try
            {
                return new DateTime(year, month, day, hour, minute, second).ToLocalTime();
            }
            catch { return DateTime.MinValue; }
        }

        class DirectoryEntryComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                return String.Compare(((StfsEntry)x).DirEntry.FileName, ((StfsEntry)y).DirEntry.FileName);
            }
        }

        static DirectoryEntryComparer _DirectoryEntryComparer = new DirectoryEntryComparer();
    }
}
