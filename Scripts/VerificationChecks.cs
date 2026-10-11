using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Sockets;
using Arena.Logging;
using UnityEngine;

namespace Arena.Verification
{
    public static class VerificationChecks
    {
        public static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);

        public static void MustReject(Action check, string label)
        {
            try { check(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException($"Negative control was accepted: {label}");
        }
    }

    internal sealed class VerificationLog : IGameLogger, IDisposable
    {
        private readonly ConcurrentQueue<string> _errors = new();
        private readonly bool _captureUnity;

        internal VerificationLog(bool captureUnity = false)
        {
            _captureUnity = captureUnity;
            if (captureUnity) Application.logMessageReceivedThreaded += OnUnityLog;
        }

        public void Log(LogLevel level, string category, string message)
        {
            if (level >= LogLevel.Error) _errors.Enqueue($"{category}: {message}");
        }

        public void Log(LogLevel level, string category, string message, params object[] args) =>
            Log(level, category, string.Format(message, args));

        public void LogException(Exception exception, string context = "") =>
            _errors.Enqueue($"{context}: {exception}");

        private void OnUnityLog(string message, string stack, LogType type)
        {
            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _errors.Enqueue($"{type}: {message}\n{stack}");
        }

        internal void ConsumeExpected(string expected, int count)
        {
            int found = 0;
            var unexpected = new List<string>();
            while (_errors.TryDequeue(out string error))
            {
                if (error == expected) found++;
                else unexpected.Add(error);
            }
            foreach (string error in unexpected) _errors.Enqueue(error);
            if (found != count)
                throw new InvalidOperationException($"Expected {count} matching errors, received {found}: {expected}");
        }

        internal void ThrowIfErrors()
        {
            var errors = new List<string>();
            while (_errors.TryDequeue(out string error)) errors.Add(error);
            if (errors.Count != 0) throw new InvalidOperationException(string.Join("\n", errors));
        }

        internal void ConsumeExpectedConnectionReset()
        {
            string[] resetMessages =
            {
                new SocketException((int)SocketError.ConnectionReset).Message.Trim(),
                new System.ComponentModel.Win32Exception((int)SocketError.ConnectionReset).Message.Trim(),
                "현재 연결은 원격 호스트에 의해 강제로 끊겼습니다.",
                "An existing connection was forcibly closed by the remote host."
            };
            int exceptions = 0, contextLogs = 0;
            var unexpected = new List<string>();
            while (_errors.TryDequeue(out string error))
            {
                if (Array.Exists(resetMessages, reset => error.Trim() == "Exception: SocketException: " + reset)) exceptions++;
                else if (error.StartsWith("Error: ") &&
                    error.Contains("[Error] [Exception]</color> Exception in TcpReceiveLoop: ") &&
                    Array.Exists(resetMessages, reset => error.TrimEnd().EndsWith(reset))) contextLogs++;
                else unexpected.Add(error);
            }
            foreach (string error in unexpected) _errors.Enqueue(error);
            if (exceptions != contextLogs || exceptions > 1)
                throw new InvalidOperationException($"Unexpected reset log counts: {exceptions}/{contextLogs}");
            ThrowIfErrors();
        }

        public void Dispose()
        {
            if (_captureUnity) Application.logMessageReceivedThreaded -= OnUnityLog;
        }
    }
}
