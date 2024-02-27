﻿using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Runtime.InteropServices;

using Fsp;

// TODO:
// - add invalid tables to metadata.ini
namespace XboxWinFsp
{
    public class StfsFileSystem : ReadOnlyFileSystem
    {
        public const int kSectorSize = 0x1000;
        public static readonly char[] kInvalidFilenameChars = new[] { '>', '<', '=', '?', ':', ';', '"', '*', '+', ',', '/', '\\', '|' };
        static readonly int[] kDataBlocksPerHashLevel = new int[] { 0xAA, 0x70E4, 0x4AF768 };

        public bool SkipHashChecks = false;

        // If this is a cache partition, the cache header can be set here
        // This'll allow the metadata.ini to present the info from it
        public CACHE_PARTITION_DATA CacheHeader;
        public int CachePartitionIndex = -1;

        public STF_VOLUME_DESCRIPTOR StfsVolumeDescriptor;

        // Position of start of this FS
        long Position = 0;

        XCONTENT_HEADER Header;
        XCONTENT_METADATA Metadata;
        XCONTENT_METADATA_INSTALLER InstallerMetadata;

        PEC_HEADER PecHeader;
        bool IsXContent = false;

        XE_CONSOLE_SIGNATURE ConsoleSignature;
        bool IsConsoleSigned = false;

        // All files in the package
        FileEntry[] Children;

        // The earliest CreationTime in all the file entries
        DateTime CreationTime = DateTime.Now;

        // Values used in some block calculations, inited by StfsInit();
        long SizeOfHeaders = 0;
        int BlocksPerHashTable = 1;
        int[] StfsBlockStep = new[] { 0xAB, 0x718F };

        // Cached hash blocks
        List<long> InvalidTables = new List<long>();
        Dictionary<long, STF_HASH_BLOCK> CachedTables = new Dictionary<long, STF_HASH_BLOCK>();

        // Misc
        SHA1 Sha1 = SHA1.Create();

        public StfsFileSystem(Stream stream, string inputPath, long partitionOffset = 0) : base(stream, inputPath, kSectorSize)
        {
            Position = partitionOffset;
        }

