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
using System.Security.AccessControl;

namespace XboxWinFsp
{
    public class GdfxFileSystem : FileSystemBase
    {
        const int kSectorSize = 0x800;

        const int kXgd2HeaderAddress = 0x10000; // Address of XGD1/XGD2 header (depends on Version field value)
        const int kXgd25HeaderAddress = 0xFDA0000; // Address of XGD2.5 header?
        const int kXgd3HeaderAddress = 0x2090000; // Address of XGD3 header?

        string imagePath = "";
        Stream stream = null;
        Object streamLock = new object();

        GDF_VOLUME_DESCRIPTOR gdfHeader;
        ulong gdfHeaderAddress = 0;
        ulong gdfBaseAddress = 0;

        GdfxEntry[] rootChildren;
        ulong totalBytesInUse = 0;

        // Info about a file stored inside the GDFX image
        protected class GdfxEntry
        {
            GdfxFileSystem fileSystem;

            public GDF_DIRECTORY_ENTRY DirEntry;
            public long DirEntryAddr;
            public string FileName;
            public GdfxEntry[] Children;
            public GdfxEntry Parent;

            public GdfxEntry(GdfxEntry parent, GdfxFileSystem fileSystem)
            {
                Parent = parent;
                this.fileSystem = fileSystem;
            }

            public bool Read(Stream stream)
            {
                DirEntryAddr = stream.Position;
                DirEntry = stream.ReadStruct<GDF_DIRECTORY_ENTRY>();
                if (!DirEntry.IsValid)
                    return false;

                byte[] fileNameBytes = new byte[DirEntry.FileNameLength];
                stream.Read(fileNameBytes, 0, DirEntry.FileNameLength);
                FileName = Encoding.ASCII.GetString(fileNameBytes);

                if(DirEntry.IsDirectory)
                    Children = fileSystem.GdfReadDirectory(DirEntry.FirstSector, DirEntry.FileSize, this);

                // Align code from Velocity, seems to work great
                stream.Position = (long)((ulong)(DirEntryAddr + DirEntry.FileNameLength + 0x11) & 0xFFFFFFFFFFFFFFFC);
                return true;
            }

            public override string ToString()
            {
                return $"{FileName}" + (Children != null ? $" ({Children.Length} children)" : "");
            }
        }

        // An open instance of a file
        // (for read-only FS maybe this can be merged with the actual entry itself?)
        protected class FileDesc
        {
            GdfxFileSystem fileSystem;

            internal GdfxEntry FileEntry;

            internal FileDesc(GdfxEntry entry, GdfxFileSystem fileSystem)
            {
                FileEntry = entry;
                this.fileSystem = fileSystem;
            }

            public Int32 GetFileInfo(out FileInfo FileInfo)
            {
                FileInfo = new FileInfo();
                FileInfo.LastWriteTime = FileInfo.CreationTime = FileInfo.ChangeTime = fileSystem.gdfHeader.TimeStamp;
                FileInfo.FileAttributes = (uint)FileAttributes.Directory;

                if (FileEntry != null)
                {
                    FileInfo.FileSize = FileEntry.DirEntry.FileSize;
                    FileInfo.AllocationSize = ((FileEntry.DirEntry.FileSize + GdfxFileSystem.kSectorSize - 1) / GdfxFileSystem.kSectorSize) * GdfxFileSystem.kSectorSize;
                    FileInfo.FileAttributes = FileEntry.DirEntry.FileAttributes;
                }

                FileInfo.FileAttributes |= (uint)FileAttributes.ReadOnly;

                return STATUS_SUCCESS;
            }
        }

