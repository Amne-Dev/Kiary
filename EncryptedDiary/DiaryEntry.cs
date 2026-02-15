using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace EncryptedDiary;

public sealed class DiaryEntry : INotifyPropertyChanged
{
    private string _title = "Untitled Entry";
    private DateTimeOffset _createdUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _updatedUtc = DateTimeOffset.UtcNow;
    private EntryFormat _format = EntryFormat.RichText;
    private string _richTextXaml = string.Empty;
    private string _markdownText = string.Empty;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
            {
                return;
            }

            _title = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public DateTimeOffset CreatedUtc
    {
        get => _createdUtc;
        set
        {
            if (_createdUtc == value)
            {
                return;
            }

            _createdUtc = value;
            OnPropertyChanged();
        }
    }

    public DateTimeOffset UpdatedUtc
    {
        get => _updatedUtc;
        set
        {
            if (_updatedUtc == value)
            {
                return;
            }

            _updatedUtc = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayUpdated));
        }
    }

    public EntryFormat Format
    {
        get => _format;
        set
        {
            if (_format == value)
            {
                return;
            }

            _format = value;
            OnPropertyChanged();
        }
    }

    public string RichTextXaml
    {
        get => _richTextXaml;
        set
        {
            if (_richTextXaml == value)
            {
                return;
            }

            _richTextXaml = value;
            OnPropertyChanged();
        }
    }

    public string MarkdownText
    {
        get => _markdownText;
        set
        {
            if (_markdownText == value)
            {
                return;
            }

            _markdownText = value;
            OnPropertyChanged();
        }
    }

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "Untitled Entry" : Title;

    [JsonIgnore]
    public string DisplayUpdated => $"Updated {UpdatedUtc.LocalDateTime:g}";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
