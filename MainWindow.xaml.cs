using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using WinRT.Interop;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace EncryptedDiary.WinUI;

public sealed partial class MainWindow : Window
{
    private static readonly Regex TimedHeaderRegex = new(
        @"^\s*(?<date>\d{1,2}[\/\-.]\d{1,2}[\/\-.]\d{2,4})\s*@\s*(?<time>\d{1,2}:\d{1,2}(?::\d{1,2})?\s*(?:am|pm)?)\s*:\s*(?<label>.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] TimestampPatterns =
    {
        "d/M/yyyy h:mtt", "d/M/yyyy h:mmtt", "d/M/yyyy hh:mmtt", "dd/MM/yyyy h:mmtt", "dd/MM/yyyy hh:mmtt",
        "d/M/yy h:mmtt", "dd/MM/yy h:mmtt", "d/M/yyyy H:m", "d/M/yyyy H:mm", "dd/MM/yyyy H:mm", "dd/MM/yy H:mm",
        "d/M/yyyy H:mm:ss", "dd/MM/yyyy H:mm:ss", "d/M/yyyy h:mm:sstt", "dd/MM/yyyy h:mm:sstt"
    };

    private static readonly TimeSpan AutoSaveInterval = TimeSpan.FromSeconds(15);

    private readonly DiaryRepository _repository = new();
    private readonly ObservableCollection<DiaryEntry> _entries = new();
    private readonly ObservableCollection<TimedJournalMarker> _timedMarkers = new();
    private readonly ObservableCollection<object> _entryItems = new();
    private readonly ObservableCollection<object> _timedMarkerItems = new();
    private readonly List<TimedJournalSection> _sections = new();
    private readonly DispatcherTimer _autoSaveTimer = new();

    private DiaryDocument _document = new();
    private DiaryEntry? _currentEntry;
    private string? _masterPassword;
    private bool _vaultInitialized;
    private bool _initStarted;
    private bool _isLoading;
    private bool _isDirty;
    private bool _syncingFormat;
    private bool _buildingTimedUi;
    private bool _suppressTimedSelection;
    private bool _entryItemsSourceBound;
    private bool _timedItemsSourceBound;
    private DiaryEntry? _contextMenuEntry;
    private string _lastFindQuery = string.Empty;
    private int _lastFindRichIndex = -1;
    private TextBox? _lastFindTextBox;
    private int _lastFindTextBoxIndex = -1;
    private readonly string _diagnosticsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Kiary",
        "kiary-debug.log");

    public MainWindow()
    {
        InitializeComponent();

        try
        {
            EntryList.ItemsSource = _entryItems;
            _entryItemsSourceBound = true;
        }
        catch (Exception ex)
        {
            _entryItemsSourceBound = false;
            WriteDiagnostics("EntryList.ItemsSource failed, using manual item fallback", ex);
        }

        try
        {
            TimedJournalList.ItemsSource = _timedMarkerItems;
            _timedItemsSourceBound = true;
        }
        catch (Exception ex)
        {
            _timedItemsSourceBound = false;
            WriteDiagnostics("TimedJournalList.ItemsSource failed, using manual item fallback", ex);
        }

        _entries.CollectionChanged += Entries_CollectionChanged;
        _timedMarkers.CollectionChanged += TimedMarkers_CollectionChanged;
        SyncEntryItems();
        SyncTimedMarkerItems();

        _syncingFormat = true;
        ModeCombo.SelectedIndex = 0;
        FontSizeCombo.SelectedIndex = 2;
        FontColorCombo.SelectedIndex = 0;
        _syncingFormat = false;

        _autoSaveTimer.Interval = AutoSaveInterval;
        _autoSaveTimer.Tick += AutoSaveTimer_Tick;

        if (Content is FrameworkElement rootElement)
        {
            rootElement.Loaded += MainWindow_Loaded;
        }
        else
        {
            Activated += MainWindow_ActivatedFallback;
        }
        Closed += MainWindow_Closed;
        StatusText.Text = "Kiary is locked. Unlock the vault to continue.";
    }