        public void StfsInit()
        {
            if (Position == 0)
                Position = Stream.Position;

            // Set ConsoleSignature if this is a cache partition, so metadata.ini can reflect it
            if (CacheHeader.IsValid && CachePartitionIndex != -1)
            {
                IsConsoleSigned = true;
                ConsoleSignature = CacheHeader.Signature;
            }

            // Read in XContent/PEC header if the volume descriptor isn't already set:
            if (StfsVolumeDescriptor.DescriptorLength != 0x24)
            {
                var fatHeader = Stream.ReadStruct<FAT_VOLUME_METADATA>();
                Stream.Position = Position;
                if (fatHeader.Signature == FatxFileSystem.kMagicFatx || fatHeader.Signature == FatxFileSystem.kMagicFatxBE)
                {
                    // STFC partition (uses FATX header for some reason, but has an invalid chainmap)
                    // TODO: find where the descriptor is located (the HDD dump I'm testing with might be missing the first cluster)
                    // For now we'll disable hash checks & use a default descriptor...
                    StfsVolumeDescriptor = STF_VOLUME_DESCRIPTOR.Default();
                    // Should be fine to set directory block count to 100, it'll exit out once the hash chain ends
                    StfsVolumeDescriptor.DirectoryAllocationBlocks = 100;
                    // We don't have a volume descriptor, so we don't have a root hash: disable hash checks ;_;
                    SkipHashChecks = true;
                }
                else
                {
                    PecHeader = Stream.ReadStruct<PEC_HEADER>();
                    PecHeader.EndianSwap();
                    if (PecHeader.ConsoleSignature.IsStructureValid)
                    {
                        IsXContent = false;
                        IsConsoleSigned = true;
                        ConsoleSignature = PecHeader.ConsoleSignature;
                        StfsVolumeDescriptor = PecHeader.StfsVolumeDescriptor;
                    }
                    else
                    {
                        IsXContent = true;
                        Stream.Seek(0, SeekOrigin.Begin);

                        Header = Stream.ReadStruct<XCONTENT_HEADER>();
                        Header.EndianSwap();

                        if (Header.SignatureType != XCONTENT_HEADER.kSignatureTypeConBE &&
                            Header.SignatureType != XCONTENT_HEADER.kSignatureTypeLiveBE &&
                            Header.SignatureType != XCONTENT_HEADER.kSignatureTypePirsBE)
                            throw new FileSystemParseException("File has invalid header magic");

                        if (Header.SizeOfHeaders == 0)
                            throw new FileSystemParseException("Package doesn't contain STFS filesystem");

                        if (Header.SignatureType == XCONTENT_HEADER.kSignatureTypeConBE)
                        {
                            IsConsoleSigned = true;
                            ConsoleSignature = Header.ConsoleSignature;
                        }

                        Stream.Position = 0x344;
                        Metadata = Stream.ReadStruct<XCONTENT_METADATA>();
                        Metadata.EndianSwap();

                        if (Header.SizeOfHeaders > 0x971A)
                        {
                            Stream.Position = 0x971A;
                            InstallerMetadata = Stream.ReadStruct<XCONTENT_METADATA_INSTALLER>();
                            InstallerMetadata.EndianSwap();
                        }

                        if (Metadata.VolumeType != 0)
                            throw new FileSystemParseException("Package contains unsupported SVOD filesystem");

                        StfsVolumeDescriptor = Metadata.StfsVolumeDescriptor;
                    }
                }

                if (StfsVolumeDescriptor.DescriptorLength != 0x24)
                    throw new FileSystemParseException("File has invalid descriptor length");
            }
            StfsInitValues();

            // Read in our directory entries...

            int directoryBlock = StfsVolumeDescriptor.DirectoryFirstBlockNumber;
            var entries = new List<FileEntry>();
            for (int i = 0; i < StfsVolumeDescriptor.DirectoryAllocationBlocks; i++)
            {
                if (directoryBlock == 0xFFFFFF)
                {
                    Console.WriteLine("Premature directory exit 1!!!");
                    break;
                }

                Stream.Position = StfsDataBlockToOffset(directoryBlock);

                bool noMoreEntries = false;
                for (int ent = 0; ent < (0x1000 / 0x40); ent++)
                {
                    var entry = new FileEntry(this);
                    if (!entry.Read(Stream))
                    {
                        noMoreEntries = true;
                        break;
                    }
                    if (entry.CreationTime < CreationTime)
                        CreationTime = entry.CreationTime;

                    if (!entry.IsDirectory)
                        BytesInUse += entry.DirEntry.FileSize;

                    entries.Add(entry);
                }


                // Find next directory block...
                var blockHashEntry = StfsGetLevel0HashEntry(directoryBlock);
                directoryBlock = blockHashEntry.Level0NextBlock;

                if (noMoreEntries)
                {
                    if (i + 1 < StfsVolumeDescriptor.DirectoryAllocationBlocks)
                        Console.WriteLine("Premature directory exit 2!!!");
                    break;
                }
            }

            // Create metadata.ini/metadata_thumbnail/etc..
            InitMetadataFiles(ref entries);

            // Connect entries up with their parents/children
            var rootEntries = new List<IFileEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                var ent = entries[i];
                if (ent.DirEntry.DirectoryIndex == -1)
                    rootEntries.Add(ent);

                if (!ent.IsDirectory)
                    continue;

                var children = new List<IFileEntry>();
                foreach (var ent2 in entries)
                    if (ent2.DirEntry.DirectoryIndex == i)
                    {
                        children.Add(ent2);
                        ent2.Parent = ent;
                    }

                children.Sort((x, y) => x.Name.CompareTo(y.Name));
                ent.Children = children;
            }

