/**
 * @file Program.cs
 *
 * @copyright 2015-2018 Bill Zissimopoulos
 */
/*
 * This file is part of WinFsp.
 *
 * You can redistribute it and/or modify it under the terms of the GNU
 * General Public License version 3 as published by the Free Software
 * Foundation.
 *
 * Licensees holding a valid commercial license may use this software
 * in accordance with the commercial license agreement provided in
 * conjunction with the software.  The terms and conditions of any such
 * commercial license agreement shall govern, supersede, and render
 * ineffective any application of the GPLv3 license to this software,
 * notwithstanding of any reference thereto in the software or
 * associated repository.
 */

using System;
using System.IO;
using Microsoft.Win32;

using Fsp;
using System.Security;
using System.Security.Permissions;
using System.Collections.Generic;

namespace XboxWinFsp
{
    class XboxFsService : Service
    {
        private FileSystemHost _Host;
        private List<FileSystemHost> _Hosts;

        private class CommandLineUsageException : Exception
        {
            public CommandLineUsageException(String Message = null) : base(Message)
            {
                HasMessage = null != Message;
            }

            public bool HasMessage;
        }

        private const String PROGNAME = "xbox-winfsp";

        public XboxFsService() : base("XboxFsService")
        {
        }

        protected void RemoveFS()
        {
            try
            {
                Console.WriteLine("\r\nRemoving any Xbox filesystems...\r\n");

                RegistryKey key;

                try
                {
                    key = Registry.LocalMachine.OpenSubKey(@"Software\WinFsp\Services", true);
                    if (key != null)
                        key.DeleteSubKeyTree("xbox-winfsp", false);
                }
                catch (ObjectDisposedException)
                {
                    // Nothing
                }

                try
                {
                    key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\*\shell", true);
                    if (key != null)
                    {
                        key.DeleteSubKeyTree("Mount as Xbox STFS/GDF", false); // Old key, before changes
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Nothing
                }

                try
                {
                    key = Registry.LocalMachine.OpenSubKey(@"Software\Classes\*\shell", true);
                    if (key != null)
                    {
                        key.DeleteSubKeyTree("Mount with XBOX-WINFSP", false);
                    }
                }
                catch (ObjectDisposedException)
                {
                    // Nothing
                }

                Console.WriteLine("Removed Xbox filesystems successfully.\r\n");
            }
            catch (Exception ex)
            {
                if(ex is SecurityException || ex is UnauthorizedAccessException)
                {
                    Console.WriteLine("An error was encountered, maybe try running as admin?\r\n");
                } else
                {
                    throw;
                }
            }

        }

        protected void SetupFS()
        {
            RemoveFS();

            try
            {

                Console.WriteLine("\r\nSetting up Xbox filesystems...\r\n");
                // Add to WinFsp services list, allows using "net use X: \\xbox-winfps\C$\game.iso"
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\WinFsp\Services\xbox-winfsp", "CommandLine", "-u %1 -m %2", RegistryValueKind.String);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\WinFsp\Services\xbox-winfsp", "Executable", System.Reflection.Assembly.GetEntryAssembly().Location, RegistryValueKind.String);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\WinFsp\Services\xbox-winfsp", "Security", "D:P(A;;RPWPLC;;;WD)", RegistryValueKind.String);
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\WinFsp\Services\xbox-winfsp", "JobControl", 1, RegistryValueKind.DWord);

                // Context menu item for all files (since STFS has no extension...)
                Registry.SetValue(@"HKEY_LOCAL_MACHINE\Software\Classes\*\shell\Mount with XBOX-WINFSP\command", null, $"\"{System.Reflection.Assembly.GetEntryAssembly().Location}\" -i \"%1\" -m *");

                Console.WriteLine("Successfully setup filesystems, you may need to restart for changes to take effect.\r\n");
            }
            catch (Exception ex)
            {
                if (ex is SecurityException || ex is UnauthorizedAccessException)
                {
                    Console.WriteLine("Error: Failed to setup filesystems, maybe try running as admin?\r\n");
                }
                else
                {
                    throw;
                }
            }
        }

