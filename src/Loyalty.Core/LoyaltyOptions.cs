using System.ComponentModel.DataAnnotations;

namespace Loyalty.Core;

public sealed class LoyaltyOptions : IValidatableObject
{
    [Required, StringLength(3, MinimumLength = 3)]
    public string Currency { get; set; } = "USD";

    [Range(1, 3650)]
    public int RollingWindowDays { get; set; } = 30;

    [Range(0, 3650)]
    public int DowngradeGraceDays { get; set; } = 14;

    public bool EnableTierReviewWorker { get; set; } = true;

    [Required]
    public Dictionary<string, decimal> Tiers { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bronze"] = 0m, ["Silver"] = 1_000m, ["Gold"] = 5_000m, ["Platinum"] = 20_000m
    };

    [Required]
    public Dictionary<string, Dictionary<string, decimal>> CashbackRates { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bronze"] = new() { ["*"] = 0.005m },
        ["Silver"] = new() { ["*"] = 0.0075m },
        ["Gold"] = new() { ["*"] = 0.01m, ["electronics"] = 0.015m },
        ["Platinum"] = new() { ["*"] = 0.02m, ["electronics"] = 0.025m }
    };

    [Required]
    public Dictionary<string, decimal> DailyClaimCap { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bronze"] = 20m, ["Silver"] = 50m, ["Gold"] = 150m, ["Platinum"] = 500m
    };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Tiers is null || Tiers.Count == 0)
        {
            yield return new ValidationResult("At least one tier is required.", [nameof(Tiers)]);
            yield break;
        }

        if (Tiers.Any(t => string.IsNullOrWhiteSpace(t.Key) || t.Value < 0m)
            || Tiers.Values.Distinct().Count() != Tiers.Count || Tiers.Values.Min() != 0m)
            yield return new ValidationResult("Tier thresholds must be unique, nonnegative, and start at zero.", [nameof(Tiers)]);

        if (Tiers.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != Tiers.Count)
            yield return new ValidationResult("Tier codes must be unique ignoring case.", [nameof(Tiers)]);

        foreach (var tier in Tiers.Keys)
        {
            var rates = CashbackRates?.FirstOrDefault(r => string.Equals(r.Key, tier, StringComparison.OrdinalIgnoreCase)).Value;
            if (rates is null || !rates.ContainsKey("*") || rates.Any(r => string.IsNullOrWhiteSpace(r.Key) || r.Value < 0m || r.Value > 1m))
                yield return new ValidationResult($"Tier '{tier}' requires valid cashback rates and a fallback rate.", [nameof(CashbackRates)]);

            var cap = DailyClaimCap?.FirstOrDefault(c => string.Equals(c.Key, tier, StringComparison.OrdinalIgnoreCase));
            if (cap is null || string.IsNullOrEmpty(cap.Value.Key) || cap.Value.Value < 0m)
                yield return new ValidationResult($"Tier '{tier}' requires a nonnegative daily claim cap.", [nameof(DailyClaimCap)]);
        }
    }
}