            // Make sure to sort so that ReadDirectoryEntry doesn't make windows loop forever...
            rootEntries.Sort((x, y) => x.Name.CompareTo(y.Name));

            Children = entries.ToArray();
            RootFiles = rootEntries;
        }

        // Creates some fake metadata entries at root of FS
        void InitMetadataFiles(ref List<FileEntry> entries)
        {
            var curTime = DateTime.Now;

            // metadata.ini
            var fakeEntry = new FileEntry(this);
            fakeEntry.DirEntry.FileName = "metadata.ini";
            fakeEntry.DirEntry.DirectoryIndex = -1;
            fakeEntry.CreationTime = curTime;
            fakeEntry.LastWriteTime = curTime;
            fakeEntry.FakeData = new MemoryStream();
            var writer = new StreamWriter(fakeEntry.FakeData);
            if (IsConsoleSigned)
            {
                writer.WriteLine("[ConsoleSignature]");
                writer.WriteLine($"ConsoleId = {BitConverter.ToString(ConsoleSignature.Cert.ConsoleId)}");
                writer.WriteLine($"ConsolePartNumber = {ConsoleSignature.Cert.ConsolePartNumber}");
                writer.WriteLine($"Privileges = 0x{ConsoleSignature.Cert.Privileges:X}");
                writer.WriteLine($"ConsoleType = 0x{ConsoleSignature.Cert.ConsoleType:X8} ({ConsoleSignature.Cert.ConsoleTypeString})");
                writer.WriteLine($"ManufacturingDate = {ConsoleSignature.Cert.ManufacturingDate}");

                writer.WriteLine();
            }
            if (IsXContent)
            {
                writer.WriteLine("[ExecutionId]");

                if (Metadata.ExecutionId.MediaId != 0)
                    writer.WriteLine($"MediaId = 0x{Metadata.ExecutionId.MediaId:X8}");
                if (Metadata.ExecutionId.Version.IsValid)
                    writer.WriteLine($"Version = v{Metadata.ExecutionId.Version}");
                if (Metadata.ExecutionId.BaseVersion.IsValid)
                    writer.WriteLine($"BaseVersion = v{Metadata.ExecutionId.BaseVersion}");
                if (Metadata.ExecutionId.TitleId != 0)
                    writer.WriteLine($"TitleId = 0x{Metadata.ExecutionId.TitleId:X8}");
                writer.WriteLine($"Platform = {Metadata.ExecutionId.Platform}");
                writer.WriteLine($"ExecutableType = {Metadata.ExecutionId.ExecutableType}");
                writer.WriteLine($"DiscNum = {Metadata.ExecutionId.DiscNum}");
                writer.WriteLine($"DiscsInSet = {Metadata.ExecutionId.DiscsInSet}");
                if (Metadata.ExecutionId.SaveGameId != 0)
                    writer.WriteLine($"SaveGameId = 0x{Metadata.ExecutionId.SaveGameId:X8}");

                writer.WriteLine();
                writer.WriteLine("[XContentHeader]");
                writer.WriteLine($"SignatureType = {Header.SignatureTypeString}");
                writer.WriteLine($"ContentId = {BitConverter.ToString(Header.ContentId)}");
                writer.WriteLine($"SizeOfHeaders = 0x{Header.SizeOfHeaders:X}");

                for (int i = 0; i < Header.LicenseDescriptors.Length; i++)
                {
                    var license = Header.LicenseDescriptors[i];
                    if (!license.IsValid)
                        continue;

                    writer.WriteLine();
                    writer.WriteLine($"[XContentLicensee{i}]");
                    writer.WriteLine($"LicenseeId = 0x{license.LicenseeId:X16} ({license.LicenseType})");
                    writer.WriteLine($"LicenseBits = 0x{license.LicenseBits:X8}");
                    writer.WriteLine($"LicenseFlags = 0x{license.LicenseFlags:X8}");
                }

                writer.WriteLine();
                writer.WriteLine("[XContentMetadata]");
                writer.WriteLine($"ContentType = 0x{Metadata.ContentType:X8}");
                writer.WriteLine($"ContentMetadataVersion = {Metadata.ContentMetadataVersion}");
                writer.WriteLine($"ContentSize = 0x{Metadata.ContentSize:X}");
                if (!Metadata.ConsoleId.IsNull())
                    writer.WriteLine($"ConsoleId = {BitConverter.ToString(Metadata.ConsoleId)}");
                if (Metadata.Creator != 0)
                    writer.WriteLine($"Creator = 0x{Metadata.Creator:X16}");
                if (Metadata.OnlineCreator != 0)
                    writer.WriteLine($"OnlineCreator = 0x{Metadata.OnlineCreator:X16}");
                if (Metadata.Category != 0)
                    writer.WriteLine($"Category = {Metadata.Category}");
                if (!Metadata.DeviceId.IsNull())
                    writer.WriteLine($"DeviceId = {BitConverter.ToString(Metadata.DeviceId)}");

                for (int i = 0; i < 9; i++)
                    if (!string.IsNullOrEmpty(Metadata.DisplayName[i].String))
                        writer.WriteLine($"DisplayName[{Utility.XboxLanguages[i]}] = {Metadata.DisplayName[i].String}");
                if (Metadata.ContentMetadataVersion >= 2)
                    for (int i = 0; i < 3; i++)
                        if (!string.IsNullOrEmpty(Metadata.DisplayNameEx[i].String))
                            writer.WriteLine($"DisplayNameEx[{Utility.XboxLanguages[i+9]}] = {Metadata.DisplayNameEx[i].String}");
                for (int i = 0; i < 9; i++)
                    if (!string.IsNullOrEmpty(Metadata.Description[i].String))
                        writer.WriteLine($"Description[{Utility.XboxLanguages[i]}] = {Metadata.Description[i].String}");
                if (Metadata.ContentMetadataVersion >= 2)
                    for (int i = 0; i < 3; i++)
                        if (!string.IsNullOrEmpty(Metadata.DescriptionEx[i].String))
                            writer.WriteLine($"DescriptionEx[{Utility.XboxLanguages[i+9]}] = {Metadata.DescriptionEx[i].String}");

                if (!string.IsNullOrEmpty(Metadata.Publisher.String))
                    writer.WriteLine($"Publisher = {Metadata.Publisher.String}");
                if (!string.IsNullOrEmpty(Metadata.TitleName.String))
                    writer.WriteLine($"TitleName = {Metadata.TitleName.String}");

                if (Metadata.FlagsAsBYTE != 0)
                    writer.WriteLine($"Flags = 0x{Metadata.FlagsAsBYTE:X2}");

                writer.WriteLine($"ThumbnailSize = 0x{Metadata.ThumbnailSize:X}");
                writer.WriteLine($"TitleThumbnailSize = 0x{Metadata.TitleThumbnailSize:X}");

                if (InstallerMetadata.IsValid)
                {
                    writer.WriteLine();
                    writer.WriteLine("[XContentMetadataInstaller]");

                    string type = "";
                    if (InstallerMetadata.IsSystemUpdate)
                        type = " (SystemUpdate)";
                    else if (InstallerMetadata.IsTitleUpdate)
                        type = " (TitleUpdate)";
                    writer.WriteLine($"MetaDataType = 0x{InstallerMetadata.MetaDataType:X8}{type}");

                    if (InstallerMetadata.CurrentVersion.IsValid)
                        writer.WriteLine($"CurrentVersion = v{InstallerMetadata.CurrentVersion}");
                    if (InstallerMetadata.NewVersion.IsValid)
                        writer.WriteLine($"NewVersion = v{InstallerMetadata.NewVersion}");
                }
            }
            else
            {
                if (CachePartitionIndex != -1 && CacheHeader.IsValid)
                {
                    writer.WriteLine("[CachePartitionData]");
                    writer.WriteLine($"CacheSignature = 0x{CacheHeader.CacheSignature:X}");
                    writer.WriteLine($"Version = {CacheHeader.Version}");
                    writer.WriteLine($"LastUsedIndex = {CacheHeader.LastUsedIndex}");
                    writer.WriteLine($"PartitionIndex = {CachePartitionIndex}");
                    writer.WriteLine($"TitleID[Partition0] = 0x{CacheHeader.TitleID[0]:X8}");
                    writer.WriteLine($"TitleID[Partition1] = 0x{CacheHeader.TitleID[1]:X8}");
                }
                else if (PecHeader.ContentId != null)
                {
                    writer.WriteLine("[PECHeader]");
                    writer.WriteLine($"ContentId = {BitConverter.ToString(PecHeader.ContentId)}");
                    if (PecHeader.Unknown != 0)
                        writer.WriteLine($"Unknown = 0x{PecHeader.Unknown:X16}");
                    if (PecHeader.Unknown2 != 0)
                        writer.WriteLine($"Unknown2 = 0x{PecHeader.Unknown2:X8}");
                    if (PecHeader.Creator != 0)
                        writer.WriteLine($"Creator = 0x{PecHeader.Creator:X16}");
                    if (PecHeader.ConsoleIdsCount != 0)
                        writer.WriteLine($"ConsoleIdsCount = {PecHeader.ConsoleIdsCount}");
                    for (int i = 0; i < 100; i++)
                        if (!PecHeader.ConsoleIds[i].Bytes.IsNull())
                            writer.WriteLine($"ConsoleId[{i}] = {BitConverter.ToString(PecHeader.ConsoleIds[i].Bytes)}");
                }
            }

            writer.WriteLine();
            writer.WriteLine("[VolumeDescriptor]");
            string volumeType = (!IsXContent || Metadata.VolumeType == 0) ? "STFS" : "SVOD";
            if (IsXContent)
            {
                writer.WriteLine($"VolumeType = {Metadata.VolumeType} ({volumeType})");
                if (Metadata.DataFiles != 0)
                    writer.WriteLine($"DataFiles = {Metadata.DataFiles}");
                if (Metadata.DataFilesSize != 0)
                    writer.WriteLine($"DataFilesSize = 0x{Metadata.DataFilesSize:X}");
            }
            if (!IsXContent || Metadata.VolumeType == 0)
            {
                string flags = "";
                if (StfsVolumeDescriptor.ReadOnlyFormat)
                    flags += "(ReadOnlyFormat) ";
                if (StfsVolumeDescriptor.RootActiveIndex)
                    flags += "(RootActiveIndex) ";
                writer.WriteLine($"Stfs.DescriptorLength = 0x{StfsVolumeDescriptor.DescriptorLength:X}");
                writer.WriteLine($"Stfs.Version = {StfsVolumeDescriptor.Version}");
                writer.WriteLine($"Stfs.Flags = {StfsVolumeDescriptor.Flags} {flags}");
                writer.WriteLine($"Stfs.DirectoryAllocationBlocks = 0x{StfsVolumeDescriptor.DirectoryAllocationBlocks:X}");
                writer.WriteLine($"Stfs.DirectoryFirstBlockNumber = 0x{StfsVolumeDescriptor.DirectoryFirstBlockNumber:X}");
                writer.WriteLine($"Stfs.RootHash = {BitConverter.ToString(StfsVolumeDescriptor.RootHash)}");
                writer.WriteLine($"Stfs.NumberOfTotalBlocks = 0x{StfsVolumeDescriptor.NumberOfTotalBlocks:X}");
                writer.WriteLine($"Stfs.NumberOfFreeBlocks = 0x{StfsVolumeDescriptor.NumberOfFreeBlocks:X}");
            }
            writer.Flush();

            fakeEntry.DirEntry.FileSize = (uint)fakeEntry.FakeData.Length;
            entries.Add(fakeEntry);

            if (IsXContent)
            {
                // metadata_thumbnail.png
                if (Metadata.ThumbnailSize > 0)
                {
                    // Don't read more than 0x3D00 bytes - some older ones can be 0x4000, but too bad for them
                    var thumbSize = Math.Min(Metadata.ThumbnailSize, 0x3D00);
                    var thumbEntry = new FileEntry(this);
                    thumbEntry.DirEntry.FileName = "metadata_thumbnail.png";
                    thumbEntry.DirEntry.DirectoryIndex = -1;
                    thumbEntry.CreationTime = curTime;
                    thumbEntry.LastWriteTime = curTime;
                    thumbEntry.FakeData = new MemoryStream();
                    thumbEntry.FakeData.Write(Metadata.Thumbnail, 0, (int)thumbSize);
                    thumbEntry.FakeData.Flush();
                    thumbEntry.DirEntry.FileSize = (uint)thumbEntry.FakeData.Length;
                    entries.Add(thumbEntry);
                }

                // metadata_thumbnail_title.png
                if (Metadata.TitleThumbnailSize > 0)
                {
                    // Don't read more than 0x3D00 bytes - some older ones can be 0x4000, but too bad for them
                    var thumbSize = Math.Min(Metadata.TitleThumbnailSize, 0x3D00);
                    var thumbEntry = new FileEntry(this);
                    thumbEntry.DirEntry.FileName = "metadata_thumbnail_title.png";
                    thumbEntry.DirEntry.DirectoryIndex = -1;
                    thumbEntry.CreationTime = curTime;
                    thumbEntry.LastWriteTime = curTime;
                    thumbEntry.FakeData = new MemoryStream();
                    thumbEntry.FakeData.Write(Metadata.TitleThumbnail, 0, (int)thumbSize);
                    thumbEntry.FakeData.Flush();
                    thumbEntry.DirEntry.FileSize = (uint)thumbEntry.FakeData.Length;
                    entries.Add(thumbEntry);
                }
            }
        }

