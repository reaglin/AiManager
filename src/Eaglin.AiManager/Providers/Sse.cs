using System.Runtime.CompilerServices;
using System.Text;

namespace Eaglin.AiManager.Providers;

/// <summary>One server-sent event: the optional <c>event:</c> name and the joined <c>data:</c> lines.</summary>
internal readonly record struct SseEvent(string? Event, string Data);

/// <summary>
/// Minimal server-sent-events reader (RFC-style: blank line ends an event, <c>:</c> lines are
/// comments, multiple <c>data:</c> lines join with newlines). All five vendors stream this way.
/// Cancelling the token cancels the pending read, which is how a stream is stopped.
/// </summary>
internal static class SseReader
{
    public static async IAsyncEnumerable<SseEvent> ReadAsync(Stream stream, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? evt = null;
        var data = new StringBuilder();

        while (true)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) break;

            if (line.Length == 0)
            {
                if (evt is not null || data.Length > 0)
                    yield return new SseEvent(evt, data.ToString());
                evt = null;
                data.Clear();
                continue;
            }
            if (line[0] == ':') continue;

            var idx   = line.IndexOf(':');
            var field = idx < 0 ? line : line[..idx];
            var value = idx < 0 ? string.Empty : line[(idx + 1)..];
            if (value.StartsWith(' ')) value = value[1..];

            switch (field)
            {
                case "event": evt = value; break;
                case "data":
                    if (data.Length > 0) data.Append('\n');
                    data.Append(value);
                    break;
            }
        }

        if (evt is not null || data.Length > 0)
            yield return new SseEvent(evt, data.ToString());
    }
}
