using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using Fsp;

namespace XboxWinFsp
{
    public class FatxDevice
    {
        Stream Stream;
        long DriveSize;

        CACHE_PARTITION_DATA CacheHeader;

        Object StreamLock = new object();

        static readonly TupleList<string, long, long> kOGXboxPartitions = new TupleList<string, long, long>
        {
            { "Data", 0xABE80000, 0x1312D6000 },
            { "System", 0x8CA80000, 0x1F400000 },
            { "Cache X", 0x80000, 0x2EE00000 },
            { "Cache Y", 0x2EE80000, 0x2EE00000 },
            { "Cache Z", 0x5DC80000, 0x2EE00000 },
            { "User Defined F", 0x1DD156000, 0 },
            { "User Defined G", 0x1FFFFFFE00, 0 },
        };

        static readonly TupleList<string, long, long> kXbox360Partitions = new TupleList<string, long, long>
        {
            // X360 MU
            { "Cache", 0, 0x7FF000 },
            { "Data", 0x7FF000, 0 },

            // X360 USB "XTAF" image - from github.com/landaire/Up
            // Needs verification...
            //{ "Cache",     0x08000400, 0x47FF000 }, // Should be 0114E00?
            //{ "SystemAux", 0x08115200, 0x8000000 }, // Should be 9EEB200?
            //{ "SystemExt", 0x12000400, 0xDFFFC00 },
            //{ "Data", 0x20000000, 0 }

            // X360 HDD
            { "Cache0", 0x80000, 0x80000000 },
            { "Cache1", 0x80080000, 0x80000000 },

            // Next 4 partitions are also grouped into \Device\DumpPartition (size 0x20E30000)
            //{ "System URL Cache", 0x100080000, 0xC0000 },
            //{ "Title URL Cache", 0x100140000, 0x40000 },
            // Neither of the above are FATX/STFC, seems to be a custom format for storing cached web pages
            { "SystemAux", 0x10C080000, 0xCE30000 },
            { "SystemExt", 0x118EB0000, 0x8000000 },

            { "System", 0x120EB0000, 0x10000000 },
            { "Partition1", 0x130EB0000, 0 }, // Retail only
        };

        // Devkits contain a partition table at 0x0 with sector address/size of each partition
        // I guess to allow a single HDD to have seperate retail/devkit partitions?
        // In my case at least, my XDK's HDD seems to contain partitions at the addresses in kXboxPartitions, but also has this partition table which has completely different addresses for them
        // On further investigating, my HDD does seem to contain partitions which the XDK doesn't actually access (sadly with the data mostly overwritten, but there are some directory structures there)
        static readonly List<string> kDevkitPartitionNames = new List<string>()
        {
            "Partition1",
            "System",
            "Unused0", // Unused in latest kernel, signs point to it being WindowsPartition
            "Dump", // Contains URL caches/AUX/EXT
            "PixDump",
            "Unused1", // Unused in latest kernel, might be PixStream
            "Unused2", // Unused in latest kernel, might be MemDump
            "AltFlash",
            "Cache0",
            "Cache1"
        };

        // Partitions found inside the "DumpPartition"
        static readonly TupleList<string, long, long> kDumpPartitions = new TupleList<string, long, long>
        {
            //{ "System URL Cache", 0x100080000, 0xC0000 },
            //{ "Title URL Cache", 0x100140000, 0x40000 },
            // Neither of the above are FATX/STFC, seems to be a custom format for storing cached web pages
            { "SystemAux", 0xC000000, 0xCE30000 },
            { "SystemExt", 0x18E30000, 0x8000000 },
        };

        public FatxDevice(Stream stream)
        {
            Stream = stream;
            DriveSize = stream.Length;
        }

        public bool IsFatxDevice()
        {

            if (!IsOGXboxDevice() && !IsXbox360Device())
                return false;

            Stream.Position = 0;
            var devkitTable = Stream.ReadStruct<FATX_DEVKIT_PARTITION_TABLE>();
            devkitTable.EndianSwap();
            if (devkitTable.IsValid)
                return true;

            Stream.Position = 0;
            var homebrewTable = Stream.ReadStruct<FATX_HOMEBREW_PARTITION_TABLE>(); // Homebew table is apparently Little Endian (need to confirm with a console-formatted HDD)
            if (homebrewTable.IsValid)
                return true;

            var kXboxPartitions = new TupleList<string, long, long>();
            kXboxPartitions.AddRange(kOGXboxPartitions);
            kXboxPartitions.AddRange(kXbox360Partitions);

            foreach (var partition in kXboxPartitions)
            {
                if (DriveSize <= partition.Item2)
                    continue;

                Stream.Seek(partition.Item2, SeekOrigin.Begin);
                var fatxHeader = Stream.ReadStruct<FAT_VOLUME_METADATA>();
                if (fatxHeader.IsValid)
                    return true;
            }

            return false;
        }