        // Precalculates some things
        void StfsInitValues()
        {
            if (IsXContent)
                SizeOfHeaders = ((Header.SizeOfHeaders + kSectorSize - 1) / kSectorSize) * kSectorSize;
            else
                SizeOfHeaders = 0x1000; // PEC

            BlocksPerHashTable = 1;
            StfsBlockStep[0] = 0xAB;
            StfsBlockStep[1] = 0x718F;
            if (!StfsVolumeDescriptor.ReadOnlyFormat)
            {
                BlocksPerHashTable = 2;
                StfsBlockStep[0] = 0xAC;
                StfsBlockStep[1] = 0x723A;
            }
        }

        int StfsComputeBackingDataBlockNumber(int BlockNumber)
        {
            int blockBase = 0xAA;
            int block = BlockNumber;

            for (int i = 0; i < 3; i++)
            {
                block += BlocksPerHashTable * ((BlockNumber + blockBase) / blockBase);
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
                num = (blockNum / 0xAA) * StfsBlockStep[0];
                if (blockNum / 0xAA == 0)
                    return num;

                num = num + ((blockNum / 0x70E4) + 1) * BlocksPerHashTable;
                if (blockNum / 0x70E4 == 0)
                    return num;
            }
            else if (level == 1)
            {
                num = (blockNum / 0x70E4) * StfsBlockStep[1];
                if (blockNum / 0x70E4 == 0)
                    return num + StfsBlockStep[0];
            }
            else
            {
                return StfsBlockStep[1];
            }
            return num + BlocksPerHashTable;
        }

