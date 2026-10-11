using System;
using UnityEngine;

namespace Arena.Logging
{
    // TODO : Debug.log -> logger.Log()로 바꾸기
    public class UnityLogger : IGameLogger
    {
        private const string TimestampFormat = "HH:mm:ss.fff";

        public void Log(LogLevel level, string category, string message)
        {
            var timestamp = DateTime.Now.ToString(TimestampFormat);
            var formatted = FormatMessage(timestamp, level, category, message);
            
            switch (level)
            {
                case LogLevel.Trace:
                case LogLevel.Debug:
                case LogLevel.Info:
                    Debug.Log(formatted);
                    break;
                case LogLevel.Warning:
                    Debug.LogWarning(formatted);
                    break;
                case LogLevel.Error:
                case LogLevel.Critical:
                    Debug.LogError(formatted);
                    break;
            }
        }

        public void Log(LogLevel level, string category, string message, params object[] args)
        {
            try
            {
                var formatted = string.Format(message, args);
                Log(level, category, formatted);
            }
            catch (FormatException ex)
            {
                Debug.LogError($"[Logger] Format error: {ex.Message}");
                Log(level, category, message); // Fallback
            }
        }

        public void LogException(Exception exception, string context = "")
        {
            var message = string.IsNullOrEmpty(context) 
                ? $"Exception: {exception.Message}" 
                : $"Exception in {context}: {exception.Message}";
            
            Debug.LogException(exception);
            Log(LogLevel.Error, "Exception", message);
        }

        private string FormatMessage(string timestamp, LogLevel level, string category, string message)
        {
            var color = GetLevelColor(level);
            return $"<color={color}>[{timestamp}] [{level}] [{category}]</color> {message}";
        }

        private string GetLevelColor(LogLevel level)
        {
            return level switch
            {
                LogLevel.Trace => "grey",
                LogLevel.Debug => "cyan",
                LogLevel.Info => "white",
                LogLevel.Warning => "yellow",
                LogLevel.Error => "red",
                LogLevel.Critical => "magenta",
                _ => "white"
            };
        }
    }
}