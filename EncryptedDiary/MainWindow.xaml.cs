using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace EncryptedDiary;

public partial class MainWindow : Window
{
    private const int DwmUseHostBackdropBrush = 17;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmSystemBackdropType = 38;
    private const int DwmCornerRound = 2;
    private const int DwmBackdropMica = 2;
    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(20);

    private enum WindowCompositionAttribute
    {
        WcaAccentPolicy = 19
    }

    private enum AccentState
    {
        Disabled = 0,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
        EnableHostBackdrop = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    private readonly DiaryRepository _repository = new();
    private readonly ObservableCollection<DiaryEntry> _entries = new();
    private readonly DispatcherTimer _autoSaveTimer = new() { Interval = AutoSaveInterval };

    private DiaryDocument _document = new();
    private DiaryEntry? _currentEntry;
    private string? _masterPassword;
    private bool _isLoading;
    private bool _isDirty;
    private bool _isInitialized;
    private bool _syncingFormatControls;
    private bool _backdropApplied;
    private DateTimeOffset _lastAutoSaveErrorUtc = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();

        EntryList.ItemsSource = _entries;
        ICollectionView entriesView = CollectionViewSource.GetDefaultView(_entries);
        entriesView.SortDescriptions.Add(new SortDescription(nameof(DiaryEntry.UpdatedUtc), ListSortDirection.Descending));
        if (entriesView is ICollectionViewLiveShaping liveShaping)
        {
            liveShaping.IsLiveSorting = true;
            liveShaping.LiveSortingProperties.Add(nameof(DiaryEntry.UpdatedUtc));
        }

        _syncingFormatControls = true;
        FontSizeCombo.SelectedIndex = 2;
        FontColorCombo.SelectedIndex = 0;
        _syncingFormatControls = false;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _backdropApplied = ApplyWindowsEffects();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeVault();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isInitialized || !_isDirty)
        {
            _autoSaveTimer.Stop();
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            "You have unsaved changes. Save before closing?",
            "Unsaved Changes",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
            return;
        }

        if (result == MessageBoxResult.Yes && !SaveVault())
        {
            e.Cancel = true;
            return;
        }

