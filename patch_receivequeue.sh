#!/bin/bash
cat << 'INNER_EOF' > p1.cs
using System;
using System.IO;

class Program
{
    static void Main()
    {
        string text = File.ReadAllText("../KcpSharp/KcpReceiveQueue.cs");

        string find = @"                var lastNode = _queue.Last;
                if (lastNode is not null && lastNode.ValueRef.Data.TryAppend(ref buffer, out var combined))
                {
                    // appended
                    if (lastNode.ValueRef.Fragment != 0)
                    {
                        if (lastNode.ValueRef.Fragment != PartiallyConsumedFragment)
                        {
                            Interlocked.Increment(ref _completedPacketsCount);
                        }
                        lastNode.ValueRef.Fragment = 0;
                    }
                    lastNode.ValueRef.Data = combined;
                    _totalBytesInQueue += buffer.Length;
                    appended = true;
                }";
        string replace = @"                var lastNode = _queue.Last;
                if (lastNode is not null && lastNode.ValueRef.Data.TryAppend(ref buffer, out var combined))
                {
                    // appended
                    if (lastNode.ValueRef.Fragment != 0)
                    {
                        if (lastNode.ValueRef.Fragment != PartiallyConsumedFragment)
                        {
                            Interlocked.Increment(ref _completedPacketsCount);
                        }
                        lastNode.ValueRef.Fragment = 0;
                    }
                    lastNode.ValueRef.Data = combined;
                    _totalBytesInQueue += buffer.Length;
                    appended = true;
                    buffer.Release(); // Manually release the rented buffer wrapper, as ownership of the memory was transferred/copied
                }";
        if (text.Contains(find)) {
            text = text.Replace(find, replace);
            Console.WriteLine("Fixed receive queue memory leak.");
        } else {
            Console.WriteLine("Did not find target string in KcpReceiveQueue.cs");
        }

        File.WriteAllText("../KcpSharp/KcpReceiveQueue.cs", text);
    }
}
INNER_EOF
dotnet new console -o PatchTool
mv p1.cs PatchTool/Program.cs
cd PatchTool && dotnet run && cd .. && rm -rf PatchTool
