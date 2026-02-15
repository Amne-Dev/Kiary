using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace EncryptedDiary.WinUI;

public sealed class DiaryEntry : INotifyPropertyChanged
{
    private string _title = "Untitled Entry";
    private DateTimeOffset _createdUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _updatedUtc = DateTimeOffset.UtcNow;
    private EntryFormat _format = EntryFormat.RichText;
    private string _richTextRtf = string.Empty;
    private string _markdownText = string.Empty;
    private List<string> _collapsedTimedSectionKeys = new();

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Title
    {
        get => _title;
        set
        {
            string normalized = TextSanitizer.Sanitize(value, allowNewLines: false);
            if (_title == normalized)
            {
                return;
            }

            _title = normalized;
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

    public string RichTextRtf
    {
        get => _richTextRtf;
        set
        {
            string normalized = TextSanitizer.Sanitize(value, allowNewLines: true);
            if (_richTextRtf == normalized)
            {
                return;
            }

            _richTextRtf = normalized;
            OnPropertyChanged();
        }
    }

    public string MarkdownText
    {
        get => _markdownText;
        set
        {
            string normalized = TextSanitizer.Sanitize(value, allowNewLines: true);
            if (_markdownText == normalized)
            {
                return;
            }

            _markdownText = normalized;
            OnPropertyChanged();
        }
    }

    public List<string> CollapsedTimedSectionKeys
    {
        get => _collapsedTimedSectionKeys;
        set
        {
            _collapsedTimedSectionKeys = value ?? new List<string>();
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
