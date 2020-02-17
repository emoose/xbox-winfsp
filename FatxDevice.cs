using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.AccessControl;
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

        static readonly TupleList<string, long, long> kXboxPartitions = new TupleList<string, long, long>
        {
            // X360 MU
            { "Cache", 0, 0x7FF000 },
            { "Data", 0x7FF000, 0 },

            // Xbox OG HDD
            // (commented for now, until I can confirm that it works...)
            /*
            { "Data", 0xABE80000, 0x1312D6000 },
            { "System", 0x8CA80000, 0x1F400000 },
            { "Cache X", 0x80000, 0x2EE00000 },
            { "Cache Y", 0x2EE80000, 0x2EE00000 },
            { "Cache Z", 0x5DC80000, 0x2EE00000 },
            { "UserDefined", 0x1DD156000, 0 },*/

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

        // For loading from a physical drive
        public FatxDevice(int physicalDeviceNum)
        {
            var sfh = Natives.CreateFile(GetPhysicalDevicePath(physicalDeviceNum),
                EFileAccess.GenericRead | EFileAccess.GenericAll,
                EFileShare.Read | EFileShare.Write,
                IntPtr.Zero, ECreationDisposition.OpenExisting,
                EFileAttributes.Normal, IntPtr.Zero);

            // Get our device size
            uint high = 0;
            uint low = Natives.GetFileSize(sfh, ref high);
            DriveSize = (high << 32) | low;

            if (DriveSize == 0xffffffff)
                DriveSize = Natives.GetDriveSize(sfh);

            // Create a stream to read with
            Stream = new FileStream(sfh, FileAccess.ReadWrite, 0x200 * 0x200, false);
            Stream = new AlignedStream(Stream, 0x200 * 0x200);
        }

        // For loading from a drive backup
        public FatxDevice(Stream stream)
        {
            Stream = stream;
            DriveSize = stream.Length;
        }

        public static string GetPhysicalDevicePath(int deviceNum)
        {
            return string.Format(@"\\.\PhysicalDrive{0:D}", deviceNum);
        }

        public bool IsFatxDevice()
        {
            Stream.Position = 0;
            var devkitTable = Stream.ReadStruct<FATX_DEVKIT_PARTITION_TABLE>();
            devkitTable.EndianSwap();
            if (devkitTable.IsValid)
                return true;
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

        public List<FileSystemHost> LoadPartitions(uint DebugLogLevel = 0)
        {
            if (!IsFatxDevice())
                return null;

            var filesystems = new List<FileSystemHost>();

            // Filter kXboxPartitions to the ones that could fit onto this drive/image
            var partitions = kXboxPartitions.Where(partition => partition.Item2 < DriveSize && partition.Item2 + partition.Item3 <= DriveSize).ToList();

            // Try reading in devkit partition table
            Stream.Position = 0;
            var devkitTable = Stream.ReadStruct<FATX_DEVKIT_PARTITION_TABLE>();
            devkitTable.EndianSwap();
            if (devkitTable.IsValid)
            {
                // Add any non-zero partitions to our partition list
                for(int i = 0; i < devkitTable.Partitions.Length; i++)
                {
                    var partition = devkitTable.Partitions[i];

                    if(partition.Offset != 0 && partition.Size != 0)
                    {
                        var partitionName = kDevkitPartitionNames[i];
                        if(partitionName == "Dump") // Dump can contain multiple partitions, see kDumpPartitions
                            foreach (var dumpPartition in kDumpPartitions)
                                partitions.Add(new Tuple<string, long, long>(
                                    $"DevKit {dumpPartition.Item1}", partition.Offset + dumpPartition.Item2, dumpPartition.Item3));
                        else
                            partitions.Add(new Tuple<string, long, long>(
                                    $"DevKit {kDevkitPartitionNames[i]}", partition.Offset, partition.Size));
                    }
                }
            }

            // Read in cache partition data
            Stream.Position = 0x800;
            CacheHeader = Stream.ReadStruct<CACHE_PARTITION_DATA>();
            CacheHeader.EndianSwap();

            // Check partition validity & remove any invalid ones
            var removeList = new List<int>();
            for(int i = 0; i < partitions.Count; i++)
            {
                Stream.Seek(partitions[i].Item2, SeekOrigin.Begin);
                var header = Stream.ReadStruct<FAT_VOLUME_METADATA>();
                if (!header.IsValid)
                    removeList.Add(i);
            }
            removeList.Reverse();
            foreach(var index in removeList)
                partitions.RemoveAt(index);

            // Sort partitions by their offset
            partitions.OrderBy(p => p.Item2).ToList();

            // Work out the retail data partition size
            // (We check all partitions for size == 0 here, because devkit partitions could be added after)
            // (Even though any retail data partition would be invalid/corrupt by devkit partition presence, it's worth trying to salvage it)
            for (int i = 0; i < partitions.Count; i++)
            {
                var partition = partitions[i];
                long size = 0x377FFC000; // 20GB HDD
                if (DriveSize != 0x04AB440C00)  // 20GB HDD
                    size = DriveSize - partition.Item2;

                if (partition.Item3 == 0)
                    partitions[i] = new Tuple<string, long, long>(partition.Item1, partition.Item2, size);
            }

            // TODO: check if any partitions interfere with each other (eg. devkit Partition1 located inside retail Partition1 space), and mark the drive label if so ("CORRUPT" or something similar)

            // Load in the filesystems & mount them:
            int stfcIndex = 0;
            int driveIndex = -1;
            foreach (var partition in partitions)
            {
                if (partition.Item3 == 0)
                    continue; // Couldn't figure out size of it ?

                driveIndex++;

                Stream.Position = partition.Item2;
                var fatx = new FatxFileSystem(Stream, partition.Item1, partition.Item2, partition.Item3);
                fatx.StreamLock = StreamLock;

                var Host = new FileSystemHost(fatx);
                Host.Prefix = null;
                if (Host.Mount(null, null, false, DebugLogLevel) < 0)
                {
                    if (true)
                    {
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

                        Host = new FileSystemHost(stfs);
                        Host.Prefix = null;
                        if (Host.Mount(null, null, false, DebugLogLevel) < 0)
                            continue;
                    }
                }
                filesystems.Add(Host);
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
}
