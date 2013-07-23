using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace EventStore.Core.TransactionLog.Chunks
{
    public class UnbufferedIOFileStream : Stream
    {
        private readonly FileStream _fileStream;
        private readonly byte[] _buffer;
        private readonly int _blockSize;
        private int _bufferedCount;
        private bool _aligned;
        private readonly byte[] _block;
        private long _lastPosition;

        private FileStream regular;
        private bool _needsFlush = false;

        private UnbufferedIOFileStream(FileStream internalFileStream, FileStream regular, int blockSize, int internalBufferSize)
        {
            this.regular = regular;
            _fileStream = internalFileStream;
            _buffer = new byte[internalBufferSize];
            _block = new byte[blockSize];
            _blockSize = blockSize;
        }

        public static UnbufferedIOFileStream Create(string path,
                                                    FileMode mode,
                                                    FileAccess acc,
                                                    FileShare share,
                                                    bool sequential,
                                                    int internalBufferSize)
        {
            var blockSize = GetDriveSectorSize.driveSectorSize(path);
            if (internalBufferSize % blockSize != 0) throw new Exception("buffer size must be aligned to block size of " + blockSize + " bytes");
            int flags = WinApi.FILE_FLAG_NO_BUFFERING;     // default to simmple no buffering
            /* Construct the proper 'flags' value to pass to CreateFile() */
            //if (sequential) flags |= WinApi.FILE_FLAG_SEQUENTIAL_SCAN;

            /* Call the Windows CreateFile() API to open the file */
            var handle = WinApi.CreateFile(path,
                                    (int)acc,
                                    FileShare.ReadWrite,
                                    IntPtr.Zero,
                                    mode,
                                    flags,
                                    IntPtr.Zero);
            FileStream stream = null;
            if (!handle.IsInvalid)
            {   /* Wrap the handle in a stream and return it to the caller */
                stream = new FileStream(handle, acc, internalBufferSize, false);
            }
            else                    // if create call failed to get a handle
            {                       // return a null pointer. 
                throw new Win32Exception();
            }
            var regular = new FileStream(path, mode, acc, FileShare.ReadWrite, 8096);
            return new UnbufferedIOFileStream(stream, regular, (int)blockSize, internalBufferSize);
        }

        public override void Flush()
        {
            if (!_needsFlush) return;
            var aligned = (int)GetLowestAlignment(_bufferedCount);
            var positionAligned = GetLowestAlignment(_lastPosition);
            if (!_aligned)
            {
                _fileStream.Seek(positionAligned, SeekOrigin.Begin);
            }
            if (_bufferedCount % _blockSize == 0)
            {
                _fileStream.Write(_buffer, 0, _bufferedCount);
                _fileStream.Flush(); //TODO use WriteFile here instead of filestream too weird to be copying/flushing
                _bufferedCount = 0;
                _lastPosition = _fileStream.Position;
                _aligned = true;
            }
            else
            {
                var left = _bufferedCount - aligned;
                Array.Clear(_block, 0, _block.Length);
                _fileStream.Write(_buffer, 0, aligned);
                Buffer.BlockCopy(_buffer, aligned, _block, 0, left); //TODO align in buffer for single write
                _fileStream.Write(_block, 0, _block.Length);
                _fileStream.Flush();
                _lastPosition = _fileStream.Position - 1;
                var toalign = GetLowestAlignment(_lastPosition);
                SetBuffer(toalign, left);
                _bufferedCount = left;
            }
            _needsFlush = false;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var aligned = GetLowestAlignment(offset);
            var left = (int)(offset - aligned);
            Flush();
            SetBuffer(aligned, left);
            return offset;
        }

        private long GetLowestAlignment(long offset)
        {
            return offset - (offset % _blockSize);
        }

        public override void SetLength(long value)
        {
            var aligned = GetLowestAlignment(value) + _blockSize;
            _fileStream.SetLength(aligned);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            regular.Position = _fileStream.Position + _bufferedCount;
            return regular.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _needsFlush = true;
            var done = false;
            var left = count;
            while (!done)
            {
                if (_bufferedCount + left < _buffer.Length)
                {
                    CopyBuffer(buffer, offset, left);
                    done = true;
                }
                else
                {
                    var toFill = _buffer.Length - _bufferedCount;
                    CopyBuffer(buffer, offset, toFill);
                    Flush();
                    left -= toFill;
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
            get { return _fileStream.Length; }
        }

        public override long Position
        {
            get
            {
                if (_aligned)
                    return _fileStream.Position + _bufferedCount;
                else
                    return GetLowestAlignment(_lastPosition) + _bufferedCount;
            }
            set { Seek(value, SeekOrigin.Begin); }
        }

        private void SetBuffer(long aligned, int left)
        {
            Buffer.BlockCopy(_buffer, _buffer.Length - left, _buffer, 0, left);
            _bufferedCount = left;
            _aligned = false;
        }

        protected override void Dispose(bool disposing)
        {
            regular.Dispose();
            _fileStream.Dispose();
        }
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

    internal static class WinApi
    {
        public const int FILE_FLAG_NO_BUFFERING = unchecked((int)0x20000000);
        public const int FILE_FLAG_OVERLAPPED = unchecked((int)0x40000000);
        public const int FILE_FLAG_SEQUENTIAL_SCAN = unchecked((int)0x08000000);

        [DllImport("KERNEL32", SetLastError = true, CharSet = CharSet.Auto, BestFitMapping = false)]
        public static extern bool GetDiskFreeSpace(string path,
                                            out uint sectorsPerCluster,
                                            out uint bytesPerSector,
                                            out uint numberOfFreeClusters,
                                            out uint totalNumberOfClusters);


        [DllImport("KERNEL32", BestFitMapping = true, CharSet = CharSet.Ansi)]
        public static extern bool WriteFile(
            IntPtr hFile,
            System.Text.StringBuilder lpBuffer,
            uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten,
            [In] ref System.Threading.NativeOverlapped lpOverlapped);

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

        [DllImport("Kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        public static extern unsafe uint SetFilePointer(
            [In] SafeFileHandle hFile,
            [In] int lDistanceToMove,
            [Out] int* lpDistanceToMoveHigh,
            [In] EMoveMethod dwMoveMethod);


    }
}
