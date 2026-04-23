using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace YtDownloader;

public sealed class DownloadJob : INotifyPropertyChanged
{
    public string Url { get; }
    public CancellationTokenSource Cts { get; } = new();

    public DownloadJob(string url) { Url = url; }

    private string? _title;
    public string? Title
    {
        get => _title;
        set { _title = value; Notify(); Notify(nameof(Display)); }
    }

    public string Display => string.IsNullOrEmpty(_title) ? Url : _title!;

    private double _percent;
    public double Percent
    {
        get => _percent;
        set { _percent = value; Notify(); }
    }

    private string _status = "Queued";
    public string Status
    {
        get => _status;
        set { _status = value; Notify(); }
    }

    private bool _isActive = true;
    public bool IsActive
    {
        get => _isActive;
        set { _isActive = value; Notify(); Notify(nameof(ActionText)); }
    }

    public string ActionText => _isActive ? "Cancel" : "Remove";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
