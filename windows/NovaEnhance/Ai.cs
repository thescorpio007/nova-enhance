using System.Text.RegularExpressions;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NovaEnhance;

record Provider(string Id, string Label, string Api, string BaseUrl, string DefaultModel, string KeyUrl);

class AiException : Exception { public AiException(string m) : base(m) { } }

/// <summary>The same five AIs as the phone, Gemini first. Busy errors are retried; thinking is turned off for quick jobs.</summary>
static class Ai
{
    public static readonly Provider[] All =
    {
        new("gemini", "Gemini", "gemini", "https://generativelanguage.googleapis.com/v1beta", "gemini-3.1-flash-lite", "https://aistudio.google.com/app/apikey"),
        new("chatgpt", "ChatGPT", "openai", "https://api.openai.com/v1", "gpt-4o-mini", "https://platform.openai.com/api-keys"),
        new("claude", "Claude", "anthropic", "https://api.anthropic.com/v1", "claude-sonnet-5-5", "https://console.anthropic.com/settings/keys"),
        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com", "deepseek-chat", "https://platform.deepseek.com/api_keys"),
        new("grok", "Grok", "openai", "https://api.x.ai/v1", "grok-3-mini", "https://console.x.ai/")
    };

    public static Provider ById(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];
    public static bool HasKey(string id) => Store.Key(id).Length > 0;
    public static Provider[] WithKeys => All.Where(p => HasKey(p.Id)).ToArray();

    /// <summary>The chosen AI, or the first one that has a key.</summary>
    public static Provider Active()
    {
        var p = ById(Store.Target);
        return HasKey(p.Id) ? p : All.FirstOrDefault(x => HasKey(x.Id));
    }

