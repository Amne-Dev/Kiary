using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EncryptedDiary;

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
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "EncryptedDiary");

        Directory.CreateDirectory(dataRoot);
        _filePath = Path.Combine(dataRoot, "diary.edry");
    }

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
}
