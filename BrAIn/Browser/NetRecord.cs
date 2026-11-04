using System.Text.Json;

namespace BrAIn.Browser;

/// <summary>Turns a record of the in-page network recorder (Assets/netrec.js) into a <see cref="NetworkEntry"/>, and merges
/// successive records of one request: the recorder resends a changed record whole, except that a response body is only
/// resent when it is new and WebSocket frames only as the ones added since the last read.</summary>
internal static class NetRecord
{
    public static NetworkEntry ToEntry(JsonElement r, string doc, double origin)
    {
        string? Str(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        double Num(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
        bool Flag(string n) => r.TryGetProperty(n, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.GetDouble() != 0));
        Dictionary<string, string>? Dict(string n)
        {
            if (!r.TryGetProperty(n, out var v) || v.ValueKind != JsonValueKind.Object) return null;
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in v.EnumerateObject()) d[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : p.Value.ToString();
            return d;
        }

        NetTiming? tm = null;
        if (r.TryGetProperty("tm", out var t) && t.ValueKind == JsonValueKind.Object)
        {
            double T(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            tm = new NetTiming(T("q"), T("dns"), T("con"), T("tls"), T("wait"), T("rcv"));
        }
        List<NetFrame>? frames = null;
        if (r.TryGetProperty("fr", out var fr) && fr.ValueKind == JsonValueKind.Array)
        {
            frames = new();
            foreach (var f in fr.EnumerateArray())
                frames.Add(new NetFrame(
                    f.TryGetProperty("d", out var d) ? d.GetString() ?? "r" : "r",
                    f.TryGetProperty("t", out var ft) ? ft.GetDouble() : 0,
                    f.TryGetProperty("n", out var fn) ? fn.GetInt32() : 0,
                    f.TryGetProperty("x", out var fx) ? fx.GetString() : null));
        }

        var start = Num("s");
        var status = (int)Num("st");
        var body = Str("body");
        var size = (long)Num("sz");
        if (size == 0) size = (long)Num("bl"); // a fetch the timeline has not described yet: at least the text length
        string? close = r.TryGetProperty("cc", out _) ? $"{(int)Num("cc")} {Str("cr")}".Trim() : null;
        var x = new NetExtra(
            Doc: doc, Kind: Str("k") ?? "other", StartMs: start, DurationMs: Num("ms"), DocOrigin: origin,
            StatusText: Str("stt"), Mime: Str("ct"), Protocol: Str("pr"), DecodedBytes: (long)Num("dsz"), Timing: tm,
            ResponseBody: body, BodyTruncated: Flag("bt"), BodyLength: (long)Num("bl"), BodyDropped: Flag("bx"),
            Redirected: Flag("red"), FinalUrl: Str("fu"), Done: Flag("dn"), Frames: frames, FrameCount: (int)Num("nf"), CloseInfo: close);
        return new NetworkEntry(
            DateTimeOffset.FromUnixTimeMilliseconds((long)(origin + start)).LocalDateTime,
            Str("id") ?? Guid.NewGuid().ToString("N")[..8], Str("m") ?? "GET", Str("u") ?? "",
            status > 0 ? status : null, Str("ty"), size, Str("er"),
            Dict("rq"), Dict("rs"), Str("pd"), x);
    }

    /// <summary>The recorder's newer record wins; what it only sends once (the body, the frames added) is kept from the older one.</summary>
    public static NetExtra? MergeExtra(NetExtra? old, NetExtra? next)
    {
        if (next is null) return old;
        if (old is null) return next;
        List<NetFrame>? frames = null;
        if (old.Frames is not null || next.Frames is not null)
        {
            frames = new(old.Frames ?? Array.Empty<NetFrame>());
            if (next.Frames is not null) frames.AddRange(next.Frames);
            if (frames.Count > 400) frames.RemoveRange(0, frames.Count - 400);
        }
        var keepBody = next.ResponseBody is null && !next.BodyDropped;
        return next with
        {
            Tab = next.Tab ?? old.Tab,
            ResponseBody = keepBody ? old.ResponseBody : next.ResponseBody,
            BodyTruncated = keepBody ? old.BodyTruncated : next.BodyTruncated,
            BodyLength = keepBody ? old.BodyLength : next.BodyLength,
            Timing = next.Timing ?? old.Timing,
            Frames = frames,
            FrameCount = Math.Max(old.FrameCount, next.FrameCount),
        };
    }
}
