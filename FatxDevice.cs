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

        static readonly long[] kKnownFatxPartitionOffsets = new long[]
        {
            // free60-MU
            0x0,
            0x7FF000,

            // free60-HDD
            0x80000,
            0x80080000,
            0x10C080000,
            0x118EB0000,
            0x120EB0000,
            0x130EB0000,

            // USB
            0x8000400,
            0x20000000,
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
            foreach (var addr in kKnownFatxPartitionOffsets)
            {
                if (DriveSize <= addr)
                    continue;

                Stream.Seek(addr, SeekOrigin.Begin);
                var fatxHeader = Stream.ReadStruct<FAT_VOLUME_METADATA>();
                if (fatxHeader.IsValid)
                    return true;
            }

            return false;
        }

        public List<FileSystemHost> LoadPartitions()
        {
            if (!IsFatxDevice())
                return null;

            var filesystems = new List<FileSystemHost>();
            var partitions = new Dictionary<long, long>();

            foreach (var offset in kKnownFatxPartitionOffsets)
            {
                if (DriveSize <= offset)
                    continue;
                partitions[offset] = 0;
            }

            Stream.Position = 0;
            var devkitTable = Stream.ReadStruct<FATX_DEVKIT_PARTITION_TABLE>();
            devkitTable.EndianSwap();
            if (devkitTable.IsValid)
            {
                partitions.Remove(0x130EB0000); // Retail only

                foreach (var partition in devkitTable.Partitions)
                {
                    if (partition.SectorNumber == 0 || partition.NumSectors == 0)
                        break;

                    partitions[partition.Offset] = partition.Size;
                }
            }

            // Read in cache partition data
            Stream.Position = 0x800;
            CacheHeader = Stream.ReadStruct<CACHE_PARTITION_DATA>();
            CacheHeader.EndianSwap();

            // Check partition validity, remove any invalid ones
            var removeList = new List<long>();
            foreach(var kvp in partitions)
            {
                Stream.Seek(kvp.Key, SeekOrigin.Begin);
                var header = Stream.ReadStruct<FAT_VOLUME_METADATA>();
                if (!header.IsValid)
                    removeList.Add(kvp.Key);
            }
            foreach(var remove in removeList)
            {
                partitions.Remove(remove);
            }

            // Sort partitions by their offset
            var keys = partitions.Keys.ToList();
            keys.Sort();

            // Work out partition size if we don't have one already...
            for (int i = 0; i < keys.Count - 1; i++)
            {
                var kvp = partitions[keys[i]];
                if (kvp == 0)
                {
                    partitions[keys[i]] = keys[i + 1] - keys[i];
                }
            }

            // Work out retail data partition size
            if (!devkitTable.IsValid)
            {
                if (DriveSize == 0x04AB440C00)  // 20GB HDD
                    partitions[keys[keys.Count - 1]] = 0x377FFC000;
                else
                    partitions[keys[keys.Count - 1]] = DriveSize - keys[keys.Count - 1];
            }

            // Load in the filesystems & mount them:
            int stfcIndex = 0;
            int driveIndex = -1;
            foreach (var partition in partitions)
            {
                if (partition.Value == 0)
                    continue; // Couldn't figure out size of it ?

                driveIndex++;

                Stream.Position = partition.Key;
                var fatx = new FatxFileSystem(Stream, "physdrive", partition.Key, partition.Value);
                fatx.StreamLock = StreamLock;

                var Host = new FileSystemHost(fatx);
                Host.Prefix = null;
                if (Host.Mount(null, null, false, 0xffffffff) < 0)
                {
                    if (true)
                    {
                        Stream.Position = partition.Key;
                        var stfs = new StfsFileSystem(Stream, "physdrive", partition.Key);
                        stfs.StreamLock = StreamLock;
                        stfs.SkipHashChecks = true; // TODO!
                        if (stfcIndex < 2 && CacheHeader.IsValid)
                        {
                            stfs.CacheHeader = CacheHeader;
                            stfs.CachePartitionIndex = stfcIndex;
                            stfs.StfsVolumeDescriptor = CacheHeader.VolumeDescriptor[stfcIndex];
                        }

                        stfcIndex++;

                        Host = new FileSystemHost(stfs);
                        Host.Prefix = null;
                        if (Host.Mount(null, null, false, 0xffffffff) < 0)
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

        // Looks like kernel will read however many partitions there are, until it reaches an empty one?
        // (up to 0x200 bytes worth at most?)
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 31)]
        public FATX_PARTITION_BOUNDS[] Partitions;

        public void EndianSwap()
        {
            RecoveryVersionMajor = RecoveryVersionMajor.EndianSwap();
            RecoveryVersionMinor = RecoveryVersionMinor.EndianSwap();
            RecoveryVersionBuildQFE = RecoveryVersionBuildQFE.EndianSwap();
            for (int i = 0; i < 31; i++)
                Partitions[i].EndianSwap();
        }
    }
}
