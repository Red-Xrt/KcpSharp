namespace HyacineCore.Server.Kcp.KcpSharp;

internal static class KcpGlobalVars
{
#if !CONVID32
    internal const ushort CONVID_LENGTH = 8;
    internal const ushort HEADER_LENGTH_WITH_CONVID = 28;
    internal const ushort HEADER_LENGTH_WITHOUT_CONVID = 20;
#else
        internal const ushort HEADER_LENGTH_WITH_CONVID = 24;
        internal const ushort HEADER_LENGTH_WITHOUT_CONVID = 20;
#endif
}