        long StfsBackingBlockToOffset(int BlockNumber)
        {
            return Position + SizeOfHeaders + (BlockNumber * 0x1000);
        }

        long StfsDataBlockToOffset(int BlockNumber)
        {
            return StfsBackingBlockToOffset(StfsComputeBackingDataBlockNumber(BlockNumber));
        }

        STF_HASH_ENTRY StfsGetLevelNHashEntry(int BlockNumber, int Level, ref byte[] ExpectedHash, bool UseSecondaryBlock)
        {
            int record = BlockNumber;
            if (Level > 0)
                record /= kDataBlocksPerHashLevel[Level - 1];

            record %= kDataBlocksPerHashLevel[0];

            long hashOffset = StfsBackingBlockToOffset(StfsComputeLevelNBackingHashBlockNumber(BlockNumber, Level));

            if (UseSecondaryBlock && !StfsVolumeDescriptor.ReadOnlyFormat)
                hashOffset += kSectorSize;

            bool isInvalidTable = InvalidTables.Contains(hashOffset);
            if (!CachedTables.ContainsKey(hashOffset))
            {
                // Cache the table in memory, since it's likely to be needed again
                byte[] block = new byte[kSectorSize];
                lock (StreamLock)
                {
                    Stream.Seek(hashOffset, SeekOrigin.Begin);
                    Stream.Read(block, 0, (int)kSectorSize);
                }

                var hashBlock = Utility.BytesToStruct<STF_HASH_BLOCK>(block);
                hashBlock.EndianSwap();
                CachedTables.Add(hashOffset, hashBlock);

                if (!isInvalidTable && !SkipHashChecks)
                {
                    // It's not cached and not in the invalid table array yet... lets check it
                    byte[] hash;
                    lock(Sha1)
                        hash = Sha1.ComputeHash(block);

                    if (!hash.BytesMatch(ExpectedHash))
                    {
                        isInvalidTable = true;
                        InvalidTables.Add(hashOffset);
                        Console.WriteLine($"Invalid hash table at 0x{hashOffset:X}!");
                    }
                }
            }

            if (isInvalidTable)
            {
                // If table is corrupt there's no use reading invalid data
                // Lets try salvaging things by providing next block as block + 1
                // (Should work fine for LIVE/PIRS packages hopefully)
                var entry2 = new STF_HASH_ENTRY();
                entry2.Level0NextBlock = BlockNumber + 1;
                return entry2;
            }

            var table = CachedTables[hashOffset];
            var entry = table.Entries[record];
            // Copy hash from entry into hash parameter...
            Array.Copy(entry.Hash, 0, ExpectedHash, 0, 0x14);
            return table.Entries[record];
        }

