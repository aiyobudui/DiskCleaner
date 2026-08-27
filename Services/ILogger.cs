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
                
                _instance ??= new NullLogger();
            }
        }

        public static ILogger Instance => _instance ?? throw new InvalidOperationException("Logger not initialized.");
    }
}
