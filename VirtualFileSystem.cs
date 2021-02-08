using System;
using System.IO;
using System.Collections.Generic;

using Fsp;

namespace XboxWinFsp
{
    public class VirtualFileSystem : ReadOnlyFileSystem
    {

        DateTime CreationTime = DateTime.Now;

        List<ReadOnlyFileSystem> FileSystems;
        FatxDevice Device;
        String Inputpath;

        private static int kSectorSize = 0x200;

        public VirtualFileSystem(Stream stream, String inputPath) : base(stream, inputPath, kSectorSize)
        {
            Device = new FatxDevice(stream);
            Inputpath = inputPath;
        }

        void VirtualFsInit()
        {

            if (!Device.IsFatxDevice())
            {
                throw new FileSystemParseException("Not a Fatx device.");
            }

            RootFiles = new List<IFileEntry>();

            FileSystems = Device.LoadPartitions();
            foreach(var fileSystem in FileSystems)
            {
                var fileEntry = new FileEntry(null);
                fileEntry.Name = fileSystem.VolumeLabel;
                fileEntry.Size = 0;
                fileEntry.IsDirectory = true;
                fileEntry.Parent = null;
                fileEntry.Children = fileSystem.RootFiles;
                fileEntry.CreationTime = fileEntry.LastAccessTime = fileEntry.LastWriteTime = CreationTime;
                RootFiles.Add(fileEntry);
            }

        }

        public override Int32 Init(Object Host0)
        {
            if (Device == null)
                return STATUS_OPEN_FAILED;

            try
            {
                VirtualFsInit();

                var Host = (FileSystemHost)Host0;
                Host.SectorSize = 512;
                Host.SectorsPerAllocationUnit = 1;
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
                Host.VolumeSerialNumber = 0;
                Host.FileSystemName = "XBOX Virtual FS";
                VolumeLabel = Inputpath;

                return base.Init(Host0);
            }
            catch (FileSystemParseException)
            {
                return STATUS_OPEN_FAILED;
            }
        }

        protected class FileEntry : IFileEntry
        {

            public string Name { get; set; }

            public ulong Size { get; set; }

            public bool IsDirectory { get; set; }

            public DateTime CreationTime { get; set; }

            public DateTime LastWriteTime { get; set; }

            public DateTime LastAccessTime { get; set; }

            public List<IFileEntry> Children { get; set; }
            public IFileEntry Parent { get; set; }

            public FileEntry(FileEntry parent)
            {
                Parent = parent;
            }

            public uint ReadBytes(IntPtr buffer, ulong fileOffset, uint length)
            {
                return 0;
            }

            public override string ToString()
            {
                return $"{(IsDirectory ? "D" : "F")} {Name} 0x{Size:X}";
            }
        }
    }

}
