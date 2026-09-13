using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;

namespace CleanFolderFinder;

/// <summary>
/// A discovered build output folder (bin or obj) that can be listed and deleted.
/// </summary>
public sealed class FolderItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public FolderItem(string path, string kind)
    {
        Path = path;
        Kind = kind;
    }

    public string Path { get; }

    /// <summary>"bin" or "obj".</summary>
    public string Kind { get; }

    private long _sizeBytes = -1;

    public long SizeBytes
    {
        get => _sizeBytes;
        set
        {
            _sizeBytes = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SizeDisplay));
        }
    }

    public string SizeDisplay => _sizeBytes < 0 ? "..." : FormatSize(_sizeBytes);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    /// <summary>Computes the total size of all files under the folder, ignoring errors.</summary>
    public static long ComputeSize(string path)
    {
        long total = 0;
        var stack = new Stack<string>();
        stack.Push(path);

        while (stack.Count > 0)
        {
            string dir = stack.Pop();

            try
            {
                foreach (string file in Directory.EnumerateFiles(dir))
                {
                    try
                    {
                        total += new FileInfo(file).Length;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            try
            {
                foreach (string sub in Directory.EnumerateDirectories(dir))
                {
                    stack.Push(sub);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return total;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
