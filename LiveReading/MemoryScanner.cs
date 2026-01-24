using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace OsuGrind.LiveReading
{
    public class MemoryScanner : IDisposable
    {
        private readonly Process _process;
        private readonly IntPtr _processHandle;

        public void Dispose()
        {
            if (_processHandle != IntPtr.Zero)
            {
                Win32.CloseHandle(_processHandle);
            }
            GC.SuppressFinalize(this);
        }

        public MemoryScanner(Process process)
        {
            _process = process;
            _processHandle = Win32.OpenProcess(Win32.PROCESS_VM_READ | Win32.PROCESS_QUERY_INFORMATION, false, process.Id);
        }

        ~MemoryScanner()
        {
            if (_processHandle != IntPtr.Zero)
            {
                Win32.CloseHandle(_processHandle);
            }
        }

        private byte[] _scanBuffer = new byte[4 * 1024 * 1024];

        public IntPtr Scan(string pattern, bool nonZeroMask = false, bool imageOnly = false, bool executableOnly = false, bool privateOnly = false)
        {
            if (_processHandle == IntPtr.Zero) return IntPtr.Zero;
            var patternBytes = ParsePattern(pattern);
            if (patternBytes.Length == 0) return IntPtr.Zero;

            long address = 0;
            Win32.MEMORY_BASIC_INFORMATION mbi;
            uint mbiSize = (uint)Marshal.SizeOf(typeof(Win32.MEMORY_BASIC_INFORMATION));

            try
            {
                while (Win32.VirtualQueryEx(_processHandle, (IntPtr)address, out mbi, mbiSize) != 0)
                {
                    if ((mbi.State == Win32.MEM_COMMIT) && (mbi.Protect != Win32.PAGE_NOACCESS) && ((mbi.Protect & Win32.PAGE_GUARD) == 0))
                    {
                        if (imageOnly && mbi.Type != Win32.MEM_IMAGE) goto next;
                        if (privateOnly && mbi.Type != Win32.MEM_PRIVATE) goto next;
                        long regionOffset = 0;
                        while (regionOffset < (long)mbi.RegionSize)
                        {
                            long bytesToRead = Math.Min(_scanBuffer.Length, (long)mbi.RegionSize - regionOffset);
                            if (Win32.ReadProcessMemory(_processHandle, IntPtr.Add(mbi.BaseAddress, (int)regionOffset), _scanBuffer, (int)bytesToRead, out IntPtr read))
                            {
                                int match = IndexOfPattern(_scanBuffer, (int)read, patternBytes, nonZeroMask);
                                if (match != -1) return IntPtr.Add(mbi.BaseAddress, (int)regionOffset + match);
                                regionOffset += Math.Max(1, (int)read - patternBytes.Length);
                            }
                            else break;
                        }
                    }
                    next: address = (long)mbi.BaseAddress + (long)mbi.RegionSize;
                }
            } catch (Exception ex) {
                OsuGrind.Services.DebugService.Log($"MemoryScanner Scan Error: {ex.Message}", "MemoryScanner");
            }
            return IntPtr.Zero;
        }

        public List<IntPtr> ScanAll(string pattern, bool nonZeroMask = false, bool imageOnly = false, bool executableOnly = false, bool privateOnly = false)
        {
            var results = new List<IntPtr>();
            if (_processHandle == IntPtr.Zero) return results;
            var patternBytes = ParsePattern(pattern);
            long address = 0;
            Win32.MEMORY_BASIC_INFORMATION mbi;
            uint mbiSize = (uint)Marshal.SizeOf(typeof(Win32.MEMORY_BASIC_INFORMATION));
            byte[] buffer = new byte[4 * 1024 * 1024];
            while (Win32.VirtualQueryEx(_processHandle, (IntPtr)address, out mbi, mbiSize) != 0)
            {
                if ((mbi.State == Win32.MEM_COMMIT) && (mbi.Protect != Win32.PAGE_NOACCESS) && ((mbi.Protect & Win32.PAGE_GUARD) == 0))
                {
                    long regionOffset = 0;
                    while (regionOffset < (long)mbi.RegionSize)
                    {
                        long bytesToRead = Math.Min(buffer.Length, (long)mbi.RegionSize - regionOffset);
                        if (Win32.ReadProcessMemory(_processHandle, IntPtr.Add(mbi.BaseAddress, (int)regionOffset), buffer, (int)bytesToRead, out IntPtr read))
                        {
                            int bytesRead = (int)read;
                            int searchOffset = 0;
                            while (searchOffset < bytesRead)
                            {
                                int match = IndexOfPattern(buffer, bytesRead, patternBytes, nonZeroMask, searchOffset);
                                if (match != -1) { results.Add(IntPtr.Add(mbi.BaseAddress, (int)regionOffset + match)); searchOffset = match + 1; }
                                else break;
                            }
                            regionOffset += Math.Max(1, bytesRead - patternBytes.Length);
                        } else break;
                    }
                }
                address = (long)mbi.BaseAddress + (long)mbi.RegionSize;
            }
            return results;
        }

        private int IndexOfPattern(byte[] buffer, int length, byte?[] pattern, bool nonZeroMask, int startIndex = 0)
        {
            int patternLength = pattern.Length;
            for (int i = startIndex; i <= length - patternLength; i++)
            {
                bool found = true;
                for (int j = 0; j < patternLength; j++)
                {
                    var p = pattern[j];
                    if (p.HasValue) { if (buffer[i + j] != p.Value) { found = false; break; } }
                    else if (nonZeroMask && buffer[i + j] == 0) { found = false; break; }
                }
                if (found) return i;
            }
            return -1;
        }

        private bool IsTarget32Bit() { try { Win32.IsWow64Process(_processHandle, out bool isWow64); return isWow64; } catch { return false; } }

        public IntPtr ReadIntPtr(IntPtr address)
        {
            int size = IsTarget32Bit() ? 4 : 8;
            byte[] buffer = new byte[size];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, size, out _))
            {
                if (size == 4)
                {
                    uint val = BitConverter.ToUInt32(buffer, 0);
                    return new IntPtr((long)val);
                }
                else
                {
                    return new IntPtr(BitConverter.ToInt64(buffer, 0));
                }
            }
            return IntPtr.Zero;
        }

        public int ReadInt32(IntPtr address)
        {
            byte[] buffer = new byte[4];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 4, out _)) return BitConverter.ToInt32(buffer, 0);
            return 0;
        }

        public uint ReadUInt32(IntPtr address)
        {
            byte[] buffer = new byte[4];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 4, out _)) return BitConverter.ToUInt32(buffer, 0);
            return 0;
        }

        public uint ReadXorInt32(IntPtr address)
        {
            byte[] buffer = new byte[8];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 8, out _))
            {
                uint v1 = BitConverter.ToUInt32(buffer, 0);
                uint v2 = BitConverter.ToUInt32(buffer, 4);
                return v1 ^ v2;
            }
            return 0;
        }

        public short ReadInt16(IntPtr address)
        {
            byte[] buffer = new byte[2];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 2, out _)) return BitConverter.ToInt16(buffer, 0);
            return 0;
        }

        public ushort ReadUInt16(IntPtr address)
        {
            byte[] buffer = new byte[2];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 2, out _)) return BitConverter.ToUInt16(buffer, 0);
            return 0;
        }

        public long ReadInt64(IntPtr address)
        {
            byte[] buffer = new byte[8];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 8, out _)) return BitConverter.ToInt64(buffer, 0);
            return 0;
        }

        public double ReadDouble(IntPtr address)
        {
            byte[] buffer = new byte[8];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 8, out _)) return BitConverter.ToDouble(buffer, 0);
            return 0;
        }

        public float ReadFloat(IntPtr address)
        {
            byte[] buffer = new byte[4];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 4, out _)) return BitConverter.ToSingle(buffer, 0);
            return 0;
        }

        public byte ReadByte(IntPtr address)
        {
            byte[] buffer = new byte[1];
            if (Win32.ReadProcessMemory(_processHandle, address, buffer, 1, out _)) return buffer[0];
            return 0;
        }

        public string ReadString(IntPtr address)
        {
            if (address == IntPtr.Zero) return "";
            bool is32 = IsTarget32Bit();
            int lenOff = is32 ? 0x4 : 0x8;
            int datOff = is32 ? 0x8 : 0xC;
            int length = ReadInt32(IntPtr.Add(address, lenOff));
            
            if (length < 0 || length > 2000) return "";
            if (length == 0) return "";

            byte[] buffer = new byte[length * 2];
            if (Win32.ReadProcessMemory(_processHandle, IntPtr.Add(address, datOff), buffer, length * 2, out IntPtr readPtr)) 
            {
                return Encoding.Unicode.GetString(buffer, 0, (int)readPtr);
            }
            return "";
        }

        private byte?[] ParsePattern(string pattern)
        {
            if (string.IsNullOrWhiteSpace(pattern)) return Array.Empty<byte?>();
            return pattern.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                          .Select(s => (s == "??" || s == "?") ? (byte?)null : Convert.ToByte(s, 16))
                          .ToArray();
        }
    }
}