    static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(120) };
    static readonly Dictionary<string, int> ThinkMode = new();

    class Busy : Exception { public Busy(string m) : base(m) { } }

    /// <summary>One question, one answer. Retries busy/connection errors twice.</summary>
    public static async Task<string> Chat(string system, string user, bool fast = true, bool json = false, Provider p = null, CancellationToken ct = default)
    {
        if (p != null || Store.Str("win_ai_busy", "other") != "other" || WithKeys.Length < 2) return await ChatOne(system, user, fast, json, p, ct);
        // Your AI is busy? Settings → ✦ Prompts: use my other AI key.
        var first = Active();
        try { return await ChatOne(system, user, fast, json, first, ct); }
        catch (AiException e) when (!ct.IsCancellationRequested && Regex.IsMatch(e.Message, "busy|too long|429|503|500|overloaded|quota|rate", RegexOptions.IgnoreCase))
        {
            foreach (var other in WithKeys.Where(x => x.Id != first?.Id))
            {
                try { var r = await ChatOne(system, user, fast, json, other, ct); Log.Warn("ai", $"{first?.Label} busy — answered by {other.Label}"); return r; }
                catch (AiException) { }
            }
            throw;
        }
    }

    static async Task<string> ChatOne(string system, string user, bool fast, bool json, Provider p, CancellationToken ct)
    {
        p ??= Active() ?? throw new AiException("No AI key yet — add one in Settings → AI & API keys");
        var key = Store.Key(p.Id);
        if (key.Length == 0) throw new AiException($"No {p.Label} key — add it in Settings → AI & API keys");
        var model = Store.Model(p.Id);
        Exception last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return await Once(p, key, model, system, user, fast, json, ct); }
            catch (Busy e) { last = e; await Task.Delay(1500 * (attempt + 1), ct); }
            catch (HttpRequestException e) { last = new AiException("No connection — check the internet or VPN (" + e.Message + ")"); await Task.Delay(1200, ct); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { last = new AiException($"{p.Label} took too long to answer"); }
        }
        throw last is AiException ? last : new AiException($"{p.Label} is busy right now — try again in a moment");
    }

    static async Task<string> Once(Provider p, string key, string model, string system, string user, bool fast, bool json, CancellationToken ct)
    {
        switch (p.Api)
        {
            case "anthropic":
            {
                var body = new JsonObject
                {
                    ["model"] = model, ["max_tokens"] = 8000, ["system"] = system,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = user })
                };
                var r = await Post(p.BaseUrl + "/messages", body, new() { ["x-api-key"] = key, ["anthropic-version"] = "2023-06-01" }, ct);
                var sb = new StringBuilder();
                foreach (var c in r["content"]?.AsArray() ?? new JsonArray())
                    if (c?["type"]?.ToString() == "text") sb.Append(c["text"]?.ToString());
                return sb.ToString().Trim();
            }
            case "gemini":
                return (await GeminiText(key, model, new JsonObject
                {
                    ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
                    ["contents"] = new JsonArray(new JsonObject { ["role"] = "user", ["parts"] = new JsonArray(new JsonObject { ["text"] = user }) })
                }, fast, json, ct)).Trim();
            default:
            {
                var body = new JsonObject
                {
                    ["model"] = model,
                    ["messages"] = new JsonArray(new JsonObject { ["role"] = "system", ["content"] = system }, new JsonObject { ["role"] = "user", ["content"] = user })
                };
                var r = await Post(p.BaseUrl + "/chat/completions", body, new() { ["Authorization"] = "Bearer " + key }, ct);
                return (r["choices"]?[0]?["message"]?["content"]?.ToString() ?? "").Trim();
            }
        }
    }

    /// <summary>Gemini, with thinking off for speed when the model allows it (same ladder as the phone).</summary>
    static async Task<string> GeminiText(string key, string model, JsonObject body, bool fast, bool json, CancellationToken ct)
    {
        var url = $"{ById("gemini").BaseUrl}/models/{model}:generateContent";
        int mode = fast ? (ThinkMode.TryGetValue(model, out var m) ? m : 0) : 2;
        while (true)
        {
            var b = body.DeepClone().AsObject();
            var gen = new JsonObject();
            if (mode == 0) gen["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 };
            if (mode == 1) gen["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = "low" };
            if (json) gen["responseMimeType"] = "application/json";
            if (gen.Count > 0) b["generationConfig"] = gen;
            try
            {
                var r = await Post(url, b, new() { ["x-goog-api-key"] = key }, ct);
                if (fast) lock (ThinkMode) ThinkMode[model] = mode;
                var sb = new StringBuilder();
                foreach (var part in r["candidates"]?[0]?["content"]?["parts"]?.AsArray() ?? new JsonArray())
                    if (part?["thought"]?.ToString() != "true") sb.Append(part?["text"]?.ToString());
                if (sb.Length == 0)
                {
                    var why = r["promptFeedback"]?["blockReason"]?.ToString() ?? r["candidates"]?[0]?["finishReason"]?.ToString() ?? "empty answer";
                    throw new AiException("Gemini gave no text (" + why + ")");
                }
                return sb.ToString();
            }
            catch (AiException e) when (e.Message.StartsWith("400") && mode < 2) { mode++; }
        }
    }

    /// <summary>Speech to text with Gemini (the recording goes to Gemini only for this).</summary>
    public static async Task<string> Transcribe(byte[] wav, CancellationToken ct = default)
    {
        var key = Store.Key("gemini");
        if (key.Length == 0) throw new AiException("Voice needs a Gemini key (Settings → AI & API keys)");
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = "audio/wav", ["data"] = Convert.ToBase64String(wav) } },
                    new JsonObject { ["text"] = "Transcribe this recording exactly, in the language spoken (English, Arabic or French). Reply with the words only." })
            })
        };
        return (await GeminiText(key, Store.Model("gemini"), body, true, false, ct)).Trim();
    }

    /// <summary>Gemini vision: all the text on a scanned page or picture (only after the user agreed to send it).</summary>
    public static async Task<string> Vision(byte[] image, string mime, CancellationToken ct = default)
    {
        var key = Store.Key("gemini");
        if (key.Length == 0) throw new AiException("Gemini vision needs a Gemini key (Settings → AI & API keys)");
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = Convert.ToBase64String(image) } },
                    new JsonObject { ["text"] = "Transcribe ALL the text visible in this scanned page or drawing: title block, notes, labels, dimensions, tables, stamps and handwriting, in any language (English, Arabic, French…). Keep a natural reading order, one item per line. Reply with the text only." })
            })
        };
        Exception last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return (await GeminiText(key, Store.Model("gemini"), body, true, false, ct)).Trim(); }
            catch (Busy e) { last = e; await Task.Delay(2000 * (attempt + 1), ct); }
        }
        throw new AiException("Gemini is busy — try again later (" + last?.Message + ")");
    }

    static async Task<JsonObject> Post(string url, JsonObject body, Dictionary<string, string> headers, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        foreach (var h in headers)
        {
            if (h.Key == "Authorization") req.Headers.Authorization = AuthenticationHeaderValue.Parse(h.Value);
            else req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        int code = (int)res.StatusCode;
        if (code == 429 || code == 500 || code == 502 || code == 503 || code == 504 || code == 529) throw new Busy($"{code}");
        if (!res.IsSuccessStatusCode)
        {
            string msg = text;
            try { var j = JsonNode.Parse(text); msg = j?["error"]?["message"]?.ToString() ?? j?["error"]?.ToString() ?? text; } catch { }
            if (msg.Length > 300) msg = msg[..300];
            var hint = code switch
            {
                401 or 403 => " — check the API key in Settings",
                404 => " — the model name may be wrong (Settings → AI & API keys)",
                _ => ""
            };
            throw new AiException($"{code} {msg}{hint}");
        }
        try { return JsonNode.Parse(text)?.AsObject() ?? new JsonObject(); }
        catch { throw new AiException("Unexpected answer from the AI"); }
    }

    /// <summary>Takes the first JSON object out of an answer (some AIs wrap it in ``` or add words).</summary>
    public static JsonObject ParseJson(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new JsonObject();
        int a = s.IndexOf('{'), b = s.LastIndexOf('}');
        if (a < 0 || b <= a) return new JsonObject();
        try { return JsonNode.Parse(s[a..(b + 1)])?.AsObject() ?? new JsonObject(); } catch { return new JsonObject(); }
    }

    /// <summary>Enhancement with the phone's instructions and the user's profile. Long texts are split and joined back.</summary>
    public static async Task<string> Enhance(Provider p, string modeId, string text, CancellationToken ct, string extra = "")
    {
        var mode = Modes.ById(modeId);
        var parts = Split(text.Replace("\r\n", "\n"), 6000);
        var outs = new List<string>();
        var sys = Modes.System() + (string.IsNullOrWhiteSpace(extra) ? "" : " Also follow these instructions from the user: " + extra.Trim());
        foreach (var part in parts)
            outs.Add(await Chat(sys, Modes.FirstMessage(mode, part), true, false, p, ct));
        return string.Join("\n\n", outs);
    }

    /// <summary>
    /// Excel: many cells in one request. Each cell is handled on its own; codes, references, names and numbers come back unchanged.
    /// Returns the new text by cell id (cells the AI left out are missing from the result).
    /// </summary>
    public static async Task<Dictionary<string, string>> EnhanceCells(Provider p, string modeId, IList<(string Id, string Text)> cells, CancellationToken ct)
    {
        var mode = Modes.ById(modeId);
        string instr = mode.Id switch
        {
            "custom" => string.IsNullOrWhiteSpace(Store.Custom) ? "Improve this text." : Store.Custom,
            "translate" => $"Translate the text into natural, fluent {(Store.TrLang.Equals("Arabic", StringComparison.OrdinalIgnoreCase) ? "Modern Standard Arabic" : Store.TrLang)}, keeping the meaning, names and technical terms. If a cell is already in that language, return it unchanged.",
            _ => mode.Instruction
        };
        var sys = "You are Nova Enhance working on spreadsheet cells. You receive a JSON object \"cells\" mapping a cell id to its text, and one instruction. " +
                  "Apply the instruction to EACH cell separately; never merge, split, move or drop cells. " +
                  "Return a cell UNCHANGED when it is a code, drawing or document reference, revision, item number, unit, name, e-mail, link, number or date " +
                  "(e.g. \"A-201 Rev C\", \"D-03\", \"RFI-044\", \"mm\", \"Nos\"), or when it needs no change. " +
                  "Keep each cell's line breaks, and keep it about as short as it was unless the instruction says otherwise. " +
                  "Reply with ONLY a JSON object {\"cells\": {...}} with exactly the same ids." + Modes.ProfileNote();
        var input = new JsonObject();
        foreach (var (id, text) in cells) input[id] = text;
        var user = $"Instruction: {instr}\n\n" + new JsonObject { ["cells"] = input }.ToJsonString(new JsonSerializerOptions { Encoder = global::System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var reply = await Chat(sys, user, true, true, p, ct);
        var res = new Dictionary<string, string>();
        var o = ParseJson(reply);
        var map = o["cells"] as JsonObject ?? o;
        foreach (var (id, _) in cells)
            if (map[id] is JsonValue v && v.TryGetValue<string>(out var t)) res[id] = t;
        return res;
    }

    static List<string> Split(string t, int max)
    {
        var res = new List<string>();
        if (t.Length <= max) { res.Add(t); return res; }
        var sb = new StringBuilder();
        foreach (var para in t.Split("\n\n"))
        {
            if (sb.Length > 0 && sb.Length + para.Length > max) { res.Add(sb.ToString()); sb.Clear(); }
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(para);
        }
        if (sb.Length > 0) res.Add(sb.ToString());
        return res;
    }

    /// <summary>Quick check used by "Save &amp; test". Returns seconds.</summary>
    public static async Task<double> Test(Provider p)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Chat("Reply with the single word OK.", "Say OK", true, false, p);
        if (r.Length == 0) throw new AiException("Empty answer");
        return sw.Elapsed.TotalSeconds;
    }
}

record Mode(string Id, string Label, string Instruction);

/// <summary>The same modes and instructions as the phone app.</summary>
static class Modes
{
    public static readonly Mode[] All =
    {
        new("grammar", "Fix grammar", "Correct all grammar, spelling and punctuation mistakes. Keep the meaning, tone and language exactly as they are."),
        new("shorten", "Shorten", "Make the text shorter and more concise. Keep all key points and the original language."),
        new("professional", "Professional", "Rewrite the text in a clear, professional and formal tone suitable for business communication. Keep every fact."),
        new("informal", "Informal", "Rewrite the text in a warm, friendly and informal tone, as you would write to a colleague you know well. Keep every fact and the original language."),
        new("expand", "Expand", "Expand the text with more detail, explanation and smooth flow, without inventing facts that change its meaning."),
        new("translate", "Translate", ""),
        new("custom", "Custom", "")
    };

    public static Mode ById(string id) => All.FirstOrDefault(m => m.Id == id) ?? All[0];

    public static readonly (string Value, string Label)[] MainLangs = { ("English", "English"), ("Arabic", "العربية"), ("French", "Français"), ("German", "Deutsch") };
    public static readonly (string Value, string Label)[] OtherLangs =
    {
        ("Spanish", "Español"), ("Italian", "Italiano"), ("Turkish", "Türkçe"), ("Urdu", "اردو Urdu"),
        ("Hindi", "हिन्दी"), ("Chinese", "中文"), ("Russian", "Русский"), ("Portuguese", "Português")
    };

    public static string LangLabel(string l = null)
    {
        l ??= Store.TrLang;
        foreach (var x in MainLangs.Concat(OtherLangs)) if (x.Value.Equals(l, StringComparison.OrdinalIgnoreCase)) return x.Label;
        return l;
    }

    public static string TranslateInstruction(string l = null)
    {
        l ??= Store.TrLang;
        var target = l.Equals("Arabic", StringComparison.OrdinalIgnoreCase) ? "Modern Standard Arabic" : l;
        var fallback = l.Equals("English", StringComparison.OrdinalIgnoreCase) ? "Modern Standard Arabic" : "English";
        return $"Translate the text into natural, fluent {target}, keeping the meaning, tone, names and technical terms. If the text is already in {l}, translate it into {fallback} instead.";
    }

    public static string Label(string id) => id == "translate" ? "Translate → " + LangLabel() : ById(id).Label;

    const string SYSTEM =
        "You are Nova Enhance, a text enhancement assistant. You receive a piece of text and an instruction. " +
        "Reply with ONLY the improved text: no introduction, no explanation, no quotation marks, no notes. " +
        "Keep the original language unless the instruction asks for a translation. Keep line breaks and lists where sensible. " +
        "In later messages of the conversation the user may ask for changes; then reply with the full revised text only.";

    public static string System() => SYSTEM + ProfileNote();

    public static string FirstMessage(Mode mode, string text)
    {
        var instr = mode.Id switch
        {
            "custom" => string.IsNullOrWhiteSpace(Store.Custom) ? "Improve this text." : Store.Custom,
            "translate" => TranslateInstruction(),
            _ => mode.Instruction
        };
        return $"Instruction: {instr}\n\nText:\n{text}";
    }

    public static readonly string[] Fields =
    {
        "Architecture", "Structural", "MEP", "Construction / Site", "Project management",
        "Interior design", "Civil", "Legal", "Medical", "IT", "Finance", "Education", "Marketing"
    };

    public static List<string> ChosenFields() => Store.ProfileFields.Split('|').Where(s => s.Trim().Length > 0).ToList();

    /// <summary>The "About me" profile, same wording as the phone.</summary>
    public static string ProfileNote()
    {
        var f = ChosenFields();
        var about = Store.ProfileAbout.Trim();
        var terms = Store.ProfileTerms.Trim();
        if (f.Count == 0 && about.Length == 0 && terms.Length == 0) return "";
        var sb = new StringBuilder("\n\nAbout the writer (use this to choose terminology):");
        if (f.Count > 0) sb.Append("\n- Field: ").Append(string.Join(", ", f));
        if (about.Length > 0) sb.Append("\n- In their words: ").Append(about);
        sb.Append("\nUse the correct, standard technical terminology of this field. Fix technical terms that are ");
        sb.Append("misspelled or misused (for example a wrong but similar word), but never change the meaning or add facts.");
        if (terms.Length > 0) sb.Append("\nKeep these terms, abbreviations and names exactly as written: ").Append(terms);
        return sb.ToString();
    }
}
