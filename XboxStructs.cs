using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Runtime.InteropServices;

namespace XboxWinFsp
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct CONSOLE_PUBLIC_KEY
    {
        public uint PublicExponent;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public byte[] Modulus;

        public void EndianSwap()
        {
            PublicExponent = PublicExponent.EndianSwap();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct XE_CONSOLE_CERTIFICATE
    {
        public ushort CertSize;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public byte[] ConsoleId;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 11)]
        public byte[] ConsolePartNumber;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public byte[] Reserved;
        public ushort Privileges;
        public uint ConsoleType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
        public byte[] ManufacturingDate;
        public CONSOLE_PUBLIC_KEY ConsolePublicKey;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public byte[] Signature;

        public bool IsStructureValid
        {
            get
            {
                return CertSize == 0x1A8;
            }
        }

        public void EndianSwap()
        {
            CertSize = CertSize.EndianSwap();
            Privileges = Privileges.EndianSwap();
            ConsoleType = ConsoleType.EndianSwap();
            ConsolePublicKey.EndianSwap();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct XE_CONSOLE_ID
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 5)]
        public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi, Pack = 1)]
    public struct XE_CONSOLE_SIGNATURE
    {
        public XE_CONSOLE_CERTIFICATE Cert;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)]
        public byte[] Signature;

        public bool IsStructureValid
        {
            get
            {
                return Cert.IsStructureValid;
            }
        }

        public void EndianSwap()
        {
            Cert.EndianSwap();
        }
    }
}
