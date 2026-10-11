using System;

namespace Arena.Logging
{
    public enum LogLevel
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4,
        Critical = 5
    }

    public interface IGameLogger
    {
        void Log(LogLevel level, string category, string message);
        void Log(LogLevel level, string category, string message, params object[] args);
        void LogException(Exception exception, string context = "");
    }
}