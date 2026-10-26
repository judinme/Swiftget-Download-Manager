namespace SwiftGet.Engine.Network;

public class MirrorFailoverHandler : IDisposable
{
    private readonly List<string> _mirrors = [];
    private int _currentIndex;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _isDisposed;

    public string CurrentUrl
    {
        get
        {
            _lock.Wait();
            try
            {
                return _mirrors.Count > 0 ? _mirrors[_currentIndex] : string.Empty;
            }
            finally
            {
                _lock.Release();
            }
        }
    }

    public MirrorFailoverHandler(string primaryUrl, IEnumerable<string>? fallbackMirrors = null)
    {
        _mirrors.Add(primaryUrl);
        if (fallbackMirrors != null)
        {
            foreach (var m in fallbackMirrors)
            {
                if (!string.IsNullOrWhiteSpace(m) && !_mirrors.Contains(m))
                {
                    _mirrors.Add(m);
                }
            }
        }
    }

    public async Task<string?> TryFailoverAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_currentIndex + 1 < _mirrors.Count)
            {
                _currentIndex++;
                return _mirrors[_currentIndex];
            }
            return null; // Exhausted all mirrors
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _currentIndex = 0;
        }
        finally
        {
            _lock.Release();
        }
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _lock.Dispose();
        GC.SuppressFinalize(this);
    }
}
