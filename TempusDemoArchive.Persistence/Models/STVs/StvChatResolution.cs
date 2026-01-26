using System.ComponentModel.DataAnnotations.Schema;

namespace TempusDemoArchive.Persistence.Models.STVs;

public class StvChatResolution
{
    public ulong DemoId { get; set; }
    public int ChatIndex { get; set; }
    public int? ClientEntityId { get; set; }
    public int? FromUserId { get; set; }
    public int? ResolvedUserId { get; set; }
    public int? CandidateCount { get; set; }
    public string? CandidateUserIdsCsv { get; set; }
    public string? Text { get; set; }
    public int? Tick { get; set; }

    [NotMapped]
    public IReadOnlyList<int> CandidateUserIds
    {
        get
        {
            if (string.IsNullOrWhiteSpace(CandidateUserIdsCsv))
            {
                return Array.Empty<int>();
            }

            var parts = CandidateUserIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ids = new List<int>(parts.Length);
            foreach (var part in parts)
            {
                if (int.TryParse(part, out var parsed))
                {
                    ids.Add(parsed);
                }
            }

            return ids;
        }
    }
}
