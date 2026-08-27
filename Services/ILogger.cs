using System;

namespace DiskCleaner.Services
{
    public interface ILogger
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message, Exception ex = null);
        void Debug(string message);
    }

    /// <summary>
    /// 空日志实现：不写任何文件，仅满足 ILogger 接口。
    /// 移除文件日志功能后所有 _logger 调用均为空操作，避免程序目录生成 DiskCleaner.log。
    /// </summary>
    public class NullLogger : ILogger
    {
        public void Info(string message) { }
        public void Warning(string message) { }
        public void Error(string message, Exception ex = null) { }
        public void Debug(string message) { }
    }

    public static class Logger
    {
        private static ILogger _instance;
        private static readonly object _lock = new();

        public static void Initialize()
        {
            lock (_lock)
            {
                // 仅使用空实现，不生成任何日志文件
                _instance ??= new NullLogger();
            }
        }

        public static ILogger Instance => _instance ?? throw new InvalidOperationException("Logger not initialized.");
    }
}
