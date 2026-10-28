using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Oire.SharpSync.Storage;

namespace FolderSyncEr.Client.Sync.Storage;

/// <summary>
/// Stream wrapper that reports byte-level progress through an event handler.
/// </summary>
public sealed class ProgressTrackingStream : Stream
{
    private readonly Stream _innerStream;
    private readonly string _path;
    private readonly long _totalBytes;
    private readonly StorageOperation _operation;
    private readonly Action<StorageProgressEventArgs> _reportProgress;
    private long _bytesTransferred;

    public ProgressTrackingStream(
        Stream innerStream,
        string path,
        long totalBytes,
        StorageOperation operation,
        Action<StorageProgressEventArgs> reportProgress)
    {
        _innerStream = innerStream ?? throw new ArgumentNullException(nameof(innerStream));
        _path = path;
        _totalBytes = totalBytes;
        _operation = operation;
        _reportProgress = reportProgress ?? throw new ArgumentNullException(nameof(reportProgress));
    }

    public override bool CanRead => _innerStream.CanRead;
    public override bool CanSeek => _innerStream.CanSeek;
    public override bool CanWrite => _innerStream.CanWrite;
    public override long Length => _innerStream.Length;

    public override long Position
    {
        get => _innerStream.Position;
        set => _innerStream.Position = value;
    }

    public override void Flush() => _innerStream.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _innerStream.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var bytesRead = _innerStream.Read(buffer, offset, count);
        ReportBytes(bytesRead);
        return bytesRead;
    }

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, offset, count, cancellationToken);
        ReportBytes(bytesRead);
        return bytesRead;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var bytesRead = await _innerStream.ReadAsync(buffer, cancellationToken);
        ReportBytes(bytesRead);
        return bytesRead;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _innerStream.Write(buffer, offset, count);
        ReportBytes(count);
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await _innerStream.WriteAsync(buffer, offset, count, cancellationToken);
        ReportBytes(count);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _innerStream.WriteAsync(buffer, cancellationToken);
        ReportBytes(buffer.Length);
    }

    public override long Seek(long offset, SeekOrigin origin) => _innerStream.Seek(offset, origin);

    public override void SetLength(long value) => _innerStream.SetLength(value);

    private void ReportBytes(int count)
    {
        if (count <= 0) return;
        _bytesTransferred += count;
        var percent = _totalBytes > 0
            ? (int)Math.Clamp((_bytesTransferred * 100) / _totalBytes, 0, 100)
            : 0;

        _reportProgress(new StorageProgressEventArgs
        {
            Path = _path,
            BytesTransferred = _bytesTransferred,
            TotalBytes = _totalBytes,
            Operation = _operation,
            PercentComplete = percent
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _innerStream.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await _innerStream.DisposeAsync();
        await base.DisposeAsync();
    }
}

