using System;
using System.IO;
using System.Runtime.InteropServices;

namespace XboxWinFsp
{
    public class FileSystemParseException : Exception
    {
        public FileSystemParseException(String Message = null) : base(Message)
        {
            HasMessage = Message != null;
        }

        public bool HasMessage;
    }

    static class Utility
    {
        public static T ReadStruct<T>(this Stream stream)
        {
            var size = Marshal.SizeOf(typeof(T));

            // Read in a byte array
            byte[] bytes = new byte[size];
            stream.Read(bytes, 0, size);

            return BytesToStruct<T>(bytes);
        }

        public static T BytesToStruct<T>(byte[] bytes)
        {
            // Pin the managed memory while, copy it out the data, then unpin it
            var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            var theStructure = (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
            handle.Free();

            return theStructure;
        }

        public static short EndianSwap(this short num)
        {
            byte[] data = BitConverter.GetBytes(num);
            Array.Reverse(data);
            return BitConverter.ToInt16(data, 0);
        }

        public static ushort EndianSwap(this ushort num)
        {
            byte[] data = BitConverter.GetBytes(num);
            Array.Reverse(data);
            return BitConverter.ToUInt16(data, 0);
        }

        public static int EndianSwap(this int num)
        {
            byte[] data = BitConverter.GetBytes(num);
            Array.Reverse(data);
            return BitConverter.ToInt32(data, 0);
        }

        public static uint EndianSwap(this uint num)
        {
            byte[] data = BitConverter.GetBytes(num);
            Array.Reverse(data);
            return BitConverter.ToUInt32(data, 0);
        }

        public static ulong EndianSwap(this ulong num)
        {
            byte[] data = BitConverter.GetBytes(num);
            Array.Reverse(data);
            return BitConverter.ToUInt64(data, 0);
        }

        public static bool BytesMatch(this byte[] b1, byte[] b2)
        {
            if (b1.Length != b2.Length)
                return false;
            for(int i = 0; i < b1.Length; i++)
            {
                if (b1[i] != b2[i])
                    return false;
            }
            return true;
        }

        public static bool IsNull(this byte[] b1)
        {
            foreach (var byt in b1)
                if (byt != 0)
                    return false;
            return true;
        }
    }
}
