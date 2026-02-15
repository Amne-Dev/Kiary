namespace EncryptedDiary.WinUI;

public sealed class TimedJournalMarker
{
    public required Guid SectionId { get; init; }
    public required string Display { get; init; }
    public required string HeaderText { get; init; }
}
