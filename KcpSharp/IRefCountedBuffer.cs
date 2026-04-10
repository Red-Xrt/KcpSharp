namespace KcpSharp
{
    internal interface IRefCountedBuffer : IDisposable
    {
        IRefCountedBuffer Retain();
    }
}
