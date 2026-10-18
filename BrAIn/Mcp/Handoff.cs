namespace BrAIn.Mcp;

/// <summary>
/// "Your turn": the agent hits something only the user can do (a login, a CAPTCHA, a
/// 2FA code, a payment) and asks for help with browser_ask_human. The tool brings the
/// tab forward, shows a card in the browser (pushed as {b:49}) and waits. The user does
/// the step in the page and presses Done; the agent then continues from the live page.
/// The ids in the push are what authorise an answer, so they only go to the trusted UI.
/// </summary>
public sealed class Handoff
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(15);

    private sealed class Pending
    {
        public string Id = "";
        public string Message = "";
        public string Host = "";
        public string Url = "";
        public TaskCompletionSource<string> Answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly object _lock = new();
    private readonly List<Pending> _pending = new();

    /// <summary>Raised when the pending list changes (any thread).</summary>
    public event Action? Changed;

    public bool HasPending { get { lock (_lock) return _pending.Count > 0; } }

    /// <summary>While the user is busy with a handoff, other actions wait instead of racing them.</summary>
    public const string BusyText = "WAIT: the user is doing a step for you in BrAIn. Do not retry yet; wait until browser_ask_human returns.";

    public static string Clamp(string s, int max)
    {
        s = (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length <= max ? s : s[..max] + "…";
    }

    /// <summary>
    /// Waits for the user. Always returns text for the agent; never throws on timeout.
    /// Cancellation (the agent gave up) also removes the card.
    /// </summary>
    public async Task<string> AskAsync(string message, string? host, string? url, TimeSpan wait, CancellationToken ct)
    {
        if (wait <= TimeSpan.Zero) wait = DefaultWait;
        if (wait > MaxWait) wait = MaxWait;
        var msg = Clamp(message, 400);
        if (msg.Length == 0) msg = "Your AI agent needs your help with this page.";

        var p = new Pending
        {
            Id = Guid.NewGuid().ToString("N"),
            Message = msg,
            Host = (host ?? "").ToLowerInvariant(),
            Url = url ?? "",
        };
        lock (_lock) _pending.Add(p);
        Changed?.Invoke();

        string answer;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(wait);
            using (cts.Token.Register(() => p.Answer.TrySetResult(ct.IsCancellationRequested ? "cancel" : "timeout")))
                answer = await p.Answer.Task.ConfigureAwait(false);
        }
        finally
        {
            lock (_lock) _pending.Remove(p);
            Changed?.Invoke();
        }

        var mins = (int)Math.Round(wait.TotalMinutes);
        return answer switch
        {
            "done" => "DONE: the user says they did it. Continue the task: take a fresh browser_snapshot first, since the page has probably changed.",
            "cant" => "CANT: the user can't or won't do this step. Do not retry it or look for another route; tell the user what you need and ask how they want to go on.",
            "cancel" => "CANCELLED: the request was cancelled before the user answered.",
            _ => $"NO ANSWER: the user did not respond in BrAIn within {mins} minute{(mins == 1 ? "" : "s")}. Do not assume the step was done; tell the user what you need and ask them.",
        };
    }

    /// <summary>Answers every open request "can't do it" (the user cancelled the agent).</summary>
    public void CancelAll()
    {
        List<Pending> all;
        lock (_lock) all = _pending.ToList();
        foreach (var p in all) p.Answer.TrySetResult("cant");
    }

    /// <summary>Called from the UI as "id:done" or "id:cant". Unknown or stale ids are ignored.</summary>
    public void Answer(string idAndChoice)
    {
        var i = (idAndChoice ?? "").IndexOf(':');
        if (i <= 0) return;
        var id = idAndChoice![..i];
        var choice = idAndChoice[(i + 1)..];
        if (choice is not ("done" or "cant")) return;
        Pending? p;
        lock (_lock) p = _pending.FirstOrDefault(x => x.Id == id);
        p?.Answer.TrySetResult(choice);
    }

    /// <summary>The push payload for the UI.</summary>
    public object View()
    {
        lock (_lock)
            return new
            {
                ask = _pending.Select(p => new { id = p.Id, message = p.Message, host = p.Host }).ToArray(),
            };
    }
}
