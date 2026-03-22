using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace HyacineCore.Server.Kcp.KcpSharp;

// Interface nội bộ, không public, không thay đổi API gốc
internal interface IKcpBatchTransport
{
    // Ghi một packet vào batch buffer. Trả về slice trong batch buffer
    // để FlushCoreAsync viết dữ liệu vào đó.
    bool TryGetBatchSlice(int requiredSize, out Memory<byte> slice, out int slotIndex);
    
    // Đánh dấu slot đã sẵn sàng để gửi với endpoint cụ thể
    void CommitBatchSlot(int slotIndex, int actualSize, IPEndPoint endpoint);
    
    // Gửi tất cả slot đã commit trong một lần gọi kernel
    ValueTask FlushBatchAsync(CancellationToken cancellationToken);
    
    // Số slot còn trống trong batch
    int BatchCapacity { get; }
}
