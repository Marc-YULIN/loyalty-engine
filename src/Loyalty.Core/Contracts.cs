namespace Loyalty.Core;

public sealed record ActivityEventDto(
    string EventId,
    Guid MemberId,
    ActivityEventType Type,
    string Category,
    decimal Amount,
    DateTimeOffset OccurredAt,
    string? OriginalEventId = null);

public sealed class DomainException(string code, string message, int statusCode = 422) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public sealed record LeaderboardEntry(Guid MemberId, int Rank, decimal Score);
public sealed record LeaderboardResult(IReadOnlyList<LeaderboardEntry> Entries, string Source);
public sealed record MemberRank(Guid MemberId, int Rank, decimal Score, string Source);

public interface ILeaderboardStore
{
    Task IncrementAsync(Guid campaignId, Guid memberId, decimal delta, CancellationToken cancellationToken = default);
    Task<LeaderboardResult> GetTopAsync(Guid campaignId, int top, CancellationToken cancellationToken = default);
    Task<MemberRank?> GetRankAsync(Guid campaignId, Guid memberId, CancellationToken cancellationToken = default);
}
