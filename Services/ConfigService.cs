using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DiskCleaner.Models;

namespace DiskCleaner.Services
{
    public interface IConfigService
    {
        Task<Config> LoadAsync();
        Task SaveAsync(Config config);
    }

    public class ConfigService : IConfigService
    {
        private readonly string _configPath;

        public ConfigService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "DiskCleaner");
            Directory.CreateDirectory(dir);
            _configPath = Path.Combine(dir, "config.json");
        }

        public async Task<Config> LoadAsync()
        {
            if (!File.Exists(_configPath)) return new Config();
            var json = await File.ReadAllTextAsync(_configPath);
            return JsonSerializer.Deserialize<Config>(json) ?? new Config();
        }

        public async Task SaveAsync(Config config)
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(_configPath, json);
        }
    }
}