        STF_HASH_ENTRY StfsGetLevel0HashEntry(int BlockNumber)
        {
            bool useSecondaryBlock = false;
            // Use secondary block for root table if RootActiveIndex flag is set
            if (StfsVolumeDescriptor.RootActiveIndex)
                useSecondaryBlock = true;

            byte[] hash = new byte[0x14];
            Array.Copy(StfsVolumeDescriptor.RootHash, 0, hash, 0, 0x14);

            uint numBlocks = StfsVolumeDescriptor.NumberOfTotalBlocks;
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

        public override Int32 Init(Object Host0)
        {
            try
            {
                StfsInit();

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
                try
                {
                    Host.VolumeCreationTime = (ulong)CreationTime.ToFileTimeUtc();
                }
                catch
                {
                    Host.VolumeCreationTime = 0;
                }

                if (IsXContent)
                {
                    Host.VolumeSerialNumber = BitConverter.ToUInt32(Header.ContentId, 0);
                    Host.FileSystemName = $"STFS ({Header.SignatureTypeString})";

                    // Update volume label if we have one, otherwise it defaults to input filename
                    if (!string.IsNullOrEmpty(Metadata.DisplayName[0].String))
                        VolumeLabel = Metadata.DisplayName[0].String;
                    else if (!string.IsNullOrEmpty(Metadata.TitleName.String))
                        VolumeLabel = Metadata.TitleName.String;
                }
                else
                {
                    if (PecHeader.ContentId != null)
                    {
                        Host.VolumeSerialNumber = BitConverter.ToUInt32(PecHeader.ContentId, 0);
                        Host.FileSystemName = $"STFS (PEC)";
                    }
                    else
                    {
                        Host.FileSystemName = $"STFS (STFC)";
                    }
                }

                return base.Init(Host0);
            }
            catch (FileSystemParseException)
            {
                return STATUS_OPEN_FAILED;
            }
        }

        // Info about a file stored inside the STFS image
        protected class FileEntry : IFileEntry
        {
            StfsFileSystem FileSystem;
            public STF_DIRECTORY_ENTRY DirEntry;

            internal int[] BlockChain; // Chain gets read in once a FileDesc is created for the entry
            public Stream FakeData; // Allows us to inject custom data into the filesystem, eg. for a fake metadata.ini file.

            public string Name
            {
                get
                {
                    return DirEntry.FileName;
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

            public DateTime CreationTime
            {
                get
                {
                    return DirEntry.CreationTime;
                }
                set
                {
                    DirEntry.CreationTime = value;
                }
            }

            public DateTime LastWriteTime
            {
                get
                {
                    return DirEntry.LastWriteTime;
                }
                set
                {
                    DirEntry.LastWriteTime = value;
                }
            }

            public DateTime LastAccessTime
            {
                get
                {
                    return LastWriteTime;
                }
                set { throw new NotImplementedException(); }
            }

            public List<IFileEntry> Children { get; set; }
            public IFileEntry Parent { get; set; }

            public FileEntry(StfsFileSystem fileSystem)
            {
                FileSystem = fileSystem;
            }

            public bool Read(Stream stream)
            {
                DirEntry = stream.ReadStruct<STF_DIRECTORY_ENTRY>();
                DirEntry.EndianSwap();
                return DirEntry.IsValid;
            }

            public override string ToString()
            {
                return $"{DirEntry.FileName}" + (Children != null ? $" ({Children.Count} children)" : "");
            }

            public uint ReadBytes(IntPtr buffer, ulong fileOffset, uint length)
            {
                if (fileOffset >= Size)
                    return 0;

                if (fileOffset + length >= Size)
                    length = (uint)(Size - fileOffset);

                if (FakeData != null)
                {
                    byte[] bytes2 = new byte[length];
                    int read = 0;
                    lock (FakeData)
                    {
                        FakeData.Seek((long)fileOffset, SeekOrigin.Begin);
                        read = FakeData.Read(bytes2, 0, bytes2.Length);
                    }
                    Marshal.Copy(bytes2, 0, buffer, read);
                    return (uint)read;
                }

                // Lock so that two threads can't try updating chain at once...
                lock (this)
                    if (BlockChain == null)
                        BlockChain = FileSystem.StfsGetDataBlockChain(DirEntry.FirstBlockNumber);

                uint chainNum = (uint)(fileOffset / kSectorSize);
                uint blockOffset = (uint)(fileOffset % kSectorSize);

                uint blockRemaining = kSectorSize - blockOffset;
                uint lengthRemaining = length;
                uint transferred = 0;

                byte[] bytes = new byte[kSectorSize];
                while (lengthRemaining > 0)
                {
                    var blockNum = BlockChain[chainNum];

                    uint toRead = blockRemaining;
                    if (toRead > lengthRemaining)
                        toRead = lengthRemaining;

                    int read = 0;
                    lock (FileSystem.StreamLock)
                    {
                        FileSystem.Stream.Seek((long)FileSystem.StfsDataBlockToOffset(blockNum) + blockOffset, SeekOrigin.Begin);
                        read = FileSystem.Stream.Read(bytes, 0, (int)toRead);
                    }

                    Marshal.Copy(bytes, 0, buffer, read);
                    transferred += (uint)read;

                    if (blockOffset + read >= kSectorSize)
                        chainNum++;

                    buffer += read;
                    blockRemaining = kSectorSize;
                    blockOffset = 0;
                    lengthRemaining -= (uint)read;
                }
                return transferred;
            }
        }
    }
}
