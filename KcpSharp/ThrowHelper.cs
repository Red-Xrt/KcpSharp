using System.Diagnostics.CodeAnalysis;

namespace KcpSharp;

internal static class ThrowHelper
{
    [DoesNotReturn]
    internal static void ThrowArgumentOutOfRangeException(string paramName)
    {
        throw new ArgumentOutOfRangeException(paramName);
    }

    [DoesNotReturn]
    internal static void ThrowTransportClosedForStreamException()
    {
        throw NewTransportClosedForStreamException();
    }

    internal static Exception NewMessageTooLargeForBufferArgument()
    {
        return new ArgumentException("Message is too large.", "buffer");
    }

    internal static Exception NewBufferTooSmallForBufferArgument()
    {
        return new ArgumentException("Buffer is too small.", "buffer");
    }

    [DoesNotReturn]
    internal static void ThrowBufferTooSmall()
    {
        throw NewBufferTooSmallForBufferArgument();
    }

    [DoesNotReturn]
    internal static void ThrowAllowPartialSendArgumentException()
    {
        throw new ArgumentException("allowPartialSend should not be set to true in non-stream mode.",
            "allowPartialSend");
    }

    internal static Exception NewArgumentOutOfRangeException(string paramName)
    {
        return new ArgumentOutOfRangeException(paramName);
    }

    internal static Exception NewConcurrentSendException()
    {
        return new InvalidOperationException("Concurrent send operations are not allowed.");
    }

    internal static Exception NewConcurrentReceiveException()
    {
        return new InvalidOperationException("Concurrent receive operations are not allowed.");
    }

    internal static Exception NewTransportClosedForStreamException()
    {
        return new IOException("The underlying transport is closed.");
    }

    internal static Exception NewOperationCanceledExceptionForCancelPendingSend(Exception? innerException,
        CancellationToken cancellationToken)
    {
        return new OperationCanceledException("This operation is cancelled by a call to CancelPendingSend.",
            innerException, cancellationToken);
    }

    internal static Exception NewOperationCanceledExceptionForCancelPendingReceive(Exception? innerException,
        CancellationToken cancellationToken)
    {
        return new OperationCanceledException("This operation is cancelled by a call to CancelPendingReceive.",
            innerException, cancellationToken);
    }

    [DoesNotReturn]
    internal static void ThrowConcurrentReceiveException()
    {
        throw NewConcurrentReceiveException();
    }

    internal static Exception NewArgumentException_BufferMustHaveNonZeroLength()
    {
        return new ArgumentException("Buffer must have non-zero length in receive operations.", "buffer");
    }

    internal static Exception NewObjectDisposedForKcpStreamException()
    {
        return new ObjectDisposedException(nameof(KcpStream));
    }

    [DoesNotReturn]
    internal static void ThrowObjectDisposedForKcpStreamException()
    {
        throw NewObjectDisposedForKcpStreamException();
    }
}