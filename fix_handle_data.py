import re

with open("KcpSharp/KcpConversation.cs", "r") as f:
    content = f.read()

start_index = content.find("private bool HandleData(")
end_index = content.find("private void AckPush(", start_index)

handle_data_new = """private bool HandleData(KcpPacketHeader header, ReadOnlySpan<byte> data, System.Buffers.IMemoryOwner<byte>? originalBuffer, int dataOffsetInBuffer)
    {
        var serialNumber = header.SerialNumber;
        if (TimeDiff(serialNumber, _rcv_nxt + _rcv_wnd) >= 0 || TimeDiff(serialNumber, _rcv_nxt) < 0) return false;

        var mutated = false;
        lock (_rcvBufLock)
        {
            if (TransportClosed) return false;

            int index = (int)(serialNumber % (uint)_rcvBufArray.Length);
            ref var itemRef = ref _rcvBufArray[index];

            if (!itemRef.IsEmpty && itemRef.Segment.SerialNumber == serialNumber)
            {
                return false; // Duplicate
            }

            // Copy data and insert
            KcpBuffer kcpBuffer;
            if (originalBuffer is not null)
            {
                // We keep a reference to the same buffer and share memory ownership
                kcpBuffer = KcpBuffer.CreateFromSharedOwner(originalBuffer, dataOffsetInBuffer, data.Length);
            }
            else
            {
                // If it came from a pooled array but without a shared owner, we must rent our own and copy it
                var rented = _bufferPool.Rent(new KcpBufferPoolRentOptions(data.Length, false));
                data.CopyTo(rented.Memory.Span);
                kcpBuffer = KcpBuffer.CreateFromSpan(rented, rented.Memory.Slice(0, data.Length));
            }

            // In case of aliasing (which shouldn't happen due to window constraints), release the old one
            if (!itemRef.IsEmpty)
            {
                itemRef.Data.Release();
            }

            itemRef = new KcpSendReceiveBufferItem
            {
                Data = kcpBuffer,
                Segment = DuplicateHeader(ref header, 0, 0, 0),
                IsEmpty = false
            };

            mutated = true;

            // move available data from rcv_buf -> rcv_queue
            while (_receiveQueue.GetQueueSize() < _rcv_wnd)
            {
                int nxtIndex = (int)(_rcv_nxt % (uint)_rcvBufArray.Length);
                ref var nxtItemRef = ref _rcvBufArray[nxtIndex];

                if (!nxtItemRef.IsEmpty && nxtItemRef.Segment.SerialNumber == _rcv_nxt)
                {
                    _receiveQueue.Enqueue(nxtItemRef.Data, nxtItemRef.Segment.Fragment);

                    nxtItemRef.Data = default;
                    nxtItemRef.IsEmpty = true;
                    _rcv_nxt++;
                    mutated = true;
                }
                else
                {
                    break;
                }
            }
        }

        return mutated;
    }

    """

content = content[:start_index] + handle_data_new + content[end_index:]

with open("KcpSharp/KcpConversation.cs", "w") as f:
    f.write(content)
