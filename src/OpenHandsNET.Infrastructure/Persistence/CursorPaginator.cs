using System.Text;

namespace OpenHandsNET.Infrastructure.Persistence;

/// <summary>
/// Shared opaque cursor codec for cursored pagination (SPEC-10 RF-004):
/// <c>page_id</c> is base64 of <c>{ticks}:{id}</c>.
/// </summary>
public static class CursorPaginator
{
    /// <summary>Encodes a cursor position.</summary>
    public static string Encode(long ticks, string id) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ticks}:{id}"));

    /// <summary>Decodes a cursor; false when missing/malformed.</summary>
    public static bool TryDecode(string? cursor, out long ticks, out string id)
    {
        ticks = 0;
        id = "";
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var sep = raw.IndexOf(':', StringComparison.Ordinal);
            if (sep > 0 && long.TryParse(raw[..sep], out ticks))
            {
                id = raw[(sep + 1)..];
                return id.Length > 0;
            }
        }
        catch (FormatException)
        {
        }

        return false;
    }
}
