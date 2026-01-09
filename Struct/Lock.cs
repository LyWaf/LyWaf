namespace LyWaf.Struct;

public class ReadLockScope : IDisposable
{
    private readonly ReaderWriterLockSlim _lock;

    public ReadLockScope(ReaderWriterLockSlim lockObj)
    {
        _lock = lockObj;
        _lock.EnterReadLock();
    }

    public void Dispose()
    {
        _lock.ExitReadLock();
        GC.SuppressFinalize(this);
    }
}

public class WriteLockScope : IDisposable
{
    private readonly ReaderWriterLockSlim _lock;

    public WriteLockScope(ReaderWriterLockSlim lockObj)
    {
        _lock = lockObj;
        _lock.EnterWriteLock();
    }

    public void Dispose()
    {
        _lock.ExitWriteLock();
        GC.SuppressFinalize(this);
    }
}


public class LockScope : IDisposable
{
    private readonly Lock _lock;

    public LockScope(Lock lockObj)
    {
        _lock = lockObj;
        _lock.Enter();
    }

    public void Dispose()
    {
        _lock.Exit();
        GC.SuppressFinalize(this);
    }
}