        public GdfxFileSystem(string path)
        {
            imagePath = path;
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

            var children = rootChildren;
            if (FileDesc.FileEntry != null)
                children = FileDesc.FileEntry.Children;

            int Index = Context != null ? (int)Context : 0;
            if (Context == null)
            {
                Index = 0;
                if (null != Marker)
                {
                    var testEntry = new GdfxEntry(null, this);
                    testEntry.FileName = Marker;
                    Index = Array.BinarySearch(children, testEntry, _DirectoryEntryComparer);
                    if (0 <= Index)
                        Index++;
                    else
                        Index = ~Index;
                }
            }

            if (children.Length > Index)
            {
                Context = Index + 1;
                var entry = children[Index];
                FileName = entry.FileName;
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

        public override Int32 GetVolumeInfo(
            out VolumeInfo VolumeInfo)
        {
            VolumeInfo = default;
            VolumeInfo.FreeSize = 0;
            VolumeInfo.TotalSize = totalBytesInUse;
            VolumeInfo.SetVolumeLabel(Path.GetFileName(imagePath)); // TODO: grab title name from XEX/XBE?

            return STATUS_SUCCESS;
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
                var entry = GdfGetEntryFromPath(FileName);
                if(entry != null)
                    FileAttributes1 = entry.DirEntry.FileAttributes;
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
                var fileDesc = new FileDesc(null, this);
                FileDesc0 = fileDesc;
                return fileDesc.GetFileInfo(out FileInfo);
            }

            var foundEntry = GdfGetEntryFromPath(FileName);

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
            if(Offset + Length >= (UInt64)FileDesc.FileEntry.DirEntry.FileSize)
            {
                Length = (uint) (FileDesc.FileEntry.DirEntry.FileSize - Offset);
            }

            Byte[] Bytes = new byte[Length];
            lock (streamLock)
            {
                stream.Seek((long)(GdfSectorToAddress(FileDesc.FileEntry.DirEntry.FirstSector) + Offset), SeekOrigin.Begin);
                PBytesTransferred = (UInt32)stream.Read(Bytes, 0, Bytes.Length);
            }
            Marshal.Copy(Bytes, 0, Buffer, Bytes.Length);
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

        public override Int32 Init(Object Host0)
        {
            stream = File.OpenRead(imagePath);
            if (stream == null)
                return STATUS_OPEN_FAILED;

            // First try reading GDF header from some standard addresses:
            int[] standardAddresses = new int[] { kXgd2HeaderAddress, kXgd25HeaderAddress, kXgd3HeaderAddress };
            long headerAddress = -1;
            foreach(var addr in standardAddresses)
            {
                stream.Position = addr;
                gdfHeader = stream.ReadStruct<GDF_VOLUME_DESCRIPTOR>();
                if(gdfHeader.IsValid)
                {
                    headerAddress = addr;
                    break;
                }
            }

            // If that failed, try scanning the file for it:
            if (!gdfHeader.IsValid)
            {
                stream.Position = kXgd2HeaderAddress;
                while (stream.Length > stream.Position)
                {
                    long pos = stream.Position;
                    gdfHeader = stream.ReadStruct<GDF_VOLUME_DESCRIPTOR>();
                    if (gdfHeader.IsValid)
                    {
                        headerAddress = pos;
                        break;
                    }

                    // Try checking the next sector...
                    stream.Position = pos + kSectorSize;
                }
            }

            // If that still failed then this probably isn't a GDFX file :(
            if (!gdfHeader.IsValid)
                return STATUS_OPEN_FAILED;

            gdfHeaderAddress = (ulong)headerAddress;
            gdfBaseAddress = gdfHeaderAddress - (kSectorSize * 32);

            rootChildren = GdfReadDirectory(gdfHeader.RootSector, gdfHeader.RootSize, null);

            FileSystemHost Host = (FileSystemHost)Host0;
            Host.SectorSize = kSectorSize;
            Host.SectorsPerAllocationUnit = 1;
            Host.MaxComponentLength = 255;
            Host.FileInfoTimeout = 1000;
            Host.CaseSensitiveSearch = false;
            Host.CasePreservedNames = true;
            Host.UnicodeOnDisk = true;
            Host.PersistentAcls = false;
            Host.PassQueryDirectoryPattern = true;
            Host.FlushAndPurgeOnCleanup = true;
            Host.VolumeCreationTime = gdfHeader.TimeStamp;
            Host.VolumeSerialNumber = gdfHeader.RootSector;

            string gdfVersion = "XGD1";
            if (gdfHeader.Version != 0) // Only Xbox360 disks have Version set?
            {
                gdfVersion = "XGD2";
                if (headerAddress == kXgd25HeaderAddress)
                    gdfVersion = "XGD2.5";
                else if (headerAddress == kXgd3HeaderAddress)
                    gdfVersion = "XGD3";
            }

            Host.FileSystemName = $"GDFX ({gdfVersion})";

            // Hack for us to set ReadOnlyVolume flag...
            var paramsField = Host.GetType().GetField("_VolumeParams", BindingFlags.NonPublic | BindingFlags.Instance);
            object paramsVal = paramsField.GetValue(Host);
            var flagsField = paramsVal.GetType().GetField("Flags", BindingFlags.NonPublic | BindingFlags.Instance);
            uint value = (uint)flagsField.GetValue(paramsVal);
            flagsField.SetValue(paramsVal, value | 0x200);
            paramsField.SetValue(Host, paramsVal);

            return STATUS_SUCCESS;
        }

        // Reads GDFX directory entries into a list
        GdfxEntry[] GdfReadDirectory(ulong sectorNum, long dirSize, GdfxEntry parent)
        {
            long curPos = stream.Position;

            stream.Position = (long)GdfSectorToAddress(sectorNum);
            var retVal = new List<GdfxEntry>();
            byte[] padBytes = new byte[4];
            long sectorRemain = 0x800;
            while (dirSize > 0)
            {
                if (sectorRemain > 0)
                    stream.Read(padBytes, 0, 4);

                if (sectorRemain <= 0 || padBytes[0] == 0xFF && padBytes[1] == 0xFF && padBytes[2] == 0xFF && padBytes[3] == 0xFF)
                {
                    sectorNum++;
                    dirSize -= kSectorSize;
                    stream.Position = (long)GdfSectorToAddress(sectorNum);
                    sectorRemain = 0x800;
                    continue;
                }

                stream.Position -= 4;

                long entPos = stream.Position;
                GdfxEntry entry = new GdfxEntry(parent, this);
                if (!entry.Read(stream))
                    break;

                long entSize = stream.Position - entPos;
                sectorRemain -= entSize;

                if (!entry.DirEntry.IsDirectory)
                    totalBytesInUse += entry.DirEntry.FileSize;

                retVal.Add(entry);
            }

            retVal.Sort((x, y) => x.FileName.CompareTo(y.FileName));

            stream.Position = curPos;
            return retVal.ToArray();
        }

        // Navigates a given path string to return the GdfxEntry of the path given
        // TODO: GDFX supports binary tree searching, need to figure out how to use that instead...
        GdfxEntry GdfGetEntryFromPath(string path)
        {
            GdfxEntry foundEntry = null;

            if (path.StartsWith("\\", StringComparison.InvariantCultureIgnoreCase))
                path = path.Substring(1);

            string[] filePath = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            // Try going through the split path to try and find the file entry
            var searchList = rootChildren;
            for (int i = 0; i < filePath.Length; i++)
            {
                var entry = Array.Find(searchList, s => s.FileName == filePath[i]);
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

        ulong GdfSectorToAddress(ulong sector)
        {
            return (sector * kSectorSize) + gdfBaseAddress;
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

        class DirectoryEntryComparer : IComparer
        {
            public int Compare(object x, object y)
            {
                return String.Compare(((GdfxEntry)x).FileName, ((GdfxEntry)y).FileName);
            }
        }

        static DirectoryEntryComparer _DirectoryEntryComparer = new DirectoryEntryComparer();
    }
}