        protected override void OnStart(String[] Args)
        {
            try
            {
                String DebugLogFile = null;
                UInt32 DebugFlags = 0;
                String VolumePrefix = null;
                String ImagePath = null;
                String MountPoint = null;
                IntPtr DebugLogHandle = (IntPtr)(-1);
                bool SetupFS = false;
                bool RemoveFS = false;
                FileSystemHost Host = null;
                GdfxFileSystem Gdfx = null;
                StfsFileSystem Stfs = null;
                FatxFileSystem Fatx = null;
                VirtualFileSystem Vfs = null;

                int I;

                for (I = 1; Args.Length > I; I++)
                {
                    String Arg = Args[I];
                    if ('-' != Arg[0])
                        break;
                    switch (Arg[1])
                    {
                        case '?':
                            throw new CommandLineUsageException();
                        case 'd':
                            argtol(Args, ref I, ref DebugFlags);
                            break;
                        case 'D':
                            argtos(Args, ref I, ref DebugLogFile);
                            break;
                        case 'm':
                            argtos(Args, ref I, ref MountPoint);
                            break;
                        case 'i':
                            argtos(Args, ref I, ref ImagePath);
                            break;
                        case 'u':
                            argtos(Args, ref I, ref VolumePrefix);
                            break;
                        case 's':
                            SetupFS = true;
                            break;
                        case 'r':
                            RemoveFS = true;
                            break;
                        default:
                            throw new CommandLineUsageException();
                    }
                }

                if (Args.Length > I)
                    throw new CommandLineUsageException();

                if (SetupFS)
                {
                    this.SetupFS();
                    throw new CommandLineUsageException();
                }
                if (RemoveFS)
                {
                    this.RemoveFS();
                    throw new CommandLineUsageException();
                }

                if (null == ImagePath && null != VolumePrefix)
                {
                    I = VolumePrefix.IndexOf('\\');
                    if (-1 != I && VolumePrefix.Length > I && '\\' != VolumePrefix[I + 1])
                    {
                        I = VolumePrefix.IndexOf('\\', I + 1);
                        if (-1 != I)
                        {
                            var truncatedPrefix = VolumePrefix.Substring(I);
                            if (truncatedPrefix.StartsWith(@"\PhysicalDrive", StringComparison.InvariantCultureIgnoreCase))
                            {
                                // Format has to be \\.\PHYSICALDRIVE{0-N}
                                ImagePath = String.Format(@"\\.{0}", truncatedPrefix);
                            }
                            else if (truncatedPrefix.Length > 2 && ':' == truncatedPrefix[2])
                            {
                                // Format has to be \\.\X:
                                ImagePath = String.Format(@"\\.{0}", truncatedPrefix);
                            }
                        }
                    }
                }

                if (null != DebugLogFile)
                    if (0 > FileSystemHost.SetDebugLogFile(DebugLogFile))
                        throw new CommandLineUsageException("cannot open debug log file");

                if (!string.IsNullOrEmpty(ImagePath) && !string.IsNullOrEmpty(MountPoint))
                {
                    // For some reason WinFsp needs MountPoint to be null for wildcard to work without elevation...
                    bool openExplorer = false;
                    if (MountPoint == "*")
                    {
                        MountPoint = null;
                        openExplorer = true; // Open mounted drive in explorer if the mountPoint is wildcard - QoL :)
                    }

                    ImagePath = Path.GetFullPath(ImagePath);
                    if (ImagePath.EndsWith(@"\"))
                        ImagePath = ImagePath.Substring(0, ImagePath.Length - 1);

                    Stream stream;

                    if (ImagePath.StartsWith(@"\\"))
                    {
                        stream = new DeviceStream(ImagePath);
                    } else
                    {
                        var fileStream = File.OpenRead(ImagePath);
                        stream = new AlignedStream(fileStream, 0x200 * 0x200);
                    }

                    Host = new FileSystemHost(Vfs = new VirtualFileSystem(stream, ImagePath));
                    Host.Prefix = VolumePrefix;
                    if (Host.Mount(MountPoint, null, true, DebugFlags) < 0)
                    {
                        Vfs = null;
                        stream.Position = 0;
                        Host = new FileSystemHost(Fatx = new FatxFileSystem(stream, ImagePath));
                        Host.Prefix = VolumePrefix;
                        if (Host.Mount(MountPoint, null, true, DebugFlags) < 0)
                        {
                            Fatx = null;
                            stream.Position = 0;
                            Host = new FileSystemHost(Stfs = new StfsFileSystem(stream, ImagePath));
                            Host.Prefix = VolumePrefix;
                            if (Host.Mount(MountPoint, null, true, DebugFlags) < 0)
                            {
                                Stfs = null;
                                stream.Position = 0;
                                Host = new FileSystemHost(Gdfx = new GdfxFileSystem(stream, ImagePath));
                                Host.Prefix = VolumePrefix;
                                if (Host.Mount(MountPoint, null, true, DebugFlags) < 0)
                                    throw new IOException("Cannot mount file system.");
                            }
                        }
                    }

                    MountPoint = Host.MountPoint();
                    _Host = Host;

                    if (openExplorer)
                        System.Diagnostics.Process.Start("explorer.exe", MountPoint);

                    Log(EVENTLOG_INFORMATION_TYPE, String.Format("{0}{1}{2} -i {3} -m {4}",
                        PROGNAME,
                        null != VolumePrefix && 0 < VolumePrefix.Length ? " -u " : "",
                            null != VolumePrefix && 0 < VolumePrefix.Length ? VolumePrefix : "",
                        ImagePath,
                        MountPoint));

                    Console.Title = $"{MountPoint} - xbox-winfsp";
                    Console.WriteLine($"\r\n{ImagePath}:\r\n Mounted to {MountPoint}, hit CTRL+C in this window to unmount.\r\n");
                }
                else if(VolumePrefix == null && ImagePath == null)
                {
                    _Hosts = new List<FileSystemHost>();
                    int _HostsCount = 0;
                    string connectedDrives = "";
                    if (Utility.IsAdministrator())
                    {
                        Log(EVENTLOG_INFORMATION_TYPE, "Loading Xbox partitions from physical drives...");
                        for (int i = 1; i < 11; i++)
                        {
                            try
                            {
                                VirtualFileSystem vfs;
                                var path = string.Format(@"\\.\PhysicalDrive{0:D}", i);
                                var stream = new DeviceStream(path);
                                Host = new FileSystemHost(vfs = new VirtualFileSystem(stream, path));
                                Host.Prefix = null;
                                if (Host.Mount(MountPoint, null, true, DebugFlags) < 0)
                                {
                                    //Console.WriteLine($"\r\nCannot mount physical drive {i}");
                                } else
                                {
                                    _Hosts.Add(Host);
                                    _HostsCount += vfs.RootFiles.Count;
                                }
                            }
                            catch
                            { }
                        }
                        Log(EVENTLOG_INFORMATION_TYPE, $"Loaded {_HostsCount} Xbox partitions from drives.");
                    }
                    if (_HostsCount <= 0)
                        throw new CommandLineUsageException();

                    Console.Title = $"HDD {connectedDrives}- xbox-winfsp";
                    Console.WriteLine("\r\nHit CTRL+C in this window to unmount.");
                } else
                {
                    throw new CommandLineUsageException();
                }
            }
            catch (CommandLineUsageException ex)
            {
                Log(EVENTLOG_ERROR_TYPE, String.Format(
                    "{0}" +
                    "usage: {1} OPTIONS\n" +
                    "\n" +
                    "options:\n" +
                    "    -d DebugFlags           [-1: enable all debug logs]\n" +
                    "    -D DebugLogFile         [file path; use - for stderr]\n" +
                    "    -i ImagePath            [path to GDFX/STFS image to be mounted]\n" +
                    "    -u \\Server\\ImagePath    [UNC prefix (single backslash)]\n" +
                    "    -m MountPoint           [X:|*|directory]\n" +
                    "    -s                      [installs xbox-winfsp filesystems, may need elevation!]\n" +
                    "    -r                      [removes any xbox-winfsp filesystems, may need elevation!]\n",
                    ex.HasMessage ? ex.Message + "\n" : "",
                    PROGNAME)); ;
                throw;
            }
            //}
            //catch (Exception ex)
            //{
           //     Log(EVENTLOG_ERROR_TYPE, String.Format("{0}", ex.Message));
           //     throw;
           // }
        }

        protected override void OnStop()
        {
            if(_Host != null)
            {
                _Host.Unmount();
                _Host = null;
            }
            if(_Hosts != null)
            {
                for(int i = _Hosts.Count - 1; i >= 0; i--)
                {
                    if (_Hosts[i] != null)
                        _Hosts[i].Unmount();
                    _Hosts.RemoveAt(i);
                }
            }
        }

        private static void argtos(String[] Args, ref int I, ref String V)
        {
            if (Args.Length > ++I)
                V = Args[I];
            else
                throw new CommandLineUsageException();
        }

        private static void argtol(String[] Args, ref int I, ref UInt32 V)
        {
            Int32 R;
            if (Args.Length > ++I)
                V = Int32.TryParse(Args[I], out R) ? (UInt32)R : V;
            else
                throw new CommandLineUsageException();
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            Environment.ExitCode = new XboxFsService().Run();
        }
    }
}