        _autoSaveTimer.Stop();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.S && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            SaveVault();
        }
    }

    private bool ApplyWindowsEffects()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        bool success = false;

        Margins glass = new() { Left = -1 };
        if (DwmExtendFrameIntoClientArea(handle, ref glass) == 0)
        {
            success = true;
        }

        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            int hostBackdrop = 1;
            if (DwmSetWindowAttribute(handle, DwmUseHostBackdropBrush, ref hostBackdrop, Marshal.SizeOf<int>()) == 0)
            {
                success = true;
            }

            int backdrop = DwmBackdropMica;
            if (DwmSetWindowAttribute(handle, DwmSystemBackdropType, ref backdrop, Marshal.SizeOf<int>()) == 0)
            {
                success = true;
            }
        }

        if (ApplyAccentBackdrop(handle))
        {
            success = true;
        }

        int corners = DwmCornerRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corners, Marshal.SizeOf<int>());
        return success;
    }

    private static bool ApplyAccentBackdrop(IntPtr handle)
    {
        AccentState state = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)
            ? AccentState.EnableHostBackdrop
            : AccentState.EnableAcrylicBlurBehind;

        AccentPolicy accent = new()
        {
            AccentState = (int)state,
            AccentFlags = 2,
            GradientColor = 0x30FFFFFF
        };

        int accentSize = Marshal.SizeOf<AccentPolicy>();
        IntPtr accentPtr = Marshal.AllocHGlobal(accentSize);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            WindowCompositionAttributeData data = new()
            {
                Attribute = (int)WindowCompositionAttribute.WcaAccentPolicy,
                Data = accentPtr,
                SizeOfData = accentSize
            };

            return SetWindowCompositionAttribute(handle, ref data) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    private void InitializeVault()
    {
        bool existingVault = _repository.Exists();
        while (true)
        {
            PasswordPromptWindow passwordPrompt = new(existingVault) { Owner = this };
            bool? dialogResult = passwordPrompt.ShowDialog();
            if (dialogResult != true)
            {
                Close();
                return;
            }

            try
            {
                _masterPassword = passwordPrompt.Password;
                _document = _repository.LoadOrCreate(_masterPassword);
                break;
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or JsonException)
            {
                MessageBox.Show(
                    this,
                    "Unable to unlock the diary. Check your password and try again.",
                    "Unlock Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                existingVault = true;
            }
        }

        _entries.Clear();
        foreach (DiaryEntry entry in _document.Entries)
        {
            _entries.Add(entry);
        }

        if (_entries.Count == 0)
        {
            DiaryEntry first = CreateEntry("First Entry");
            _entries.Add(first);
            _isDirty = true;
        }

        EntryList.SelectedIndex = 0;
        _isInitialized = true;
        _autoSaveTimer.Start();
        string effectStatus = _backdropApplied ? "Transparency enabled." : "Transparency unsupported by composition API.";
        UpdateStatus($"Diary unlocked. {effectStatus} Auto-save every {AutoSaveInterval.TotalSeconds:0}s.");
    }

    private DiaryEntry CreateEntry(string title)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DiaryEntry
        {
            Title = title,
            CreatedUtc = now,
            UpdatedUtc = now,
            Format = EntryFormat.RichText,
            RichTextXaml = CreateDefaultDocument(),
            MarkdownText = string.Empty
        };
    }

    private static string CreateDefaultDocument()
    {
        FlowDocument document = new(new Paragraph(new Run("Start writing...")));
        return XamlWriter.Save(document);
    }

    private void AutoSaveTimer_Tick(object? sender, EventArgs e)
    {
        if (!_isInitialized || !_isDirty || string.IsNullOrWhiteSpace(_masterPassword))
        {
            return;
        }

        if (SaveVault(isAutoSave: true))
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastAutoSaveErrorUtc > TimeSpan.FromMinutes(1))
        {
            UpdateStatus("Auto-save failed. Manual save is recommended.");
            _lastAutoSaveErrorUtc = now;
        }
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EntryList.SelectedItem is DiaryEntry selected)
        {
            LoadEntry(selected);
        }
    }

    private void LoadEntry(DiaryEntry entry)
    {
        _currentEntry = entry;
        _isLoading = true;

        TitleBox.Text = entry.Title;
        RichEditor.Document = DeserializeDocument(entry.RichTextXaml);
        MarkdownEditor.Text = entry.MarkdownText;
        SelectMode(entry.Format);
        ApplyEditorMode(entry.Format);
        SyncFormatControls();

        _isLoading = false;
        UpdateStatus($"Editing \"{entry.DisplayTitle}\"");
    }

    private void SelectMode(EntryFormat format)
    {
        foreach (object item in ModeCombo.Items)
        {
            if (item is ComboBoxItem comboItem &&
                comboItem.Tag is string tag &&
                string.Equals(tag, format.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                ModeCombo.SelectedItem = comboItem;
                return;
            }
        }

        ModeCombo.SelectedIndex = 0;
    }

    private EntryFormat GetSelectedFormat()
    {
        if (ModeCombo.SelectedItem is ComboBoxItem comboItem &&
            comboItem.Tag is string tag &&
            Enum.TryParse(tag, out EntryFormat format))
        {
            return format;
        }

        return EntryFormat.RichText;
    }

    private void ApplyEditorMode(EntryFormat format)
    {
        bool richMode = format == EntryFormat.RichText;
        RichEditorContainer.Visibility = richMode ? Visibility.Visible : Visibility.Collapsed;
        MarkdownEditorContainer.Visibility = richMode ? Visibility.Collapsed : Visibility.Visible;
        RichFormattingToolbar.IsEnabled = richMode;
        RichFormattingToolbar.Opacity = richMode ? 1.0 : 0.56;
    }

    private void NewEntryButton_Click(object sender, RoutedEventArgs e)
    {
        DiaryEntry newEntry = CreateEntry($"Entry {_entries.Count + 1}");
        _entries.Add(newEntry);
        EntryList.SelectedItem = newEntry;
        MarkDirty("Created a new entry.");
    }

    private void DeleteEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEntry is null)
        {
            return;
        }

        MessageBoxResult result = MessageBox.Show(
            this,
            $"Delete \"{_currentEntry.DisplayTitle}\"?",
            "Delete Entry",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        DiaryEntry? removed = _currentEntry;
        _entries.Remove(removed);

        if (_entries.Count == 0)
        {
            DiaryEntry fallback = CreateEntry("First Entry");
            _entries.Add(fallback);
        }

        EntryList.SelectedIndex = 0;
        MarkDirty("Entry deleted.");
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveVault();
    }

    private bool SaveVault(bool isAutoSave = false)
    {
        if (string.IsNullOrWhiteSpace(_masterPassword))
        {
            return false;
        }

        try
        {
            if (_currentEntry is not null)
            {
                _currentEntry.RichTextXaml = SerializeDocument(RichEditor.Document);
                _currentEntry.MarkdownText = MarkdownEditor.Text;
            }

            _document.Entries = _entries.OrderByDescending(x => x.UpdatedUtc).ToList();
            _repository.Save(_document, _masterPassword);
            _isDirty = false;

            if (isAutoSave)
            {
                UpdateStatus($"Auto-saved at {DateTime.Now:t}");
            }
            else
            {
                UpdateStatus($"Saved at {DateTime.Now:t}");
            }

            return true;
        }
        catch (Exception ex)
        {
            if (!isAutoSave)
            {
                MessageBox.Show(
                    this,
                    $"Unable to save the diary.\n\n{ex.Message}",
                    "Save Failed",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }

            return false;
        }
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _currentEntry is null)
        {
            return;
        }

        _currentEntry.Title = string.IsNullOrWhiteSpace(TitleBox.Text) ? "Untitled Entry" : TitleBox.Text.Trim();
        Touch(_currentEntry);
        MarkDirty("Title updated.");
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _currentEntry is null)
        {
            return;
        }

        EntryFormat selectedFormat = GetSelectedFormat();
        _currentEntry.Format = selectedFormat;
        Touch(_currentEntry);
        ApplyEditorMode(selectedFormat);
        SyncFormatControls();
        MarkDirty("Editor mode changed.");
    }

    private void RichEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _currentEntry is null)
        {
            return;
        }

        _currentEntry.RichTextXaml = SerializeDocument(RichEditor.Document);
        Touch(_currentEntry);
        MarkDirty("Rich text updated.");
    }

    private void MarkdownEditor_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isLoading || _currentEntry is null)
        {
            return;
        }

        _currentEntry.MarkdownText = MarkdownEditor.Text;
        Touch(_currentEntry);
        MarkDirty("Markdown updated.");
    }

    private void RichEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _syncingFormatControls)
        {
            return;
        }

        SyncFormatControls();
    }

    private void BoldButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanFormatRichText())
        {
            return;
        }

        object current = RichEditor.Selection.GetPropertyValue(TextElement.FontWeightProperty);
        FontWeight next = current is FontWeight weight && weight == FontWeights.Bold ? FontWeights.Normal : FontWeights.Bold;
        ApplyRichSelectionProperty(TextElement.FontWeightProperty, next);
        MarkDirty("Text formatting updated.");
    }

    private void ItalicButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanFormatRichText())
        {
            return;
        }

        object current = RichEditor.Selection.GetPropertyValue(TextElement.FontStyleProperty);
        FontStyle next = current is FontStyle style && style == FontStyles.Italic ? FontStyles.Normal : FontStyles.Italic;
        ApplyRichSelectionProperty(TextElement.FontStyleProperty, next);
        MarkDirty("Text formatting updated.");
    }

    private void UnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanFormatRichText())
        {
            return;
        }

        object current = RichEditor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        bool hasUnderline = current is TextDecorationCollection collection &&
                            collection.Any(item => item.Location == TextDecorationLocation.Underline);
        object next = hasUnderline ? new TextDecorationCollection() : TextDecorations.Underline;
        ApplyRichSelectionProperty(Inline.TextDecorationsProperty, next);
        MarkDirty("Text formatting updated.");
    }

    private void ClearFormatButton_Click(object sender, RoutedEventArgs e)
    {
        if (!CanFormatRichText())
        {
            return;
        }

        TextRange selection = new(RichEditor.Selection.Start, RichEditor.Selection.End);
        if (selection.IsEmpty)
        {
            return;
        }

        selection.ClearAllProperties();
        MarkDirty("Formatting cleared.");
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _syncingFormatControls || FontSizeCombo.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        if (!double.TryParse(selected.Content?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out double fontSize))
        {
            return;
        }

        if (CanFormatRichText())
        {
            ApplyRichSelectionProperty(TextElement.FontSizeProperty, fontSize);
            MarkDirty("Font size updated.");
            return;
        }

        MarkdownEditor.FontSize = fontSize;
        UpdateStatus($"Markdown font size set to {fontSize:0}.");
    }

    private void FontColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _syncingFormatControls || FontColorCombo.SelectedItem is not ComboBoxItem selected)
        {
            return;
        }

        if (selected.Tag is not string colorHex ||
            ColorConverter.ConvertFromString(colorHex) is not Color color)
        {
            return;
        }

        SolidColorBrush brush = new(color);
        if (CanFormatRichText())
        {
            ApplyRichSelectionProperty(TextElement.ForegroundProperty, brush);
            MarkDirty("Font color updated.");
            return;
        }

        MarkdownEditor.Foreground = brush;
        UpdateStatus($"Markdown font color set to {selected.Content}.");
    }

    private bool CanFormatRichText()
    {
        return _currentEntry is not null && GetSelectedFormat() == EntryFormat.RichText;
    }

    private void ApplyRichSelectionProperty(DependencyProperty property, object value)
    {
        RichEditor.Focus();
        RichEditor.Selection.ApplyPropertyValue(property, value);
        TouchCurrentEntry();
        SyncFormatControls();
    }

    private void SyncFormatControls()
    {
        _syncingFormatControls = true;
        try
        {
            object sizeObject = RichEditor.Selection.GetPropertyValue(TextElement.FontSizeProperty);
            if (sizeObject is double size)
            {
                SelectFontSize(size);
            }

            object colorObject = RichEditor.Selection.GetPropertyValue(TextElement.ForegroundProperty);
            if (colorObject is SolidColorBrush brush)
            {
                SelectFontColor(brush.Color);
            }
        }
        finally
        {
            _syncingFormatControls = false;
        }
    }

    private void SelectFontSize(double fontSize)
    {
        ComboBoxItem? bestMatch = FontSizeCombo.Items
            .OfType<ComboBoxItem>()
            .OrderBy(item =>
            {
                if (!double.TryParse(item.Content?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out double candidate))
                {
                    return double.MaxValue;
                }

                return Math.Abs(candidate - fontSize);
            })
            .FirstOrDefault();

        if (bestMatch is not null)
        {
            FontSizeCombo.SelectedItem = bestMatch;
        }
    }

    private void SelectFontColor(Color color)
    {
        string target = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        ComboBoxItem? match = FontColorCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), target, StringComparison.OrdinalIgnoreCase));

        if (match is not null)
        {
            FontColorCombo.SelectedItem = match;
        }
    }

    private void Touch(DiaryEntry entry)
    {
        entry.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    private void TouchCurrentEntry()
    {
        if (_currentEntry is not null)
        {
            Touch(_currentEntry);
        }
    }

    private void MarkDirty(string message)
    {
        _isDirty = true;
        UpdateStatus(message);
    }

    private void UpdateStatus(string message)
    {
        StatusText.Text = message;
    }

    private static string SerializeDocument(FlowDocument document)
    {
        return XamlWriter.Save(document);
    }

    private static FlowDocument DeserializeDocument(string? xaml)
    {
        if (string.IsNullOrWhiteSpace(xaml))
        {
            return new FlowDocument(new Paragraph());
        }

        try
        {
            object parsed = XamlReader.Parse(xaml);
            if (parsed is FlowDocument flowDocument)
            {
                return flowDocument;
            }
        }
        catch
        {
            // Fall back to plain text when stored XAML cannot be parsed.
        }

        return new FlowDocument(new Paragraph(new Run(xaml)));
    }
}
