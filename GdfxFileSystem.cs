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

        // Commonly used addrs for XGD data start
        // These aren't actually set in stone, XGD sector seems to be defined inside SS
        // "Xbox 360 Trial Disk" is one weird outlier that uses a completely different address
        // See DvdPhysicalFormatInformation usage inside GdfxInit for how to use SS to find it
        const long kXgdRawHeaderAddress = 0x10000; // SDK generated only?
        const long kXgd1HeaderAddress = 0x18300000 + 0x10000; // Address of XGD1 (OG xbox) header
        const long kXgd2HeaderAddress = 0xFD90000 + 0x10000; // Address of XGD2 header?
        const long kXgd3HeaderAddress = 0x2080000 + 0x10000; // Address of XGD3 header?
        const long kXgd2HeaderAddressAlt = 0x89D80000 + 0x10000; // "Xbox 360 Trial Disk" (http://redump.org/disc/58095/) - only disk with weird XGD address seen so far

        GDF_VOLUME_DESCRIPTOR GdfHeader;

        ulong HeaderAddress = 0;
        ulong BaseAddress = 0;

        public GdfxFileSystem(Stream stream, string inputPath) : base(stream, inputPath, kSectorSize)
        {
        }

        void GdfxInit()
        {
            // First try reading GDF header from some standard addresses:
            long[] standardAddresses = new long[] { kXgdRawHeaderAddress, kXgd2HeaderAddress, kXgd3HeaderAddress, kXgd1HeaderAddress, kXgd2HeaderAddressAlt };
            long headerAddress = -1;
            foreach (var addr in standardAddresses)
            {
                if (Stream.Length > addr)
                {
                    Stream.Position = addr;
                    GdfHeader = Stream.ReadStruct<GDF_VOLUME_DESCRIPTOR>();
                    if (GdfHeader.IsValid)
                    {
                        headerAddress = addr;
                        break;
                    }
                }
            }

            // If that failed, try locating XGD sector from data inside merged SS & PFI, if exists
            // TODO: turns out this is pointless, because SS/PFI/DMI mergers copy them into the sectors just before XGD area
            // So in order to find SS/PFI, you'd need to know where XGD was in the first place, which is the whole reason we're trying to read SS/PFI...
            // Commenting out for now, method used here should still be useful for reading from external SS/PFI files for any other problematic disks
            /*
            const int kSectorNumXtremePFI = 0x1FB1D; // note: these are only actually valid for disks with XGD @ 0xFD90000, which is why this code is removed
            const int kSectorNumXtremeDMI = 0x1FB1E;
            const int kSectorNumXtremeSS = 0x1FB1F;
            if (!GdfHeader.IsValid)
            {
                long pfiAddress = kSectorNumXtremePFI * kSectorSize;
                long ssAddress = kSectorNumXtremeSS * kSectorSize;
                if (Stream.Length > pfiAddress && Stream.Length > ssAddress)
                {
                    Stream.Position = pfiAddress;
                    var pfi = Stream.ReadStruct<DvdPhysicalFormatInformation>();

                    Stream.Position = ssAddress;
                    var ss = Stream.ReadStruct<DvdPhysicalFormatInformation>();
                    if (pfi.IsMainPFI && ss.IsXboxPFI)
                    {
                        // DataZoneSectorStart is in "physical" sectors, which include lead-in and other sectors that come before the actual data area
                        // main PFI gives us the starting sector for data area, so we can remove that from the xbox/SS PFI to find the XGD sector in our data
                        long xgdSector = ss.DataZoneSectorStart - pfi.DataZoneSectorStart;
                        long xgdAddress = (xgdSector + 32) * kSectorSize;
                        if (Stream.Length > xgdAddress)
                        {
                            Stream.Position = xgdAddress;
                            GdfHeader = Stream.ReadStruct<GDF_VOLUME_DESCRIPTOR>();
                            if (GdfHeader.IsValid)
                                headerAddress = xgdAddress;
                        }
                    }
                }
            }*/

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
        List<IFileEntry> GdfReadDirectory(ulong sectorNum, long dirSize, FileEntry parent)
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
                    entry.Children = GdfReadDirectory(entry.DirEntry.FirstSector, (long)entry.Size, entry);
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
                Host.VolumeCreationTime = (ulong)GdfHeader.TimeStamp;
                Host.VolumeSerialNumber = GdfHeader.RootSector;

                string gdfType = "Raw XGD";
                if (HeaderAddress == kXgd1HeaderAddress)
                    gdfType = "XGD1";
                else if (HeaderAddress == kXgd2HeaderAddress)
                    gdfType = "XGD2";
                else if (HeaderAddress == kXgd3HeaderAddress)
                    gdfType = "XGD3";
                else if (HeaderAddress == kXgd2HeaderAddressAlt)
                    gdfType = "XGD2, oversized video partition";
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

            public DateTime CreationTime
            {
                get
                {
                    return DateTime.FromFileTimeUtc(FileSystem.GdfHeader.TimeStamp);
                }
                set { throw new NotImplementedException(); }
            }

            public DateTime LastWriteTime
            {
                get
                {
                    return DateTime.FromFileTimeUtc(FileSystem.GdfHeader.TimeStamp);
                }
                set { throw new NotImplementedException(); }
            }

            public DateTime LastAccessTime
            {
                get
                {
                    return DateTime.FromFileTimeUtc(FileSystem.GdfHeader.TimeStamp);
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

                // Windows-1252 encoding seems to allow ANSI/extended ASCII characters to work
                // eg. used in "Amped: Freestyle Snowboarding" for the file "Gigi Rüf.res"
                fileName = Encoding.GetEncoding(1252).GetString(fileNameBytes);

                if(FileSystem.GdfSectorToAddress(DirEntry.FirstSector) + DirEntry.FileSize > (ulong)stream.Length)
                {
                    // The stream ends before the file, truncate it
                    DirEntry.FileSize = (uint)((ulong)stream.Length - FileSystem.GdfSectorToAddress(DirEntry.FirstSector));
                }

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
    public struct DvdPhysicalFormatInformation 
    {
        // based on format from https://www.ecma-international.org/wp-content/uploads/ECMA-267_1st_edition_december_1997.pdf
        // are there any better sources we can find this struct from?

        public byte DiscCategoryAndVerNo; // 0x01 in main PFI, 0xD1/0xE1 in SS PFI
        public byte DiscSizeAndMaxTransferRate; // 0x02 in main PFI, 0x0F in SS PFI
        public byte DiscStructure; // 0x31
        public byte RecordingDensity; // 0x10

        public uint DataZoneSectorStart; // should always be 0x30000 for main disk PFI, security-sector PFI stores (XGD sector + MainPFI.DataZoneSectorStart)
        public uint DataZoneSectorEnd;
        public uint DataZoneSectorEndLayer0;
        public byte BCADescriptor;

        // xbox PFI contains a bunch of extra data after it

        public void EndianSwap()
        {
            DataZoneSectorStart = DataZoneSectorStart.EndianSwap();
            DataZoneSectorEnd = DataZoneSectorEnd.EndianSwap();
            DataZoneSectorEndLayer0 = DataZoneSectorEndLayer0.EndianSwap();
        }

        public bool IsValid
        {
            get
            {
                return DiscStructure == 0x31 && RecordingDensity == 0x10; // seems constant between main PFI & xbox PFI
            }
        }

        public bool IsMainPFI
        {
            get
            {
                // TODO: Unsure if these are constant between every disc, commenting for now...
                //return DiscCategoryAndVerNo == 0x01 && DiscSizeAndMaxTransferRate == 0x02 && IsValid;
                return IsValid;
            }
        }

        public bool IsXboxPFI
        {
            get
            {
                // Booktype D for OG xbox, E for xbox360, not sure if any others were ever used
                return (DiscCategoryAndVerNo == 0xD1 || DiscCategoryAndVerNo == 0xE1) && DiscSizeAndMaxTransferRate == 0x0F && IsValid;
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
        public long TimeStamp;
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
