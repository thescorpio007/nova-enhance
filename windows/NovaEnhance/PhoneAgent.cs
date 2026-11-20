using System.Text.Json.Nodes;

namespace NovaEnhance;

/// <summary>
/// 🧭 The Nova agent from the phone (step 2): the phone sends the question, the PC runs the same agent on its data (Brain, log,
/// emails, threads), and the ✓ / ✗ examples go to the phone. Each call waits up to ~110 s and returns what is new:
/// steps, examples to confirm, or the result (also saved as a Nova Chat conversation, so you can continue on the PC).
/// 🔒 private emails never go to the phone. Designed by A.HAMROUNI
/// </summary>
static class PhoneAgent
{
    sealed class Run
    {
        public string Id = "", ChatId = "", Question = "";
        public readonly List<string> Steps = new();
        public string Detail = "";
        public JsonObject Confirm;   // waiting for ✓ / ✗
        public TaskCompletionSource<(List<long>, List<long>, string)?> Wait;
        public JsonObject Result; public string Error = "";
        public CancellationTokenSource Cts = new();
        public Agent.Session Session;
        public SemaphoreSlim Changed = new(0);
        public DateTime At = DateTime.Now;
        public void Poke() { try { Changed.Release(); } catch { } }
    }

    static readonly Dictionary<string, Run> runs = new();

    static bool Private(MailHit h) => Projects.Private.IsMatch($"{h.Subject} {h.Folder} {h.AttNames}");

    public static async Task<JsonObject> Handle(JsonObject a)
    {
        var sid = a["sid"]?.ToString() ?? "";
        Run r;
        lock (runs)
        {
            foreach (var old in runs.Where(x => DateTime.Now - x.Value.At > TimeSpan.FromHours(2)).Select(x => x.Key).ToList()) runs.Remove(old);
            runs.TryGetValue(sid, out r);
        }
        if (a["stop"]?.GetValue<bool>() == true && r != null) { r.Wait?.TrySetResult(null); r.Cts.Cancel(); return new JsonObject { ["state"] = "stopped" }; }
        if (a["confirm"] is JsonObject c && r?.Wait != null)
        {
            List<long> Ids(string k) => (c[k] as JsonArray)?.Select(x => long.TryParse(x?.ToString(), out var v) ? v : 0).Where(v => v > 0).ToList() ?? new();
            r.Confirm = null;
            r.Wait.TrySetResult((Ids("yes"), Ids("no"), c["note"]?.ToString() ?? ""));
        }
        else if (a["q"]?.ToString() is { Length: > 0 } q)
        {
            // A new question, or a follow-up in the same conversation (same chat id).
            var chat = a["chat"]?.ToString() ?? "";
            r = new Run { Id = Guid.NewGuid().ToString("N")[..12], Question = q, ChatId = chat.Length > 0 ? chat : DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Random.Shared.Next(100, 999) };
            r.Session = Agent.SessionFor(r.ChatId, null);
            lock (runs) runs[r.Id] = r;
            Start(r);
        }
        if (r == null) return new JsonObject { ["state"] = "unknown", ["text"] = "This request is no longer on the PC — ask again." };
        // Wait for something new (a step, examples, the result), up to ~100 s.
        var until = DateTime.Now.AddSeconds(100);
        int seen = a["seen"]?.GetValue<int>() ?? 0;
        while (DateTime.Now < until && r.Result == null && r.Error.Length == 0 && r.Confirm == null)
            if (await r.Changed.WaitAsync(TimeSpan.FromSeconds(3)) && r.Steps.Count > seen + 2) break;
        return State(r);
    }

    static JsonObject State(Run r)
    {
        var o = new JsonObject { ["sid"] = r.Id, ["chat"] = r.ChatId, ["steps"] = new JsonArray(r.Steps.Select(s => (JsonNode)s).ToArray()), ["detail"] = r.Detail };
        if (r.Error.Length > 0) { o["state"] = "error"; o["text"] = r.Error; }
        else if (r.Result != null) { o["state"] = "done"; o["result"] = r.Result.DeepClone(); }
        else if (r.Confirm != null) { o["state"] = "confirm"; o["confirm"] = r.Confirm.DeepClone(); }
        else o["state"] = "running";
        return o;
    }

    static void Start(Run r)
    {
        var host = new Agent.Host
        {
            Progress = s => { lock (r.Steps) r.Steps.Add(s); r.Poke(); },
            Detail = s => { r.Detail = s; },
            Confirm = async (q, ex) =>
            {
                var shown = ex.Where(h => !Private(h)).ToList();
                if (shown.Count == 0) return (new List<long>(), new List<long>(), "");
                r.Wait = new TaskCompletionSource<(List<long>, List<long>, string)?>();
                r.Confirm = new JsonObject { ["question"] = q, ["items"] = Agent.Examples(shown) };
                foreach (var it in (r.Confirm["items"] as JsonArray)!.OfType<JsonObject>()) { it.Remove("entry"); it.Remove("store"); }
                r.Poke();
                using var reg = r.Cts.Token.Register(() => r.Wait.TrySetResult(null));
                return await r.Wait.Task;
            }
        };
        _ = Task.Run(async () =>
        {
            using var busy = Busy.Begin("📱 Nova agent for the phone");
            try
            {
                var res = await Agent.Run(r.Question, r.Session, host, "chat", r.Cts.Token);
                // 🔒 private emails stay on the PC
                res.Rows.RemoveAll(x => x.Mail != null && Private(x.Mail));
                r.ChatId = NovaChat.SaveAgentTurn(r.ChatId, r.Question, res);
                var j = Agent.Json(res);
                foreach (var row in (j["rows"] as JsonArray)!.OfType<JsonObject>()) { row.Remove("entry"); row.Remove("store"); }
                r.Result = j;
            }
            catch (OperationCanceledException) { r.Error = "Stopped."; }
            catch (Exception e) { r.Error = e.Message; Log.Warn("phone agent", e.Message); }
            r.Poke();
        });
    }
}