        public bool IsOGXboxDevice()
        {
            if (DriveSize < (0x600 + 4))
                return false;

            byte[] bytes = new byte[4];

            Stream.Position = 0x600;
            Stream.Read(bytes, 0, 4);
            if (Encoding.ASCII.GetString(bytes) == "BRFR")
                return true;

            return false;
        }

        public bool IsXbox360Device()
        {
            if (DriveSize < (0x800 + 4))
                return false;

            byte[] bytes = new byte[4];

            Stream.Position = 0x800;
            Stream.Read(bytes, 0, 4);
            if (Encoding.ASCII.GetString(bytes) == "Josh")
                return true;

            return false;
        }

        public List<ReadOnlyFileSystem> LoadPartitions(uint DebugLogLevel = 0)
        {
            if (!IsFatxDevice())
                return null;

            var filesystems = new List<ReadOnlyFileSystem>();

            List<Tuple<String, long, long>> partitions;

            bool ogXbox = IsOGXboxDevice();

            if (ogXbox)
            {
                // Filter kXboxPartitions to the ones that could fit onto this drive/image
                partitions = kOGXboxPartitions.Where(partition => partition.Item2 < DriveSize && partition.Item2 + partition.Item3 <= DriveSize).ToList();

                // Try reading in homebrew partition table
                Stream.Position = 0;
                var homebrewTable = Stream.ReadStruct<FATX_HOMEBREW_PARTITION_TABLE>();
                if (homebrewTable.IsValid)
                {
                    // Add any non-zero partitions to our partition list
                    for (int i = 0; i < homebrewTable.Partitions.Length; i++)
                    {
                        var partition = homebrewTable.Partitions[i];
                        if (partition.IsValid)
                        {
                            var tuple = new Tuple<String, long, long>(partition.Name, partition.Offset, partition.Size);
                            // Remove any overlapping partition
                            partitions.RemoveAll(p => (p.Item2 < (tuple.Item2 + tuple.Item3) && tuple.Item2 < (p.Item2 + p.Item3)));
                            partitions.RemoveAll(p => (p.Item2 == tuple.Item2 && p.Item3 == 0));
                            partitions.Add(tuple);
                        }
                    }
                }

            }
            else if (IsXbox360Device())
            {
                // Filter kXboxPartitions to the ones that could fit onto this drive/image
                partitions = kXbox360Partitions.Where(partition => partition.Item2 < DriveSize && partition.Item2 + partition.Item3 <= DriveSize).ToList();

                // Try reading in devkit partition table
                Stream.Position = 0;
                var devkitTable = Stream.ReadStruct<FATX_DEVKIT_PARTITION_TABLE>();
                devkitTable.EndianSwap();
                if (devkitTable.IsValid)
                {
                    // Add any non-zero partitions to our partition list
                    for (int i = 0; i < devkitTable.Partitions.Length; i++)
                    {
                        var partition = devkitTable.Partitions[i];

                        if (partition.Offset != 0 && partition.Size != 0)
                        {
                            var partitionName = kDevkitPartitionNames[i];
                            if (partitionName == "Dump")
                            { // Dump can contain multiple partitions, see kDumpPartitions
                                foreach (var dumpPartition in kDumpPartitions)
                                {
                                    partitions.Add(new Tuple<string, long, long>(
                                        $"DevKit {dumpPartition.Item1}", partition.Offset + dumpPartition.Item2, dumpPartition.Item3));
                                }
                            }
                            else
                            {
                                partitions.Add(new Tuple<string, long, long>(
                                    $"DevKit {kDevkitPartitionNames[i]}", partition.Offset, partition.Size));
                            }
                        }
                    }
                }

            }
            else
            {
                return null;
            }

            // Sort partitions by their offset
            partitions = partitions.OrderBy(p => p.Item2).ThenBy(p => p.Item3).ToList();

            for (int i = partitions.Count - 1; i >= 0; i--) // Iterate backward, to remove invalid partitions and detect overlaps
            {
                Stream.Seek(partitions[i].Item2, SeekOrigin.Begin);
                var header = Stream.ReadStruct<FAT_VOLUME_METADATA>();
                if (!header.IsValid)
                {
                    partitions.RemoveAt(i);
                } else if (partitions[i].Item3 == 0)    // Partition was found but we have to guess the size
                {
                    long size = 0x377FFC000; // 20GB HDD
                    if (DriveSize != 0x04AB440C00)  // 20GB HDD
                        size = DriveSize - partitions[i].Item2;

                    for (int j = partitions.Count - 1; j > i; j--)
                    {
                        size = Math.Min(size, partitions[j].Item2 - partitions[i].Item2);   // Stop at any partition found after it
                        // Please note that if the drive was previously formatted with a G partition and the FATX header was not overwritten, the F partition will be truncated
                    }
                    partitions[i] = new Tuple<string, long, long>(partitions[i].Item1, partitions[i].Item2, size);
                }
                    
            }

            // TODO: check if any partitions interfere with each other (eg. devkit Partition1 located inside retail Partition1 space), and mark the drive label if so ("CORRUPT" or something similar)

            // Read in cache partition data
            Stream.Position = 0x800;
            CacheHeader = Stream.ReadStruct<CACHE_PARTITION_DATA>();
            CacheHeader.EndianSwap();

            // Load in the filesystems & mount them:
            int stfcIndex = 0;
            int driveIndex = -1;
            foreach (var partition in partitions)
            {
                if (partition.Item3 == 0)
                    continue; // Couldn't figure out size of it ?

                driveIndex++;

                Stream.Position = partition.Item2;
                var fatx = new FatxFileSystem(Stream, partition.Item1, partition.Item2, partition.Item3, ogXbox);
                fatx.StreamLock = StreamLock;

                try
                {
                    fatx.FatxInit();
                    filesystems.Add(fatx);
                } catch (FileSystemParseException)
                {
                    // Not a FATX partition. Let's try STFS
                    Stream.Position = partition.Item2;
                    var stfs = new StfsFileSystem(Stream, partition.Item1, partition.Item2);
                    stfs.StreamLock = StreamLock;
                    stfs.SkipHashChecks = true; // TODO!
                    if (stfcIndex < 2 && CacheHeader.IsValid)
                    {
                        stfs.CacheHeader = CacheHeader;
                        stfs.CachePartitionIndex = stfcIndex;
                        stfs.StfsVolumeDescriptor = CacheHeader.VolumeDescriptor[stfcIndex];
                    }

                    stfcIndex++;
                    if (stfcIndex >= 2)
                        stfcIndex = 0; // Reset index for devkit cache partitions

                    try
                    {
                        stfs.StfsInit();
                        filesystems.Add(stfs);
                    }
                    catch (FileSystemParseException)
                    { }

                }
                
            }

            return filesystems;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct FATX_PARTITION_BOUNDS
    {
        public uint SectorNumber;
        public uint NumSectors;

        public long Offset
        {
            get
            {
                return (long)SectorNumber * FatxFileSystem.kSectorSize;
            }
        }

        public long Size
        {
            get
            {
                return (long)NumSectors * FatxFileSystem.kSectorSize;
            }
        }

        public void EndianSwap()
        {
            SectorNumber = SectorNumber.EndianSwap();
            NumSectors = NumSectors.EndianSwap();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct FATX_DEVKIT_PARTITION_TABLE // only on devkits :(
    {
        public ushort RecoveryVersionMajor;
        public ushort RecoveryVersionMinor;
        public uint RecoveryVersionBuildQFE;

        public bool IsValid
        {
            get
            {
                return RecoveryVersionMajor == 2 && RecoveryVersionBuildQFE >= 0x5F50001;
            }
        }

        // Kernel reads 10 partitions from here
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)]
        public FATX_PARTITION_BOUNDS[] Partitions;

        public void EndianSwap()
        {
            RecoveryVersionMajor = RecoveryVersionMajor.EndianSwap();
            RecoveryVersionMinor = RecoveryVersionMinor.EndianSwap();
            RecoveryVersionBuildQFE = RecoveryVersionBuildQFE.EndianSwap();
            for (int i = 0; i < 10; i++)
                Partitions[i].EndianSwap();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct FATX_HOMEBREW_PARTITION_ENTRY
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] NameBytes;
        public uint Flags;
        public uint LBAStart;
        public uint LBASize;
        public uint Reserved;   // Usually all zeroes

        public String Name
        {
            get
            {
                return Encoding.ASCII.GetString(NameBytes).Trim();
            }
        }

        public long Offset
        {
            get
            {
                return (long)LBAStart * FatxFileSystem.kSectorSize;
            }
        }

        public long Size
        {
            get
            {
                return (long)LBASize * FatxFileSystem.kSectorSize;
            }
        }

        public bool IsValid
        {
            get
            {
                // 0x400 is the address of the first partition
                // 0x80000000 is a flag indicating that the partition is in use
                return (LBAStart >= 0x400) && (LBASize > 0) && ((Flags & 0x80000000) != 0);
            }
        }

    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct FATX_HOMEBREW_PARTITION_TABLE // As defined by XBP Table Writer
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] MagicBytes;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Res0; // Reserved bytes, always 0
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 14)]
        public FATX_HOMEBREW_PARTITION_ENTRY[] Partitions;

        public String Magic
        {
            get
            {
                return Encoding.ASCII.GetString(MagicBytes).Trim();
            }
        }

        public bool IsValid
        {
            get
            {
                return Magic == "****PARTINFO****";
            }
        }

    }

}
