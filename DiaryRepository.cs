using System.Text.Json;
using System.Text.Json.Serialization;

namespace EncryptedDiary.WinUI;

public sealed class DiaryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _filePath;

    public DiaryRepository()
    {
        string dataRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Kiary");

        Directory.CreateDirectory(dataRoot);
        _filePath = Path.Combine(dataRoot, "diary.edry");
        MigrateLegacyVaultIfNeeded();
    }

    public string VaultPath => _filePath;

    public bool Exists()
    {
        return File.Exists(_filePath);
    }

    public DiaryDocument LoadOrCreate(string password)
    {
        if (!Exists())
        {
            return new DiaryDocument();
        }

        byte[] encryptedPayload = File.ReadAllBytes(_filePath);
        string json = DiaryCrypto.Decrypt(encryptedPayload, password);
        DiaryDocument? document = JsonSerializer.Deserialize<DiaryDocument>(json, JsonOptions);

        if (document is null)
        {
            return new DiaryDocument();
        }

        document.Entries ??= new List<DiaryEntry>();
        NormalizeEntries(document.Entries);
        return document;
    }

    public void Save(DiaryDocument document, string password)
    {
        string json = JsonSerializer.Serialize(document, JsonOptions);
        byte[] encryptedPayload = DiaryCrypto.Encrypt(json, password);
        string tempPath = _filePath + ".tmp";

        File.WriteAllBytes(tempPath, encryptedPayload);
        File.Move(tempPath, _filePath, true);
    }

    private void MigrateLegacyVaultIfNeeded()
    {
        if (File.Exists(_filePath))
        {
            return;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] legacyRoots =
        {
            Path.Combine(localAppData, "EncryptedDiaryWinUI"),
            Path.Combine(localAppData, "EncryptedDiary")
        };

        foreach (string legacyRoot in legacyRoots)
        {
            string legacyFile = Path.Combine(legacyRoot, "diary.edry");
            if (!File.Exists(legacyFile))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.Copy(legacyFile, _filePath, overwrite: false);
            return;
        }
    }

    private static void NormalizeEntries(List<DiaryEntry> entries)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        for (int i = 0; i < entries.Count; i++)
        {
            DiaryEntry entry = entries[i];
            if (entry is null)
            {
                entries[i] = new DiaryEntry
                {
                    CreatedUtc = now,
                    UpdatedUtc = now
                };
                continue;
            }

            entry.Title = entry.Title;
            entry.RichTextRtf = entry.RichTextRtf;
            entry.MarkdownText = entry.MarkdownText;

            if (!Enum.IsDefined(typeof(EntryFormat), entry.Format))
            {
                entry.Format = EntryFormat.RichText;
            }

            if (entry.CreatedUtc == default)
            {
                entry.CreatedUtc = now;
            }

            if (entry.UpdatedUtc == default)
            {
                entry.UpdatedUtc = entry.CreatedUtc;
            }
        }
    }
}
