using System;

namespace Arena.Network
{
    public class NetworkException : Exception
    {
        public NetworkErrorCode ErrorCode { get; }
        public bool IsRecoverable { get; }

        public NetworkException(string message, NetworkErrorCode errorCode, bool isRecoverable = false)
            : base(message)
        {
            ErrorCode = errorCode;
            IsRecoverable = isRecoverable;
        }

        public NetworkException(string message, NetworkErrorCode errorCode, Exception innerException)
            : base(message, innerException)
        {
            ErrorCode = errorCode;
            IsRecoverable = false;
        }
    }

    public enum NetworkErrorCode
    {
        None = 0,
        ServerStartFailed = 1,
        ConnectionTimeout = 2,
        ConnectionFailed = 3,
        PacketCreationFailed = 4,
    }
}
