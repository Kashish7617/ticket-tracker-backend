using System.Text.Json;

namespace TicketTracker.Api.Infrastructure;

/// <summary>
/// Single source of truth for the wire names of enums ("in_progress"), shared by the JSON
/// serializer, query-string parsing and the summary endpoint's dictionary keys.
/// </summary>
public static class EnumNames
{
    public static readonly JsonNamingPolicy Policy = JsonNamingPolicy.SnakeCaseLower;

    public static string ToWireName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        Policy.ConvertName(value.ToString());

    public static IEnumerable<string> WireNames<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(ToWireName);

    /// <summary>Parses a wire name such as "in_progress". Numbers and unknown names are rejected.</summary>
    public static bool TryParse<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(ToWireName(candidate), value?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                result = candidate;
                return true;
            }
        }

        result = default;
        return false;
    }
}