    private void Entries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncEntryItems();
    }

    private void TimedMarkers_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncTimedMarkerItems();
    }

    private void SyncEntryItems()
    {
        object? selected = EntryList.SelectedItem;

        if (_entryItemsSourceBound)
        {
            _entryItems.Clear();
            foreach (DiaryEntry entry in _entries)
            {
                _entryItems.Add(entry);
            }
        }
        else
        {
            EntryList.Items.Clear();
            foreach (DiaryEntry entry in _entries)
            {
                EntryList.Items.Add(entry);
            }
        }

        if (selected is DiaryEntry selectedEntry && _entries.Contains(selectedEntry))
        {
            EntryList.SelectedItem = selectedEntry;
        }
    }

    private void SyncTimedMarkerItems()
    {
        Guid? selectedId = (TimedJournalList.SelectedItem as TimedJournalMarker)?.SectionId;

        if (_timedItemsSourceBound)
        {
            _timedMarkerItems.Clear();
            foreach (TimedJournalMarker marker in _timedMarkers)
            {
                _timedMarkerItems.Add(marker);
            }
        }
        else
        {
            TimedJournalList.Items.Clear();
            foreach (TimedJournalMarker marker in _timedMarkers)
            {
                TimedJournalList.Items.Add(marker);
            }
        }

        if (selectedId.HasValue)
        {
            TimedJournalMarker? selected = _timedMarkers.FirstOrDefault(m => m.SectionId == selectedId.Value);
            if (selected is not null)
            {
                _suppressTimedSelection = true;
                TimedJournalList.SelectedItem = selected;
                _suppressTimedSelection = false;
            }
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initStarted || _vaultInitialized)
        {
            return;
        }

        _initStarted = true;
        try
        {
            ConfigureTransparentTitleBar();
            await InitializeVaultAsync();
        }
        catch (Exception ex)
        {
            _initStarted = false;
            WriteDiagnostics("Startup (Loaded) failed", ex);
            StatusText.Text = "Startup failed. Check details and retry.";
            await ShowMessageAsync("Startup Error", ex.Message);
        }
    }

    private async void MainWindow_ActivatedFallback(object sender, WindowActivatedEventArgs args)
    {
        if (_initStarted || _vaultInitialized)
        {
            return;
        }

        _initStarted = true;
        try
        {
            ConfigureTransparentTitleBar();
            await InitializeVaultAsync();
        }
        catch (Exception ex)
        {
            _initStarted = false;
            WriteDiagnostics("Startup (ActivatedFallback) failed", ex);
            StatusText.Text = "Startup failed. Check details and retry.";
            await ShowMessageAsync("Startup Error", ex.Message);
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _autoSaveTimer.Stop();
        if (_isDirty)
        {
            SaveVault(true);
        }
    }

    private async Task InitializeVaultAsync()
    {
        bool hasExistingVault = _repository.Exists();

        while (true)
        {
            string? password = await PromptForPasswordAsync(hasExistingVault);
            if (password is null)
            {
                Close();
                return;
            }

            try
            {
                _document = _repository.LoadOrCreate(password);
                _masterPassword = password;
                break;
            }
            catch (Exception ex) when (ex is CryptographicException or InvalidDataException or JsonException)
            {
                hasExistingVault = true;
                await ShowMessageAsync("Unlock Failed", "Unable to unlock Kiary with that password.");
            }
        }

        _entries.Clear();
        foreach (DiaryEntry entry in _document.Entries.Where(e => e is not null).OrderByDescending(e => e.UpdatedUtc))
        {
            _entries.Add(entry);
        }

        if (_entries.Count == 0)
        {
            DiaryEntry first = CreateEntry("First Entry");
            _entries.Add(first);
            _document.Entries.Add(first);
            _isDirty = true;
        }

        _vaultInitialized = true;
        _autoSaveTimer.Start();

        EntryList.SelectedItem = _entries[0];
        LoadEntrySafe(_entries[0]);

        if (_isDirty)
        {
            SaveVault(true);
        }

        StatusText.Text = $"Vault unlocked. Auto-save every {AutoSaveInterval.TotalSeconds:0}s. Path: {_repository.VaultPath}";
    }

    private async Task<string?> PromptForPasswordAsync(bool existingVault)
    {
        while (true)
        {
            PasswordBox passwordBox = new() { PlaceholderText = existingVault ? "Master password" : "Create master password" };
            PasswordBox? confirmBox = null;

            StackPanel panel = new() { Spacing = 10 };
            panel.Children.Add(new TextBlock
            {
                Text = existingVault ? "Enter your Kiary master password." : "Set a master password for your encrypted Kiary vault.",
                TextWrapping = TextWrapping.WrapWholeWords
            });
            panel.Children.Add(passwordBox);

            if (!existingVault)
            {
                confirmBox = new PasswordBox { PlaceholderText = "Confirm password" };
                panel.Children.Add(confirmBox);
            }

            ContentDialog dialog = new()
            {
                Title = existingVault ? "Unlock Kiary" : "Create Kiary Vault",
                PrimaryButtonText = existingVault ? "Unlock" : "Create",
                CloseButtonText = "Exit",
                DefaultButton = ContentDialogButton.Primary,
                Content = panel
            };
            dialog.XamlRoot = await GetDialogXamlRootAsync();

            ContentDialogResult result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(passwordBox.Password))
            {
                await ShowMessageAsync("Password Required", "Please enter a non-empty password.");
                continue;
            }

            if (confirmBox is not null && !string.Equals(passwordBox.Password, confirmBox.Password, StringComparison.Ordinal))
            {
                await ShowMessageAsync("Mismatch", "Password confirmation does not match.");
                continue;
            }

            return passwordBox.Password;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK"
        };
        dialog.XamlRoot = await GetDialogXamlRootAsync();

        await dialog.ShowAsync();
    }

    private async Task<XamlRoot> GetDialogXamlRootAsync()
    {
        for (int i = 0; i < 60; i++)
        {
            XamlRoot? root = GetDialogXamlRoot();
            if (root is not null)
            {
                return root;
            }

            await Task.Delay(25);
        }

        throw new InvalidOperationException("Unable to resolve XamlRoot for dialog.");
    }

    private XamlRoot? GetDialogXamlRoot()
    {
        if (TitleBarDragRegion.XamlRoot is not null)
        {
            return TitleBarDragRegion.XamlRoot;
        }

        if (Content is FrameworkElement element)
        {
            return element.XamlRoot;
        }

        return null;
    }

    private static DiaryEntry CreateEntry(string title)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new DiaryEntry
        {
            Title = title,
            CreatedUtc = now,
            UpdatedUtc = now,
            Format = EntryFormat.RichText,
            RichTextRtf = string.Empty,
            MarkdownText = string.Empty
        };
    }

    private void ConfigureTransparentTitleBar()
    {
        try
        {
            TryApplyWindowBackdrop();
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleBarDragRegion);

            IntPtr hwnd = WindowNative.GetWindowHandle(this);
            WindowId id = Win32Interop.GetWindowIdFromWindow(hwnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(id);
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            appWindow.TitleBar.BackgroundColor = Colors.Transparent;
            appWindow.TitleBar.InactiveBackgroundColor = Colors.Transparent;
            appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
            appWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(44, 255, 255, 255);
            appWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(72, 255, 255, 255);

            string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "logo.ico");
            if (File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
            }
        }
        catch
        {
        }
    }

    private void TryApplyWindowBackdrop()
    {
        try
        {
            SystemBackdrop = new MicaBackdrop();
            return;
        }
        catch
        {
        }

        try
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }
        catch
        {
        }
    }

    private void AutoSaveTimer_Tick(object? sender, object e)
    {
        if (!_vaultInitialized || !_isDirty)
        {
            return;
        }

        SaveVault(true);
    }

    private void NewEntryButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_vaultInitialized)
        {
            return;
        }

        DiaryEntry entry = CreateEntry($"Entry {_entries.Count + 1}");
        _entries.Insert(0, entry);
        EntryList.SelectedItem = entry;
        MarkDirty("Created a new entry.", true);
    }

    private async void DeleteEntryButton_Click(object sender, RoutedEventArgs e)
    {
        await DeleteEntryAsync(ResolveContextEntryTarget());
        _contextMenuEntry = null;
    }

    private void EntryList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _contextMenuEntry = null;

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        ListViewItem? item = FindAncestor<ListViewItem>(source);
        if (item?.DataContext is DiaryEntry entry)
        {
            _contextMenuEntry = entry;
            EntryList.SelectedItem = entry;
        }
    }

    private async void DeleteEntryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await DeleteEntryAsync(ResolveContextEntryTarget());
        _contextMenuEntry = null;
    }

    private async void RenameEntryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DiaryEntry? targetEntry = ResolveContextEntryTarget();
        if (targetEntry is null)
        {
            return;
        }

        TextBox renameBox = new()
        {
            Text = TextSanitizer.Sanitize(targetEntry.Title, allowNewLines: false),
            PlaceholderText = "Entry title"
        };

        ContentDialog dialog = new()
        {
            Title = "Rename Entry",
            Content = renameBox,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = await GetDialogXamlRootAsync()
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string renamed = TextSanitizer.Sanitize(renameBox.Text, allowNewLines: false).Trim();
        if (string.IsNullOrWhiteSpace(renamed))
        {
            renamed = "Untitled Entry";
        }

        targetEntry.Title = renamed;
        MarkDirty("Entry renamed.", true);
        _contextMenuEntry = null;
    }

    private void DuplicateEntryMenuItem_Click(object sender, RoutedEventArgs e)
    {
        DiaryEntry? source = ResolveContextEntryTarget();
        if (source is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DiaryEntry copy = new()
        {
            Title = BuildDuplicateTitle(source.DisplayTitle),
            CreatedUtc = now,
            UpdatedUtc = now,
            Format = source.Format,
            RichTextRtf = source.RichTextRtf,
            MarkdownText = source.MarkdownText
        };

        _entries.Insert(0, copy);
        EntryList.SelectedItem = copy;
        MarkDirty("Entry duplicated.", true);
        _contextMenuEntry = null;
    }

    private DiaryEntry? ResolveContextEntryTarget()
    {
        return _contextMenuEntry ?? EntryList.SelectedItem as DiaryEntry ?? _currentEntry;
    }

    private string BuildDuplicateTitle(string baseTitle)
    {
        string normalizedBase = TextSanitizer.Sanitize(baseTitle, allowNewLines: false).Trim();
        if (string.IsNullOrWhiteSpace(normalizedBase))
        {
            normalizedBase = "Untitled Entry";
        }

        string candidate = $"{normalizedBase} (Copy)";
        int counter = 2;
        while (_entries.Any(e => string.Equals(e.Title, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{normalizedBase} (Copy {counter})";
            counter++;
        }

        return candidate;
    }

    private async Task DeleteEntryAsync(DiaryEntry? targetEntry)
    {
        if (targetEntry is null)
        {
            return;
        }

        ContentDialog confirm = new()
        {
            Title = "Delete Entry",
            Content = $"Delete \"{targetEntry.DisplayTitle}\"?",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = await GetDialogXamlRootAsync()
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        int index = _entries.IndexOf(targetEntry);
        if (index < 0)
        {
            return;
        }

        _entries.RemoveAt(index);
        if (_entries.Count == 0)
        {
            _entries.Add(CreateEntry("First Entry"));
        }

        EntryList.SelectedItem = _entries[Math.Clamp(index, 0, _entries.Count - 1)];
        MarkDirty("Entry deleted.", true);
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        DependencyObject? current = source;
        while (current is not null)
        {
            if (current is T typed)
            {
                return typed;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || EntryList.SelectedItem is not DiaryEntry selected)
        {
            return;
        }

        if (_currentEntry is not null && _currentEntry.Id != selected.Id)
        {
            PersistEditorIntoCurrentEntry();
        }

        LoadEntrySafe(selected);
    }

    private void LoadEntrySafe(DiaryEntry entry)
    {
        try
        {
            LoadEntry(entry);
        }
        catch (Exception ex)
        {
            WriteDiagnostics($"LoadEntry failed for entry {entry.Id}", ex);

            entry.Title = "Recovered Entry";
            entry.RichTextRtf = string.Empty;
            entry.MarkdownText = string.Empty;
            entry.Format = EntryFormat.RichText;

            LoadEntry(entry);
            MarkDirty("Entry recovered from invalid data.", true);
        }
    }

    private void LoadEntry(DiaryEntry entry)
    {
        _currentEntry = entry;
        _isLoading = true;
        ResetFindState();

        LoadRichText(entry);
        RebuildTimedSections(GetTimedSourceText(entry), preserveCurrentStates: false);
        SelectMode(entry.Format);
        ApplyEditorMode(entry.Format);
        SyncFormatControls();

        _isLoading = false;
        StatusText.Text = $"Editing \"{entry.DisplayTitle}\".";
    }

    private void LoadRichText(DiaryEntry entry)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(entry.RichTextRtf))
            {
                RichEditor.Document.SetText(TextSetOptions.None, string.Empty);
            }
            else
            {
                RichEditor.Document.SetText(
                    TextSetOptions.FormatRtf,
                    TextSanitizer.Sanitize(entry.RichTextRtf, allowNewLines: true));
            }
        }
        catch
        {
            RichEditor.Document.SetText(
                TextSetOptions.None,
                TextSanitizer.Sanitize(entry.MarkdownText, allowNewLines: true));
        }
    }

    private void SelectMode(EntryFormat format)
    {
        foreach (object item in ModeCombo.Items)
        {
            if (item is ComboBoxItem combo && combo.Tag is string tag && string.Equals(tag, format.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                ModeCombo.SelectedItem = combo;
                return;
            }
        }

        ModeCombo.SelectedIndex = 0;
    }

    private EntryFormat GetSelectedFormat()
    {
        if (ModeCombo.SelectedItem is ComboBoxItem combo && combo.Tag is string tag && Enum.TryParse(tag, out EntryFormat format))
        {
            return format;
        }

        return EntryFormat.RichText;
    }

    private void ResetFindState()
    {
        _lastFindQuery = string.Empty;
        _lastFindRichIndex = -1;
        _lastFindTextBox = null;
        _lastFindTextBoxIndex = -1;
    }

    private void ApplyEditorMode(EntryFormat format)
    {
        bool rich = format == EntryFormat.RichText;
        RichEditorHost.Visibility = rich ? Visibility.Visible : Visibility.Collapsed;
        MarkdownEditorHost.Visibility = rich ? Visibility.Collapsed : Visibility.Visible;
        RichToolbar.Opacity = rich ? 1.0 : 0.58;
        NewTimedSectionButton.Visibility = rich ? Visibility.Collapsed : Visibility.Visible;
        UpdateTimedHint();
    }

    private void ModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoading || _currentEntry is null)
        {
            return;
        }

        EntryFormat format = GetSelectedFormat();
        _currentEntry.Format = format;

        if (format == EntryFormat.Markdown && string.IsNullOrWhiteSpace(_currentEntry.MarkdownText))
        {
            RichEditor.Document.GetText(TextGetOptions.None, out string plain);
            _currentEntry.MarkdownText = (plain ?? string.Empty).TrimEnd('\r', '\n');
            RebuildTimedSections(_currentEntry.MarkdownText ?? string.Empty);
        }
        else
        {
            RebuildTimedSections(GetTimedSourceText(_currentEntry));
        }

        ApplyEditorMode(format);
        MarkDirty("Editor mode changed.", true);
    }

    private void RichEditor_TextChanged(object sender, RoutedEventArgs e)
    {
        if (_isLoading || _currentEntry is null)
        {
            return;
        }

        CaptureRichTextToEntry(_currentEntry);
        if (_currentEntry.Format == EntryFormat.RichText)
        {
            RebuildTimedSections(GetTimedSourceText(_currentEntry));
            MarkDirty("Rich text edited.");
        }
    }

    private void RichEditor_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLoading)
        {
            SyncFormatControls();
        }
    }

    private void BoldButton_Click(object sender, RoutedEventArgs e)
    {
        ITextSelection s = RichEditor.Document.Selection;
        s.CharacterFormat.Bold = s.CharacterFormat.Bold == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        MarkDirty("Formatting changed.");
        SyncFormatControls();
    }

    private void ItalicButton_Click(object sender, RoutedEventArgs e)
    {
        ITextSelection s = RichEditor.Document.Selection;
        s.CharacterFormat.Italic = s.CharacterFormat.Italic == FormatEffect.On ? FormatEffect.Off : FormatEffect.On;
        MarkDirty("Formatting changed.");
        SyncFormatControls();
    }

    private void UnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        ITextSelection s = RichEditor.Document.Selection;
        s.CharacterFormat.Underline = s.CharacterFormat.Underline == UnderlineType.None ? UnderlineType.Single : UnderlineType.None;
        MarkDirty("Formatting changed.");
        SyncFormatControls();
    }

    private void FontSizeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFormat)
        {
            return;
        }

        if (FontSizeCombo.SelectedItem is ComboBoxItem combo &&
            double.TryParse(combo.Content?.ToString(), NumberStyles.Number, CultureInfo.InvariantCulture, out double size))
        {
            RichEditor.Document.Selection.CharacterFormat.Size = (float)size;
            MarkDirty("Font size changed.");
        }
    }

    private void FontColorCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingFormat)
        {
            return;
        }

        if (FontColorCombo.SelectedItem is ComboBoxItem combo && combo.Tag is string hex && TryParseColor(hex, out Color color))
        {
            RichEditor.Document.Selection.CharacterFormat.ForegroundColor = color;
            MarkDirty("Text color changed.");
        }
    }

    private void ClearFormatButton_Click(object sender, RoutedEventArgs e)
    {
        ITextSelection s = RichEditor.Document.Selection;
        s.CharacterFormat.Bold = FormatEffect.Off;
        s.CharacterFormat.Italic = FormatEffect.Off;
        s.CharacterFormat.Underline = UnderlineType.None;
        s.CharacterFormat.Size = 16;

        if (TryParseColor("#1C2A37", out Color color))
        {
            s.CharacterFormat.ForegroundColor = color;
        }

        MarkDirty("Formatting cleared.", true);
        SyncFormatControls();
    }

    private async void FindMenuItem_Click(object sender, RoutedEventArgs e)
    {
        await ShowFindDialogAsync();
    }

    private async void DocumentationMenuItem_Click(object sender, RoutedEventArgs e)
    {
        StackPanel content = new() { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "Getting Started",
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Kiary stores entries in an encrypted vault using your master password.",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new TextBlock
        {
            Text = "Writing Modes",
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "- Rich Text: formatting controls for bold, italic, underline, size, and color.\n- Markdown: plain text editing with Timed Journal sections.",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new TextBlock
        {
            Text = "Timed Journal Syntax",
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "Start a line with:\n dd/mm/yyyy@hh:mmpm : Your title\nExample:\n 25/11/2025@10:59pm : I want",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new TextBlock
        {
            Text = "When syntax is detected, the Timed Journal sidebar updates and lets you jump to that section.",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new TextBlock
        {
            Text = "Shortcuts And Menu",
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "- Ctrl+F: Find in current entry\n- File menu: Save, Import, Export\n- Right-click entry: Rename, Duplicate, Delete",
            TextWrapping = TextWrapping.WrapWholeWords
        });

        ContentDialog dialog = new()
        {
            Title = "Kiary Documentation",
            PrimaryButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = await GetDialogXamlRootAsync(),
            Content = new ScrollViewer
            {
                MaxHeight = 520,
                Content = content
            }
        };

        await dialog.ShowAsync();
    }

    private async void AboutMenuItem_Click(object sender, RoutedEventArgs e)
    {
        HyperlinkButton githubLink = new()
        {
            Content = "https://github.com/Amne-Dev"
        };
        githubLink.Click += async (_, _) =>
        {
            await Launcher.LaunchUriAsync(new Uri("https://github.com/Amne-Dev"));
        };

        StackPanel content = new() { Spacing = 10 };
        content.Children.Add(new Image
        {
            Width = 56,
            Height = 56,
            HorizontalAlignment = HorizontalAlignment.Left,
            Source = new BitmapImage(new Uri("ms-appx:///Assets/Square44x44Logo.scale-200.png"))
        });
        content.Children.Add(new TextBlock
        {
            Text = "Kiary",
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "An encrypted diary with Rich Text and Markdown support.",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new TextBlock
        {
            Text = "Built with WinUI 3.\nYour vault is encrypted at rest using your master password.",
            TextWrapping = TextWrapping.WrapWholeWords
        });
        content.Children.Add(new TextBlock
        {
            Text = "Created by AmDev",
            FontWeight = FontWeights.SemiBold
        });
        content.Children.Add(githubLink);

        ContentDialog dialog = new()
        {
            Title = "About Kiary",
            PrimaryButtonText = "Close",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = await GetDialogXamlRootAsync(),
            Content = content
        };

        await dialog.ShowAsync();
    }

    private async void FindKeyboardAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        await ShowFindDialogAsync();
    }

    private async Task ShowFindDialogAsync()
    {
        if (_currentEntry is null)
        {
            return;
        }

        TextBox findBox = new()
        {
            PlaceholderText = "Find text",
            Text = _lastFindQuery
        };

        ContentDialog dialog = new()
        {
            Title = "Find",
            Content = findBox,
            PrimaryButtonText = "Find Next",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = await GetDialogXamlRootAsync()
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string query = TextSanitizer.Sanitize(findBox.Text, allowNewLines: false).Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        bool found = FindNextOccurrence(query);
        if (found)
        {
            StatusText.Text = $"Found \"{query}\".";
        }
        else
        {
            _lastFindQuery = query;
            StatusText.Text = $"No matches for \"{query}\".";
        }
    }

    private bool FindNextOccurrence(string query)
    {
        EntryFormat format = GetSelectedFormat();
        return format == EntryFormat.Markdown
            ? FindNextInMarkdownSections(query)
            : FindNextInRichEditor(query);
    }

    private bool FindNextInRichEditor(string query)
    {
        RichEditor.Document.GetText(TextGetOptions.None, out string text);
        text ??= string.Empty;
        if (text.Length == 0)
        {
            return false;
        }

        bool sameQuery = string.Equals(_lastFindQuery, query, StringComparison.OrdinalIgnoreCase);
        int startIndex = sameQuery ? Math.Min(_lastFindRichIndex + 1, text.Length) : 0;

        int index = text.IndexOf(query, startIndex, StringComparison.OrdinalIgnoreCase);
        if (index < 0 && startIndex > 0)
        {
            index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        }

        if (index < 0)
        {
            return false;
        }

        _lastFindQuery = query;
        _lastFindRichIndex = index;
        _lastFindTextBox = null;
        _lastFindTextBoxIndex = -1;

        RichEditor.Focus(FocusState.Programmatic);
        RichEditor.Document.Selection.SetRange(index, index + query.Length);
        return true;
    }

    private bool FindNextInMarkdownSections(string query)
    {
        List<MarkdownSearchTarget> targets = new();
        foreach (TimedJournalSection section in _sections)
        {
            if (section.IsTimed && section.HeaderEditor is not null)
            {
                string headerText = section.HeaderEditor.Text ?? string.Empty;
                if (!string.IsNullOrEmpty(headerText))
                {
                    targets.Add(new MarkdownSearchTarget(section, section.HeaderEditor, headerText));
                }
            }

            if (section.BodyEditor is not null)
            {
                string bodyText = section.BodyEditor.Text ?? string.Empty;
                if (!string.IsNullOrEmpty(bodyText))
                {
                    targets.Add(new MarkdownSearchTarget(section, section.BodyEditor, bodyText));
                }
            }
        }

        if (targets.Count == 0)
        {
            return false;
        }

        bool sameQuery = string.Equals(_lastFindQuery, query, StringComparison.OrdinalIgnoreCase);
        int startTarget = 0;
        int startOffset = 0;

        if (sameQuery && _lastFindTextBox is not null)
        {
            int existingIndex = targets.FindIndex(t => ReferenceEquals(t.Editor, _lastFindTextBox));
            if (existingIndex >= 0)
            {
                startTarget = existingIndex;
                startOffset = Math.Max(0, _lastFindTextBoxIndex + 1);
            }
        }

        for (int pass = 0; pass < 2; pass++)
        {
            int firstTarget = pass == 0 ? startTarget : 0;
            int firstOffset = pass == 0 ? startOffset : 0;

            for (int i = firstTarget; i < targets.Count; i++)
            {
                MarkdownSearchTarget target = targets[i];
                int offset = pass == 0 && i == firstTarget ? Math.Min(firstOffset, target.Text.Length) : 0;
                int index = target.Text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    continue;
                }

                if (target.Section.Expander is not null)
                {
                    target.Section.IsExpanded = true;
                    target.Section.Expander.IsExpanded = true;
                }

                target.Editor.Focus(FocusState.Programmatic);
                target.Editor.Select(index, query.Length);

                _lastFindQuery = query;
                _lastFindRichIndex = -1;
                _lastFindTextBox = target.Editor;
                _lastFindTextBoxIndex = index;
                return true;
            }

            if (startTarget == 0)
            {
                break;
            }
        }

        return false;
    }

    private static bool TryParseColor(string? hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        string raw = hex.Trim().TrimStart('#');
        if (raw.Length == 6 && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
        {
            color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }

        if (raw.Length == 8 && uint.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint argb))
        {
            color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            return true;
        }

        return false;
    }

    private void SyncFormatControls()
    {
        if (_syncingFormat)
        {
            return;
        }

        _syncingFormat = true;

        int rounded = (int)Math.Round(Math.Max(16, RichEditor.Document.Selection.CharacterFormat.Size));
        SelectComboItem(FontSizeCombo, rounded.ToString(CultureInfo.InvariantCulture));

        Color c = RichEditor.Document.Selection.CharacterFormat.ForegroundColor;
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        foreach (object item in FontColorCombo.Items)
        {
            if (item is ComboBoxItem combo && combo.Tag is string tag && string.Equals(tag, hex, StringComparison.OrdinalIgnoreCase))
            {
                FontColorCombo.SelectedItem = combo;
                break;
            }
        }

        _syncingFormat = false;
    }

    private static void SelectComboItem(ComboBox combo, string value)
    {
        foreach (object item in combo.Items)
        {
            if (item is ComboBoxItem comboItem && string.Equals(comboItem.Content?.ToString(), value, StringComparison.Ordinal))
            {
                combo.SelectedItem = comboItem;
                break;
            }
        }
    }

    private void NewTimedSectionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEntry is null)
        {
            return;
        }

        TimedJournalSection section = new()
        {
            IsTimed = true,
            HeaderText = $"{DateTime.Now:dd/MM/yyyy@hh:mmtt} : ",
            IsExpanded = true,
            BodyText = string.Empty
        };

        RefreshTimedSectionHeader(section);
        _sections.Add(section);
        RefreshTimedSectionStateKeys();
        PersistCollapsedTimedSectionState(markDirty: false);

        RenderTimedSections();
        PersistMarkdownIntoCurrentEntry();
        MarkDirty("Timed section added.", true);

        section.Expander?.StartBringIntoView();
        if (section.HeaderEditor is not null)
        {
            section.HeaderEditor.Focus(FocusState.Programmatic);
            section.HeaderEditor.SelectionStart = section.HeaderEditor.Text.Length;
        }

        SelectMarkerForSection(section);
    }

    private void TimedJournalList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressTimedSelection || _buildingTimedUi)
        {
            return;
        }

        if (TimedJournalList.SelectedItem is not TimedJournalMarker marker)
        {
            return;
        }

        JumpToTimedMarker(marker);
    }

    private void JumpToTimedMarker(TimedJournalMarker marker)
    {
        TimedJournalExpander.IsExpanded = true;

        TimedJournalSection? target = _sections.FirstOrDefault(s => s.Id == marker.SectionId);
        if (target is null)
        {
            return;
        }

        if (GetSelectedFormat() == EntryFormat.Markdown)
        {
            if (target.Expander is null)
            {
                RenderTimedSections();
                target = _sections.FirstOrDefault(s => s.Id == marker.SectionId);
                if (target is null)
                {
                    return;
                }
            }

            target.IsExpanded = true;
            target.Expander?.IsExpanded = true;
            PersistCollapsedTimedSectionState(markDirty: true);
            target.Expander?.StartBringIntoView();
            DispatcherQueue.TryEnqueue(() =>
            {
                target.Expander?.StartBringIntoView();
                target.BodyEditor?.Focus(FocusState.Programmatic);
            });
            return;
        }

        JumpToTimedHeaderInRichEditor(target.HeaderText, marker.Display);
    }

    private void JumpToTimedHeaderInRichEditor(string headerText, string markerDisplay)
    {
        try
        {
            RichEditor.Document.GetText(TextGetOptions.None, out string content);
            string haystack = content ?? string.Empty;

            string primaryNeedle = markerDisplay?.Trim() ?? string.Empty;
            string fallbackNeedle = TextSanitizer.Sanitize(headerText, allowNewLines: false).Trim();

            if (string.IsNullOrWhiteSpace(haystack))
            {
                return;
            }

            int start = -1;
            int length = 0;

            if (!string.IsNullOrWhiteSpace(primaryNeedle))
            {
                start = haystack.IndexOf(primaryNeedle, StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    length = primaryNeedle.Length;
                }
            }

            if (start < 0 && !string.IsNullOrWhiteSpace(fallbackNeedle))
            {
                start = haystack.IndexOf(fallbackNeedle, StringComparison.OrdinalIgnoreCase);
                if (start >= 0)
                {
                    length = fallbackNeedle.Length;
                }
            }

            if (start < 0)
            {
                return;
            }

            RichEditor.Document.Selection.SetRange(start, start + Math.Max(length, 1));
            RichEditor.Document.Selection.ScrollIntoView(PointOptions.Start);
            RichEditor.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            WriteDiagnostics("JumpToTimedHeaderInRichEditor failed", ex);
        }
    }

    private void SelectMarkerForSection(TimedJournalSection section)
    {
        if (!section.IsTimed || section.Marker is null)
        {
            return;
        }

        _suppressTimedSelection = true;
        TimedJournalList.SelectedItem = section.Marker;
        _suppressTimedSelection = false;
    }

    private void RebuildTimedSections(string markdown, bool preserveCurrentStates = true)
    {
        Dictionary<string, bool>? currentStates = preserveCurrentStates ? CaptureCurrentSectionExpansionStates() : null;
        HashSet<string> persistedCollapsed = GetPersistedCollapsedTimedKeys();

        _sections.Clear();
        foreach (TimedJournalSection section in ParseSections(markdown, currentStates, persistedCollapsed))
        {
            _sections.Add(section);
        }

        PersistCollapsedTimedSectionState(markDirty: false);
        RenderTimedSections();
    }

    private Dictionary<string, bool> CaptureCurrentSectionExpansionStates()
    {
        Dictionary<string, bool> states = new(StringComparer.Ordinal);
        foreach (TimedJournalSection section in _sections)
        {
            if (!section.IsTimed || string.IsNullOrWhiteSpace(section.StateKey))
            {
                continue;
            }

            states[section.StateKey] = section.IsExpanded;
        }

        return states;
    }

    private HashSet<string> GetPersistedCollapsedTimedKeys()
    {
        if (_currentEntry is null || _currentEntry.CollapsedTimedSectionKeys is null)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        return new HashSet<string>(
            _currentEntry.CollapsedTimedSectionKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim()),
            StringComparer.Ordinal);
    }

    private static string BuildTimedSectionStateKey(string headerText, Dictionary<string, int> seenHeaders)
    {
        string normalizedHeader = TextSanitizer.Sanitize(headerText, allowNewLines: false).Trim();
        if (string.IsNullOrWhiteSpace(normalizedHeader))
        {
            normalizedHeader = "Timed";
        }

        int occurrence = seenHeaders.TryGetValue(normalizedHeader, out int existing) ? existing : 0;
        seenHeaders[normalizedHeader] = occurrence + 1;
        return $"{normalizedHeader}#{occurrence}";
    }

    private List<TimedJournalSection> ParseSections(
        string markdown,
        IReadOnlyDictionary<string, bool>? runtimeExpansionStates,
        IReadOnlySet<string> persistedCollapsedKeys)
    {
        string normalized = TextSanitizer.Sanitize(markdown, allowNewLines: true)
            .Replace("\r\n", "\n")
            .Replace('\r', '\n')
            .Replace('\u2028', '\n')
            .Replace('\u2029', '\n');
        string[] lines = normalized.Split('\n');

        List<TimedJournalSection> parsed = new();
        TimedJournalSection? current = null;
        Dictionary<string, int> seenHeaders = new(StringComparer.Ordinal);

        foreach (string line in lines)
        {
            if (TryParseTimedHeader(line, out TimedHeaderData header))
            {
                string stateKey = BuildTimedSectionStateKey(header.HeaderText, seenHeaders);
                bool isExpanded = true;
                if (runtimeExpansionStates is not null && runtimeExpansionStates.TryGetValue(stateKey, out bool runtimeExpanded))
                {
                    isExpanded = runtimeExpanded;
                }
                else if (persistedCollapsedKeys.Contains(stateKey))
                {
                    isExpanded = false;
                }

                current = new TimedJournalSection
                {
                    IsTimed = true,
                    StateKey = stateKey,
                    IsExpanded = isExpanded,
                    HeaderText = header.HeaderText,
                    Display = header.Display,
                    OccurredAt = header.OccurredAt,
                    BodyText = string.Empty
                };
                parsed.Add(current);
                continue;
            }

            if (current is null)
            {
                current = new TimedJournalSection
                {
                    IsTimed = false,
                    IsExpanded = true,
                    HeaderText = "Notes",
                    Display = "Notes",
                    BodyText = string.Empty
                };
                parsed.Add(current);
            }

            current.BodyText = string.IsNullOrEmpty(current.BodyText) ? line : $"{current.BodyText}\n{line}";
        }

        if (parsed.Count == 0)
        {
            parsed.Add(new TimedJournalSection
            {
                IsTimed = false,
                IsExpanded = true,
                HeaderText = "Notes",
                Display = "Notes",
                BodyText = string.Empty
            });
        }

        return parsed;
    }

    private void RenderTimedSections()
    {
        _buildingTimedUi = true;
        RefreshTimedSectionStateKeys();
        TimedSectionPanel.Children.Clear();

        foreach (TimedJournalSection section in _sections)
        {
            if (section.IsTimed)
            {
                RefreshTimedSectionHeader(section);
            }

            Expander expander = new()
            {
                Header = section.IsTimed ? section.Display : "Notes",
                IsExpanded = section.IsExpanded,
                ExpandDirection = ExpandDirection.Down,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            expander.Expanding += (_, _) => OnSectionExpansionChanged(section, true);
            expander.Collapsed += (_, _) => OnSectionExpansionChanged(section, false);

            StackPanel stack = new() { Spacing = 6 };

            if (section.IsTimed)
            {
                TextBox headerEditor = new()
                {
                    Text = TextSanitizer.Sanitize(section.HeaderText, allowNewLines: false),
                    PlaceholderText = "dd/mm/yyyy@hh:mmpm : Title"
                };

                headerEditor.GotFocus += (_, _) => SelectMarkerForSection(section);
                headerEditor.LostFocus += (_, _) =>
                {
                    if (_buildingTimedUi || _isLoading)
                    {
                        return;
                    }

                    ResyncTimedSectionsFromEditors();
                    PersistMarkdownIntoCurrentEntry();
                    RefreshTimedMarkers();
                    UpdateTimedHint();
                };
                headerEditor.TextChanged += (_, _) =>
                {
                    if (_buildingTimedUi || _isLoading)
                    {
                        return;
                    }

                    section.HeaderText = headerEditor.Text;
                    RefreshTimedSectionHeader(section);
                    expander.Header = section.IsTimed ? section.Display : "Untimed";
                    RefreshTimedSectionStateKeys();
                    PersistCollapsedTimedSectionState(markDirty: false);
                    PersistMarkdownIntoCurrentEntry();
                    RefreshTimedMarkers();
                    UpdateTimedHint();
                    MarkDirty("Timed header updated.");
                };

                section.HeaderEditor = headerEditor;
                stack.Children.Add(headerEditor);
            }
            else
            {
                section.HeaderEditor = null;
            }

            TextBox bodyEditor = new()
            {
                Text = TextSanitizer.Sanitize(section.BodyText, allowNewLines: true),
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120,
                VerticalContentAlignment = VerticalAlignment.Top
            };

            bodyEditor.GotFocus += (_, _) => SelectMarkerForSection(section);
            bodyEditor.LostFocus += (_, _) =>
            {
                if (_buildingTimedUi || _isLoading)
                {
                    return;
                }

                ResyncTimedSectionsFromEditors();
                PersistMarkdownIntoCurrentEntry();
                RefreshTimedMarkers();
                UpdateTimedHint();
            };
            bodyEditor.TextChanged += (_, _) =>
            {
                if (_buildingTimedUi || _isLoading)
                {
                    return;
                }

                section.BodyText = bodyEditor.Text;
                ResyncTimedSectionsFromEditors();
                PersistMarkdownIntoCurrentEntry();
                MarkDirty("Markdown edited.");
            };

            section.BodyEditor = bodyEditor;
            section.Expander = expander;

            stack.Children.Add(bodyEditor);
            expander.Content = stack;
            TimedSectionPanel.Children.Add(expander);
        }

        _buildingTimedUi = false;
        RefreshTimedMarkers();
        UpdateTimedHint();
    }

    private void RefreshTimedSectionStateKeys()
    {
        Dictionary<string, int> seenHeaders = new(StringComparer.Ordinal);
        foreach (TimedJournalSection section in _sections)
        {
            if (!section.IsTimed)
            {
                section.StateKey = string.Empty;
                continue;
            }

            section.StateKey = BuildTimedSectionStateKey(section.HeaderText, seenHeaders);
        }
    }

    private void OnSectionExpansionChanged(TimedJournalSection section, bool isExpanded)
    {
        if (_buildingTimedUi || _isLoading || !section.IsTimed)
        {
            return;
        }

        section.IsExpanded = isExpanded;
        PersistCollapsedTimedSectionState(markDirty: true);
    }

    private void PersistCollapsedTimedSectionState(bool markDirty)
    {
        if (_currentEntry is null)
        {
            return;
        }

        RefreshTimedSectionStateKeys();

        List<string> collapsedKeys = _sections
            .Where(s => s.IsTimed && !s.IsExpanded && !string.IsNullOrWhiteSpace(s.StateKey))
            .Select(s => s.StateKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        bool changed = !_currentEntry.CollapsedTimedSectionKeys.SequenceEqual(collapsedKeys, StringComparer.Ordinal);
        if (!changed)
        {
            return;
        }

        _currentEntry.CollapsedTimedSectionKeys = collapsedKeys;
        if (markDirty)
        {
            MarkDirty("Section visibility updated.");
        }
    }

    private void RefreshTimedMarkers()
    {
        Guid? selectedId = (TimedJournalList.SelectedItem as TimedJournalMarker)?.SectionId;

        _timedMarkers.Clear();
        foreach (TimedJournalSection section in _sections)
        {
            section.Marker = null;
            if (!section.IsTimed)
            {
                continue;
            }

            TimedJournalMarker marker = new()
            {
                SectionId = section.Id,
                Display = GetTimedMarkerDisplay(section),
                HeaderText = section.HeaderText
            };

            section.Marker = marker;
            _timedMarkers.Add(marker);
        }

        if (selectedId.HasValue)
        {
            TimedJournalMarker? selected = _timedMarkers.FirstOrDefault(m => m.SectionId == selectedId.Value);
            if (selected is not null)
            {
                _suppressTimedSelection = true;
                TimedJournalList.SelectedItem = selected;
                _suppressTimedSelection = false;
            }
        }

        UpdateTimedSidebarVisibility();
    }

    private void UpdateTimedHint()
    {
        if (GetSelectedFormat() != EntryFormat.Markdown)
        {
            TimedJournalHint.Text = string.Empty;
            return;
        }

        TimedJournalHint.Text = _timedMarkers.Count == 0
            ? "Timed Journal is empty. Add a line like 14/02/2026@09:30pm : Reflection, or use New Timed."
            : "Timed sections are live. Use the sidebar to jump and the arrow to collapse/expand each section.";
    }

    private static string GetTimedMarkerDisplay(TimedJournalSection section)
    {
        Match match = TimedHeaderRegex.Match(section.HeaderText);
        if (match.Success)
        {
            string datePart = match.Groups["date"].Value.Trim();
            string timePart = Regex.Replace(match.Groups["time"].Value.Trim(), @"\s+", string.Empty).ToLowerInvariant();
            return $"{datePart}@{timePart}";
        }

        if (section.OccurredAt.HasValue)
        {
            return section.OccurredAt.Value.LocalDateTime
                .ToString("dd/MM/yyyy@hh:mmtt", CultureInfo.InvariantCulture)
                .ToLowerInvariant();
        }

        return "Timed";
    }

    private void UpdateTimedSidebarVisibility()
    {
        bool hasTimedItems = _timedMarkers.Count > 0;
        TimedJournalExpander.Visibility = hasTimedItems ? Visibility.Visible : Visibility.Collapsed;

        if (!hasTimedItems)
        {
            _suppressTimedSelection = true;
            TimedJournalList.SelectedItem = null;
            _suppressTimedSelection = false;
        }
    }

    private void RefreshTimedSectionHeader(TimedJournalSection section)
    {
        if (!TryParseTimedHeader(section.HeaderText, out TimedHeaderData header))
        {
            section.IsTimed = false;
            section.Display = "Notes";
            section.OccurredAt = null;
            return;
        }

        section.IsTimed = true;
        section.Display = header.Display;
        section.OccurredAt = header.OccurredAt;
        section.HeaderText = header.HeaderText;
    }

    private static bool TryParseTimedHeader(string line, out TimedHeaderData header)
    {
        Match match = TimedHeaderRegex.Match(line);
        if (!match.Success)
        {
            header = default;
            return false;
        }

        string datePart = match.Groups["date"].Value.Trim();
        string timePart = match.Groups["time"].Value.Trim();
        string labelPart = match.Groups["label"].Value.Trim();

        DateTimeOffset? timestamp = TryParseTimestamp(datePart, timePart);
        string normalizedHeader = $"{datePart}@{timePart} : {labelPart}".TrimEnd();
        string label = string.IsNullOrWhiteSpace(labelPart) ? "Timed note" : labelPart;

        header = new TimedHeaderData(
            normalizedHeader,
            timestamp.HasValue ? $"{timestamp.Value.LocalDateTime:g} - {label}" : $"{datePart}@{timePart} - {label}",
            timestamp);

        return true;
    }

    private static DateTimeOffset? TryParseTimestamp(string datePart, string timePart)
    {
        string date = datePart.Replace('-', '/').Replace('.', '/');
        string time = Regex.Replace(timePart, @"\s+", string.Empty).ToLowerInvariant();
        string candidate = $"{date} {time}";

        if (DateTime.TryParseExact(candidate, TimestampPatterns, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime parsed))
        {
            return new DateTimeOffset(parsed);
        }

        if (DateTimeOffset.TryParse($"{date} {timePart}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTimeOffset parsedOffset))
        {
            return parsedOffset;
        }

        return null;
    }

    private void PersistEditorIntoCurrentEntry()
    {
        if (_currentEntry is null || _isLoading)
        {
            return;
        }

        if (GetSelectedFormat() == EntryFormat.Markdown)
        {
            ResyncTimedSectionsFromEditors();
        }

        _currentEntry.Format = GetSelectedFormat();
        CaptureRichTextToEntry(_currentEntry);
        if (_currentEntry.Format == EntryFormat.Markdown)
        {
            PersistMarkdownIntoCurrentEntry();
        }
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentEntry is null)
        {
            return;
        }

        PersistEditorIntoCurrentEntry();

        ContentDialog warningDialog = new()
        {
            Title = "Export Warning",
            Content = "Exported files are not encrypted. Their content will be visible to anyone who can open the file.",
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = await GetDialogXamlRootAsync()
        };

        if (await warningDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        FileSavePicker picker = new()
        {
            SuggestedFileName = MakeSafeFileName(_currentEntry.DisplayTitle)
        };
        picker.FileTypeChoices.Add("Markdown", new List<string> { ".md" });
        picker.FileTypeChoices.Add("Text", new List<string> { ".txt" });
        picker.FileTypeChoices.Add("Rich Text", new List<string> { ".rtf" });
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        string extension = Path.GetExtension(file.Name).ToLowerInvariant();
        string content = extension switch
        {
            ".md" => GetEntryMarkdownForExport(_currentEntry),
            ".rtf" => GetEntryRtfForExport(_currentEntry),
            _ => GetEntryPlainTextForExport(_currentEntry)
        };

        await FileIO.WriteTextAsync(file, content);
        StatusText.Text = $"Exported \"{_currentEntry.DisplayTitle}\" to {file.Name}.";
    }

    private async void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add(".rtf");

        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        string content = await FileIO.ReadTextAsync(file);
        string extension = Path.GetExtension(file.Name).ToLowerInvariant();
        string importedTitle = Path.GetFileNameWithoutExtension(file.Name);
        if (string.IsNullOrWhiteSpace(importedTitle))
        {
            importedTitle = $"Imported {_entries.Count + 1}";
        }

        DiaryEntry entry = CreateEntry(importedTitle);
        switch (extension)
        {
            case ".rtf":
                entry.Format = EntryFormat.RichText;
                entry.RichTextRtf = content;
                entry.MarkdownText = string.Empty;
                break;
            case ".md":
                entry.Format = EntryFormat.Markdown;
                entry.MarkdownText = content;
                entry.RichTextRtf = string.Empty;
                break;
            default:
                entry.Format = EntryFormat.Markdown;
                entry.MarkdownText = content;
                entry.RichTextRtf = string.Empty;
                break;
        }

        entry.UpdatedUtc = DateTimeOffset.UtcNow;
        _entries.Insert(0, entry);
        EntryList.SelectedItem = entry;
        MarkDirty("Entry imported.", true);
    }

    private static string MakeSafeFileName(string value)
    {
        string cleaned = TextSanitizer.Sanitize(value, allowNewLines: false).Trim();
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            cleaned = "KiaryEntry";
        }

        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            cleaned = cleaned.Replace(invalid, '_');
        }

        return cleaned;
    }

    private string GetEntryMarkdownForExport(DiaryEntry entry)
    {
        if (entry.Format == EntryFormat.Markdown)
        {
            return TextSanitizer.Sanitize(entry.MarkdownText, allowNewLines: true);
        }

        return GetEntryPlainTextForExport(entry);
    }

    private string GetEntryRtfForExport(DiaryEntry entry)
    {
        if (entry.Format == EntryFormat.RichText && _currentEntry?.Id == entry.Id)
        {
            CaptureRichTextToEntry(entry);
        }

        string rtf = TextSanitizer.Sanitize(entry.RichTextRtf, allowNewLines: true);
        if (!string.IsNullOrWhiteSpace(rtf))
        {
            return rtf;
        }

        return GetEntryPlainTextForExport(entry);
    }

    private string GetEntryPlainTextForExport(DiaryEntry entry)
    {
        if (_currentEntry?.Id == entry.Id)
        {
            try
            {
                RichEditor.Document.GetText(TextGetOptions.None, out string text);
                return TextSanitizer.Sanitize(text, allowNewLines: true);
            }
            catch
            {
            }
        }

        if (entry.Format == EntryFormat.Markdown)
        {
            return TextSanitizer.Sanitize(entry.MarkdownText, allowNewLines: true);
        }

        return TextSanitizer.Sanitize(entry.MarkdownText, allowNewLines: true);
    }

    private void ResyncTimedSectionsFromEditors()
    {
        if (_isLoading || _buildingTimedUi)
        {
            return;
        }

        string markdown = ComposeMarkdownText();
        List<TimedJournalSection> reparsed = ParseSections(markdown, CaptureCurrentSectionExpansionStates(), GetPersistedCollapsedTimedKeys());
        if (AreSectionsEquivalent(_sections, reparsed))
        {
            return;
        }

        _sections.Clear();
        foreach (TimedJournalSection section in reparsed)
        {
            _sections.Add(section);
        }

        RenderTimedSections();
    }

    private static bool AreSectionsEquivalent(List<TimedJournalSection> left, List<TimedJournalSection> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (int i = 0; i < left.Count; i++)
        {
            TimedJournalSection a = left[i];
            TimedJournalSection b = right[i];

            if (a.IsTimed != b.IsTimed)
            {
                return false;
            }

            if (!string.Equals(a.HeaderText, b.HeaderText, StringComparison.Ordinal))
            {
                return false;
            }

            if (!string.Equals(a.BodyText, b.BodyText, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private string GetTimedSourceText(DiaryEntry entry)
    {
        if (entry.Format == EntryFormat.Markdown)
        {
            return TextSanitizer.Sanitize(entry.MarkdownText, allowNewLines: true);
        }

        try
        {
            RichEditor.Document.GetText(TextGetOptions.None, out string text);
            return TextSanitizer.Sanitize(text, allowNewLines: true);
        }
        catch
        {
            return TextSanitizer.Sanitize(entry.MarkdownText, allowNewLines: true);
        }
    }

    private void CaptureRichTextToEntry(DiaryEntry entry)
    {
        try
        {
            RichEditor.Document.GetText(TextGetOptions.FormatRtf, out string rtf);
            entry.RichTextRtf = rtf;
        }
        catch
        {
            RichEditor.Document.GetText(TextGetOptions.None, out string plain);
            entry.RichTextRtf = plain;
        }
    }

    private void PersistMarkdownIntoCurrentEntry()
    {
        if (_currentEntry is null)
        {
            return;
        }

        _currentEntry.MarkdownText = ComposeMarkdownText();
    }

    private string ComposeMarkdownText()
    {
        StringBuilder builder = new();

        for (int i = 0; i < _sections.Count; i++)
        {
            TimedJournalSection section = _sections[i];
            string header = section.HeaderEditor?.Text ?? section.HeaderText;
            string body = section.BodyEditor?.Text ?? section.BodyText;

            if (section.IsTimed)
            {
                builder.Append(header.TrimEnd());
                builder.Append('\n');
            }

            if (!string.IsNullOrEmpty(body))
            {
                builder.Append(body.Replace("\r\n", "\n"));
            }

            if (i < _sections.Count - 1)
            {
                builder.Append('\n');
            }
        }

        return builder.ToString().TrimEnd('\r', '\n');
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        SaveVault(false);
    }

    private bool SaveVault(bool isAutoSave)
    {
        if (!_vaultInitialized || string.IsNullOrWhiteSpace(_masterPassword))
        {
            return false;
        }

        PersistEditorIntoCurrentEntry();
        _document.Entries = _entries.ToList();

        try
        {
            WriteDiagnostics($"Saving {_entries.Count} entries to {_repository.VaultPath} (auto={isAutoSave})");
            _repository.Save(_document, _masterPassword);
            _isDirty = false;
            if (!isAutoSave)
            {
                StatusText.Text = $"Saved {DateTime.Now:t}.";
            }

            return true;
        }
        catch (Exception ex)
        {
            WriteDiagnostics(isAutoSave ? "Auto-save failed" : "Save failed", ex);
            StatusText.Text = (isAutoSave ? "Auto-save failed: " : "Save failed: ") + ex.Message;
            return false;
        }
    }

    private void WriteDiagnostics(string message, Exception? exception = null)
    {
        try
        {
            string directory = Path.GetDirectoryName(_diagnosticsPath)!;
            Directory.CreateDirectory(directory);

            string text = $"{DateTimeOffset.Now:u} {message}";
            if (exception is not null)
            {
                text += $"{Environment.NewLine}{exception}{Environment.NewLine}";
            }

            File.AppendAllText(_diagnosticsPath, text + Environment.NewLine);
        }
        catch
        {
            // best-effort diagnostics only
        }
    }

    private void MarkDirty(string status, bool updateStatus = false)
    {
        if (_currentEntry is null || _isLoading)
        {
            return;
        }

        _currentEntry.UpdatedUtc = DateTimeOffset.UtcNow;
        _isDirty = true;

        int index = _entries.IndexOf(_currentEntry);
        if (index > 0)
        {
            _entries.Move(index, 0);
            EntryList.SelectedItem = _currentEntry;
        }

        if (updateStatus)
        {
            StatusText.Text = status;
        }
    }

    private readonly record struct MarkdownSearchTarget(TimedJournalSection Section, TextBox Editor, string Text);

    private sealed class TimedJournalSection
    {
        public Guid Id { get; } = Guid.NewGuid();
        public bool IsTimed { get; set; }
        public bool IsExpanded { get; set; } = true;
        public string StateKey { get; set; } = string.Empty;
        public string HeaderText { get; set; } = string.Empty;
        public string BodyText { get; set; } = string.Empty;
        public DateTimeOffset? OccurredAt { get; set; }
        public string Display { get; set; } = "Notes";
        public Expander? Expander { get; set; }
        public TextBox? HeaderEditor { get; set; }
        public TextBox? BodyEditor { get; set; }
        public TimedJournalMarker? Marker { get; set; }
    }

    private readonly record struct TimedHeaderData(string HeaderText, string Display, DateTimeOffset? OccurredAt);
}
