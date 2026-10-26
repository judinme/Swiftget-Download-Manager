using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SwiftGet.Engine.IO;

public sealed class SparseFileWriter : ISparseFileWriter
{
    private readonly string _filePath;
    private FileStream? _fileStream;
    private SafeFileHandle? _handle;
    private long _fileLength;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private bool _isDisposed;

    public long FileLength => _fileLength;
    public string FilePath => _filePath;

    public SparseFileWriter(string filePath)
    {
        _filePath = filePath;
    }

    public async Task PreallocateAsync(long totalBytes, CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();

            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (_fileStream == null)
            {
                _fileStream = new FileStream(
                    _filePath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    FileOptions.Asynchronous);

                _handle = _fileStream.SafeFileHandle;
            }

            // Attempt to enable Windows NTFS sparse file support
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && _handle != null && !_handle.IsInvalid)
            {
                TrySetSparseWindows(_handle);
            }

            if (totalBytes > 0)
            {
                // Do not truncate downloaded bytes on resume if file is already at least totalBytes
                if (_fileStream.Length < totalBytes)
                {
                    _fileStream.SetLength(totalBytes);
                }
                _fileLength = Math.Max(_fileStream.Length, totalBytes);
            }
            else
            {
                _fileLength = _fileStream.Length;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public ValueTask WriteAtAsync(long offset, ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        EnsureNotDisposed();

        var handle = _handle;
        if (handle == null || handle.IsInvalid || handle.IsClosed)
        {
            throw new InvalidOperationException("SparseFileWriter is not initialized or has been closed. Call PreallocateAsync first.");
        }

        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset must be non-negative.");
        }

        // RandomAccess.WriteAsync is thread-safe and non-blocking for concurrent disjoint offsets
        return RandomAccess.WriteAsync(handle, buffer, offset, ct);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        await _lifecycleLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureNotDisposed();
            if (_fileStream != null)
            {
                await _fileStream.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async Task CloseAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
    }

    private void EnsureNotDisposed()
    {
        ObjectDisposedException.ThrowIf(_isDisposed, nameof(SparseFileWriter));
    }

    private static void TrySetSparseWindows(SafeFileHandle handle)
    {
        try
        {
            const int FSCTL_SET_SPARSE = 0x000900C4;
            DeviceIoControl(
                handle,
                FSCTL_SET_SPARSE,
                IntPtr.Zero,
                0,
                IntPtr.Zero,
                0,
                out _,
                IntPtr.Zero);
        }
        catch
        {
            // Silently ignore if filesystem does not support sparse files (e.g. exFAT/FAT32)
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        uint nInBufferSize,
        IntPtr lpOutBuffer,
        uint nOutBufferSize,
        out uint lpBytesReturned,
        IntPtr lpOverlapped);

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed) return;

        await _lifecycleLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_isDisposed) return;
            _isDisposed = true;

            if (_fileStream != null)
            {
                await _fileStream.FlushAsync().ConfigureAwait(false);
                await _fileStream.DisposeAsync().ConfigureAwait(false);
                _fileStream = null;
                _handle = null;
            }
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }
}
