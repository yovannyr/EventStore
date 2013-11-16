using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EventStore.Core.TransactionLog.Chunks
{
    public unsafe class UnbufferedIOFileStream : Stream
    {
        private readonly byte[] _buffer;
        private readonly int _blockSize;
        private int _bufferedCount;
        private bool _aligned;
        private readonly byte[] _block;
        private long _lastPosition;
        private bool _needsFlush;
        private readonly FileStream _regular;
        private readonly SafeFileHandle _handle;

        private UnbufferedIOFileStream(FileStream regular, SafeFileHandle handle, int blockSize, int internalBufferSize)
        {
            _regular = regular;
            _handle = handle;
            _buffer = new byte[internalBufferSize];
            _block = new byte[blockSize];
            _blockSize = blockSize;
        }

        public static UnbufferedIOFileStream Create(string path,
                                                    FileMode mode,
                                                    FileAccess acc,
                                                    FileShare share,
                                                    bool sequential,
                                                    int internalBufferSize,
                                                    bool writeThrough,
                                                    uint minBlockSize)
        {
            var blockSize = GetDriveSectorSize.driveSectorSize(path);
            blockSize = blockSize > minBlockSize ? blockSize : minBlockSize;
            if (internalBufferSize % blockSize != 0) throw new Exception("buffer size must be aligned to block size of " + blockSize + " bytes");
            int flags = WinApi.FILE_FLAG_NO_BUFFERING;
            if (writeThrough) flags = flags | WinApi.FILE_FLAG_WRITE_THROUGH;
            /* Construct the proper 'flags' value to pass to CreateFile() */
            //if (sequential) flags ;

            /* Call the Windows CreateFile() API to open the file */
            var handle = WinApi.CreateFile(path,
                                    (int)acc,
                                    FileShare.ReadWrite,
                                    IntPtr.Zero,
                                    mode,
                                    flags,
                                    IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new Win32Exception();
            }
            var regular = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Read, FileShare.ReadWrite);
            return new UnbufferedIOFileStream(regular, handle, (int)blockSize, internalBufferSize);
        }

        public override void Flush()
        {
            if (!_needsFlush) return;
            var aligned = (int)GetLowestAlignment(_bufferedCount);
            var positionAligned = GetLowestAlignment(_lastPosition);
            if (!_aligned)
            {
                WinApi.SetFilePointer(_handle, (int)positionAligned, null, WinApi.EMoveMethod.Begin);
            }
            if (_bufferedCount % _blockSize == 0)
            {
                InternalWrite(_buffer, (uint)_bufferedCount);
                _lastPosition = positionAligned + _bufferedCount;
                _bufferedCount = 0;
                _aligned = true;
            }
            else
            {
                var left = _bufferedCount - aligned;

                InternalWrite(_buffer, (uint)(aligned + _blockSize)); //write ahead to next block (checkpoint handles)
                _lastPosition = positionAligned + aligned + left;
                SetBuffer(left);
                _bufferedCount = left;
            }
            _needsFlush = false;
        }

        private void InternalWrite(byte[] buffer, uint count)
        {
            int written = 0;
            if (!WinApi.WriteFile(_handle, buffer, count, ref written, IntPtr.Zero))
            {
                throw new Win32Exception();
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var aligned = GetLowestAlignment(offset);
            var left = (int)(offset - aligned);
            Flush();
            SetBuffer(left);
            return offset;
        }

        private long GetLowestAlignment(long offset)
        {
            return offset - (offset % _blockSize);
        }

        public override void SetLength(long value)
        {
            var aligned = GetLowestAlignment(value);
            aligned = aligned == value ? aligned : aligned + _blockSize;
            WinApi.SetFilePointer(_handle, (int)aligned, null, WinApi.EMoveMethod.Begin);
            if (!WinApi.SetEndOfFile(_handle))
            {
                throw new Win32Exception();
            }
            WinApi.FlushFileBuffers(_handle);
            Seek(0, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            _regular.Position = _lastPosition + _bufferedCount;
            return _regular.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var done = false;
            var left = count;
            var current = offset;
            while (!done)
            {
                _needsFlush = true;
                if (_bufferedCount + left < _buffer.Length)
                {
                    CopyBuffer(buffer, current, left);
                    done = true;
                    current += left;
                }
                else
                {
                    var toFill = _buffer.Length - _bufferedCount;
                    CopyBuffer(buffer, current, toFill);
                    Flush();
                    left -= toFill;
                    current += toFill;
                    done = left == 0;
                }
            }
        }

        private void CopyBuffer(byte[] buffer, int offset, int count)
        {
            Buffer.BlockCopy(buffer, offset, _buffer, _bufferedCount, count);
            _bufferedCount += count;
        }

        public override bool CanRead
        {
            get { return false; }
        }

        public override bool CanSeek
        {
            get { return true; }
        }

        public override bool CanWrite
        {
            get { return true; }
        }

        public override long Length
        {
            get { return _regular.Length; }
        }

        public override long Position
        {
            get
            {
                if (_aligned)
                    return _lastPosition + _bufferedCount;
                else
                    return GetLowestAlignment(_lastPosition) + _bufferedCount;
            }
            set { Seek(value, SeekOrigin.Begin); }
        }

        private void SetBuffer(int left)
        {
            Buffer.BlockCopy(_buffer, _buffer.Length - left, _buffer, 0, left);
            _bufferedCount = left;
            _aligned = false;
        }

        protected override void Dispose(bool disposing)
        {
            Flush();
            _regular.Dispose();
            _handle.Close();
        }
    }
    internal static class WinApi
    {
        public const int FILE_FLAG_NO_BUFFERING = unchecked((int)0x20000000);
        public const int FILE_FLAG_OVERLAPPED = unchecked((int)0x40000000);
        public const int FILE_FLAG_SEQUENTIAL_SCAN = unchecked((int)0x08000000);
        public const int FILE_FLAG_WRITE_THROUGH = unchecked((int)0x80000000);
        [DllImport("KERNEL32", SetLastError = true, CharSet = CharSet.Auto, BestFitMapping = false)]
        public static extern bool GetDiskFreeSpace(string path,
                                            out uint sectorsPerCluster,
                                            out uint bytesPerSector,
                                            out uint numberOfFreeClusters,
                                            out uint totalNumberOfClusters);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool WriteFile(
         SafeFileHandle hFile,
         Byte[] aBuffer,
         UInt32 cbToWrite,
         ref int cbThatWereWritten,
         IntPtr pOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern UInt32 SetFilePointer(
         SafeFileHandle hFile,
         Int32 cbDistanceToMove,
         IntPtr pDistanceToMoveHigh,
         EMoveMethod fMoveMethod);

        [DllImport("KERNEL32", SetLastError = true, CharSet = CharSet.Auto, BestFitMapping = false)]
        public static extern SafeFileHandle CreateFile(String fileName,
                                                       int desiredAccess,
                                                       FileShare shareMode,
                                                       IntPtr securityAttrs,
                                                       FileMode creationDisposition,
                                                       int flagsAndAttributes,
                                                       IntPtr templateFile);
        public enum EMoveMethod : uint
        {
            Begin = 0,
            Current = 1,
            End = 2
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool SetEndOfFile(
         SafeFileHandle hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        internal static extern bool FlushFileBuffers(SafeFileHandle filehandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern unsafe uint SetFilePointer(
            [In] SafeFileHandle hFile,
            [In] int lDistanceToMove,
            [Out] int* lpDistanceToMoveHigh,
            [In] EMoveMethod dwMoveMethod);


    }
    public class GetDriveSectorSize
    {
        /// <summary>
        /// Return the sector size of the volume the specified filepath lives on.
        /// </summary>
        /// <param name="path">UNC path name for the file or directory</param>
        /// <returns>device sector size in bytes </returns>
        public static uint driveSectorSize(string path)
        {
            uint size = 512; // sector size in bytes. 
            uint toss;       // ignored outputs
            WinApi.GetDiskFreeSpace(Path.GetPathRoot(path), out toss, out size, out toss, out toss);
            return size;
        }
    }
}
