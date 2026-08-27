using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace DiskCleaner.Models
{
    
    
    
    
    public class OptimizeTool : INotifyPropertyChanged
    {
        public string Icon { get; set; }
        public string Name { get; set; }
        
        public string Detail { get; set; }
        
        public string Action { get; set; }
        
        public bool RequiresAdmin { get; set; }
        public ICommand Command { get; set; }
        
        public bool HasRestore { get; set; }
        
        public string RestoreDescription { get; set; }
        public ICommand RestoreCommand { get; set; }

        public ICommand CancelCommand { get; set; }

        public ICommand ClearCommand { get; set; }

        private string _statusText;
        
        public string StatusText
        {
            get => _statusText;
            set { if (_statusText != value) { _statusText = value; OnPropertyChanged(); } }
        }

        private bool _isRunning;
        
        public bool IsRunning
        {
            get => _isRunning;
            set { if (_isRunning != value) { _isRunning = value; OnPropertyChanged(); } }
        }

        private readonly ObservableCollection<string> _outputLines = new();
        public ObservableCollection<string> OutputLines => _outputLines;

        private bool _hasOutput;
        public bool HasOutput
        {
            get => _hasOutput;
            private set { if (_hasOutput != value) { _hasOutput = value; OnPropertyChanged(); } }
        }

        public void AppendOutput(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            _outputLines.Add(line);
            if (!_hasOutput) HasOutput = true;
        }

        public void ClearOutput()
        {
            _outputLines.Clear();
            if (_hasOutput) HasOutput = false;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
