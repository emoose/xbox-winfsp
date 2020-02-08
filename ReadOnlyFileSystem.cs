using System;
using System.IO;
using System.Collections.Generic;

using Fsp;
using VolumeInfo = Fsp.Interop.VolumeInfo;
using FileInfo = Fsp.Interop.FileInfo;
using System.Reflection;

namespace XboxWinFsp
{
    public class ReadOnlyFileSystem : FileSystemBase
    {
        internal string InputPath;
        internal Stream Stream;

        public List<IFileEntry> RootFiles;
        public int SectorSize;
        public ulong TotalBytesInUse;
        public string VolumeLabel;

        public ReadOnlyFileSystem(Stream stream, string inputPath, int sectorSize)
        {
            Stream = stream;
            SectorSize = sectorSize;
            InputPath = inputPath;
            VolumeLabel = Path.GetFileName(inputPath);
        }

        public interface IFileEntry
        {
            string Name { get; set; }
            ulong Size { get; set; }

            bool IsDirectory { get; set; }

            ulong CreationTime { get; set; }
            ulong LastWriteTime { get; set; }

            List<IFileEntry> Children { get; set; }
            IFileEntry Parent { get; set; }

            uint ReadBytes(IntPtr buffer, ulong fileOffset, uint length);
        }

        public class FileInstance
        {
            ReadOnlyFileSystem FileSystem;
            internal IFileEntry FileEntry;

            public FileInstance(IFileEntry entry, ReadOnlyFileSystem fileSystem)
            {
                FileEntry = entry;
                FileSystem = fileSystem;
            }

            public Int32 GetFileInfo(out FileInfo FileInfo)
            {
                FileInfo = new FileInfo();
                FileInfo.FileAttributes = (uint)FileAttributes.Directory;

                if (FileEntry != null)
                {
                    FileInfo.CreationTime = (ulong)FileEntry.CreationTime;//.ToFileTimeUtc();
                    FileInfo.LastWriteTime = FileInfo.ChangeTime = (ulong)FileEntry.LastWriteTime;//.ToFileTimeUtc();
                    FileInfo.FileSize = FileEntry.Size;
                    FileInfo.AllocationSize = Utility.RoundToPages(FileEntry.Size, (ulong)FileSystem.SectorSize) * (ulong)FileSystem.SectorSize;
                    FileInfo.FileAttributes = FileEntry.IsDirectory ? (uint)FileAttributes.Directory : 0;
                }

                FileInfo.FileAttributes |= (uint)FileAttributes.ReadOnly;

                return STATUS_SUCCESS;
            }
        }

        IFileEntry FindFile(string path)
        {
            IFileEntry foundEntry = null;

            if (path.StartsWith("\\", StringComparison.InvariantCultureIgnoreCase))
                path = path.Substring(1);

            var filePath = path.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);

            // Try going through the split path to try and find the file entry
            var searchList = RootFiles;
            for (int i = 0; i < filePath.Length; i++)
            {
                var entry = searchList.Find(s => string.Compare(s.Name, filePath[i], StringComparison.InvariantCultureIgnoreCase) == 0);
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
                var fileDesc = new FileInstance(null, this);
                FileDesc0 = fileDesc;
                return fileDesc.GetFileInfo(out FileInfo);
            }

            var foundEntry = FindFile(FileName);
            if (foundEntry == null)
            {
                FileInfo = default;
                FileDesc0 = null;
                return STATUS_NO_SUCH_FILE;
            }

            var fileDesc2 = new FileInstance(foundEntry, this);
            FileDesc0 = fileDesc2;
            return fileDesc2.GetFileInfo(out FileInfo);
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
            var Instance = (FileInstance)FileDesc0;
            var Enumerator = (IEnumerator<IFileEntry>)Context;

            if (Enumerator == null)
            {
                var childArr = RootFiles;
                if (Instance.FileEntry != null)
                    childArr = Instance.FileEntry.Children;

                Enumerator = childArr.GetEnumerator();
                Context = Enumerator;
                int Index = 0;
                if (null != Marker)
                {
                    Index = childArr.FindIndex(s => s.Name == Marker);
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
                FileName = entry.Name;
                var desc = new FileInstance(entry, this);
                desc.GetFileInfo(out FileInfo);
                return true;
            }

            FileName = default;
            FileInfo = default;
            return false;
        }

        public override Int32 Read(
            Object FileNode,
            Object FileDesc0,
            IntPtr Buffer,
            UInt64 Offset,
            UInt32 Length,
            out UInt32 PBytesTransferred)
        {
            var Instance = (FileInstance)FileDesc0;
            if (Offset >= Instance.FileEntry.Size)
            {
                PBytesTransferred = 0;
                return STATUS_END_OF_FILE;
            }

            PBytesTransferred = Instance.FileEntry.ReadBytes(Buffer, Offset, Length);
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
                var entry = FindFile(FileName);
                if (entry != null)
                    FileAttributes1 = entry.IsDirectory ? (uint)FileAttributes.Directory : 0;
                else
                    return STATUS_NO_SUCH_FILE;
            }
            return STATUS_SUCCESS;
        }

        public override Int32 GetFileInfo(
            Object FileNode,
            Object FileDesc0,
            out FileInfo FileInfo)
        {
            var instance = (FileInstance)FileDesc0;
            return instance.GetFileInfo(out FileInfo);
        }

        public override Int32 GetVolumeInfo(out VolumeInfo VolumeInfo)
        {
            VolumeInfo = default;
            VolumeInfo.FreeSize = 0;
            VolumeInfo.TotalSize = TotalBytesInUse + VolumeInfo.FreeSize;
            VolumeInfo.SetVolumeLabel(VolumeLabel);

            return STATUS_SUCCESS;
        }

        public override Int32 Init(Object Host0)
        {
            var Host = (FileSystemHost)Host0;

            // Hack for us to set ReadOnlyVolume flag...
            var paramsField = Host.GetType().GetField("_VolumeParams", BindingFlags.NonPublic | BindingFlags.Instance);
            object paramsVal = paramsField.GetValue(Host);
            var flagsField = paramsVal.GetType().GetField("Flags", BindingFlags.NonPublic | BindingFlags.Instance);
            uint value = (uint)flagsField.GetValue(paramsVal);
            flagsField.SetValue(paramsVal, value | 0x200);
            paramsField.SetValue(Host, paramsVal);

            return STATUS_SUCCESS;
        }
    }
}
