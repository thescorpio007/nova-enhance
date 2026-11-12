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
        new("grok", "Grok", "openai", "https://api.x.ai/v1", "grok-3-mini", "https://console.x.ai/"),
        new("local", "Local AI", "openai", "", "", "https://lmstudio.ai"),
        new("server", "Other server", "openai", "", "llama-3.3-70b-versatile", "https://console.groq.com/keys")
    };

    public static Provider ById(string id) => All.FirstOrDefault(p => p.Id == id) ?? All[0];

    // ---------------------------------------------------------------- 💻 Local AI and 🌐 other servers (OpenAI-compatible)
    public static string LocalUrl { get => Store.Str("win_local_url", "http://localhost:1234/v1").TrimEnd('/'); set => Store.Set("win_local_url", value?.Trim().TrimEnd('/')); }
    public static string ServerUrl { get => Store.Str("win_server_url", "https://api.groq.com/openai/v1").TrimEnd('/'); set => Store.Set("win_server_url", value?.Trim().TrimEnd('/')); }
    public static bool LocalOn { get => Store.Bool("win_local_on", false); set => Store.SetBool("win_local_on", value); }
    public static bool ServerOn { get => Store.Bool("win_server_on", false); set => Store.SetBool("win_server_on", value); }
    /// <summary>manual = the default AI only · private = local AI for your emails and documents · local-first = local, cloud when it fails.</summary>
    public static string Route { get => Store.Str("win_ai_route", "manual"); set => Store.Set("win_ai_route", value); }
    public static bool LocalNoAsk { get => Store.Bool("win_local_noask", true); set => Store.SetBool("win_local_noask", value); }
    /// <summary>Gemini (cloud) or the local AI for reading scanned pages and drawings.</summary>
    public static string VisionBy { get => Store.Str("win_vision", "gemini"); set => Store.Set("win_vision", value); }
    public static int LocalContext { get => Store.Int("win_local_ctx", 16000); set => Store.SetInt("win_local_ctx", value); }

    public static string Url(Provider p) => p.Id == "local" ? LocalUrl : p.Id == "server" ? ServerUrl : p.BaseUrl;
    public static string LabelOf(Provider p) => p == null ? "AI" : p.Id == "local" ? "Local AI · " + Store.Model("local") : p.Id == "server" ? Store.Str("win_server_name", "Server") + " · " + Store.Model("server") : p.Label;

    public static bool HasKey(string id) => id == "local" ? LocalOn : id == "server" ? ServerOn && ServerUrl.Length > 0 : Store.Key(id).Length > 0;

    static readonly AsyncLocal<bool> priv = new();
    sealed class Undo : IDisposable { readonly Action a; public Undo(Action a) => this.a = a; public void Dispose() => a(); }
    /// <summary>Marks the work in this block as private (your emails and documents) for the routing rule.</summary>
    public static IDisposable Private() { var old = priv.Value; priv.Value = true; return new Undo(() => priv.Value = old); }

    /// <summary>The AI the next question goes to: the routing rule first, then your default AI.</summary>
    public static Provider Current()
    {
        if (HasKey("local") && (Route == "local-first" || (Route == "private" && priv.Value))) return ById("local");
        return Active();
    }

    public static bool CurrentIsLocal => Current()?.Id == "local";

    /// <summary>How many characters of sources fit (local models have a small memory).</summary>
    public static int MaxChars => CurrentIsLocal ? Math.Max(6000, LocalContext * 3 - 6000) : 400_000;

    /// <summary>The models a local or company server offers (GET /models).</summary>
    public static async Task<List<string>> Models(string url, string key = "")
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url.TrimEnd('/') + "/models");
        if (key.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        using var res = await Http.SendAsync(req, cts.Token);
        var j = JsonNode.Parse(await res.Content.ReadAsStringAsync(cts.Token));
        return (j?["data"] as JsonArray ?? new JsonArray()).Select(x => x?["id"]?.ToString() ?? "").Where(x => x.Length > 0 && !x.Contains("embed", StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static readonly (string Name, string Url)[] LocalPresets =
    {
        ("LM Studio", "http://localhost:1234/v1"), ("Ollama", "http://localhost:11434/v1"), ("Jan", "http://localhost:1337/v1"),
        ("GPT4All", "http://localhost:4891/v1"), ("llama.cpp", "http://localhost:8080/v1"), ("KoboldCpp", "http://localhost:5001/v1")
    };

    public static readonly (string Name, string Url, string Model, string KeyUrl)[] ServerPresets =
    {
        ("Groq (free, very fast)", "https://api.groq.com/openai/v1", "llama-3.3-70b-versatile", "https://console.groq.com/keys"),
        ("OpenRouter (many models, one key)", "https://openrouter.ai/api/v1", "openrouter/auto", "https://openrouter.ai/keys"),
        ("Mistral", "https://api.mistral.ai/v1", "mistral-medium-latest", "https://console.mistral.ai/api-keys"),
        ("Together AI", "https://api.together.xyz/v1", "meta-llama/Llama-3.3-70B-Instruct-Turbo", "https://api.together.ai/settings/api-keys"),
        ("Perplexity", "https://api.perplexity.ai", "sonar", "https://www.perplexity.ai/settings/api"),
        ("Company / office server", "", "", "")
    };

    /// <summary>Finds a running local AI server (LM Studio, Ollama, Jan…): (name, url, models) or null.</summary>
    public static async Task<(string Name, string Url, List<string> Models)?> Detect()
    {
        foreach (var (n, u) in LocalPresets)
            try { var m = await Models(u); return (n, u, m); } catch { }
        return null;
    }
    public static Provider[] WithKeys => All.Where(p => HasKey(p.Id)).ToArray();

    /// <summary>The chosen AI, or the first one that has a key.</summary>
    public static Provider Active()
    {
        var p = ById(Store.Target);
        return HasKey(p.Id) ? p : All.FirstOrDefault(x => HasKey(x.Id) && x.Id != "local") ?? All.FirstOrDefault(x => HasKey(x.Id));
    }

    static readonly HttpClient Http = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(120) };
    /// <summary>Local models are slower: up to 10 minutes for one answer.</summary>
    static readonly HttpClient SlowHttp = new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromMinutes(10) };
    static readonly Dictionary<string, int> ThinkMode = new();

    /// <summary>Who answered the last question (shown under the answer when another AI stood in).</summary>
    public static string LastBy = "";

    /// <summary>The cloud AI that stands in for the local one: your default if it is cloud, then Gemini, then any key.</summary>
    static Provider CloudBackup()
    {
        var a = Active();
        if (a != null && a.Id != "local") return a;
        if (HasKey("gemini")) return ById("gemini");
        return WithKeys.FirstOrDefault(x => x.Id != "local");
    }

    class Busy : Exception { public Busy(string m) : base(m) { } }

    /// <summary>One question, one answer. Retries busy/connection errors twice.</summary>
    public static async Task<string> Chat(string system, string user, bool fast = true, bool json = false, Provider p = null, CancellationToken ct = default)
    {
        if (p != null) return await ChatOne(system, user, fast, json, p, ct);
        var first = Current();
        try { var r0 = await ChatOne(system, user, fast, json, first, ct); LastBy = LabelOf(first); return r0; }
        catch (AiException e) when (!ct.IsCancellationRequested)
        {
            var others = new List<Provider>();
            // Local AI not running or too slow → your cloud AI (routing rule).
            // LM Studio closed (option A): the question goes to Gemini instead — unless it holds private words (those stay on this PC).
            bool down = e.Message.Contains("not running", StringComparison.OrdinalIgnoreCase);
            if (first?.Id == "local" && (Route != "manual" || down))
            {
                if (Projects.Private.IsMatch(system + " " + user))
                { if (down) throw new AiException("LM Studio is closed, and this question holds private words (bank, password…) so Nova kept it on this PC — start LM Studio (Developer → Start server) and ask again"); }
                else if (CloudBackup() is { } cloud) others.Add(cloud);
            }
            // Your AI is busy? Settings → ✦ Prompts: use my other AI key.
            if (Store.Str("win_ai_busy", "other") == "other" && Regex.IsMatch(e.Message, "busy|too long|429|503|500|overloaded|quota|rate", RegexOptions.IgnoreCase))
                others.AddRange(WithKeys.Where(x => x.Id != first?.Id && !others.Contains(x)));
            foreach (var other in others)
            {
                try { var r = await ChatOne(system, user, fast, json, other, ct); Log.Warn("ai", $"{LabelOf(first)}: {e.Message} — answered by {LabelOf(other)}"); LastBy = LabelOf(other) + (down ? " (LM Studio is closed)" : ""); return r; }
                catch (AiException) { }
            }
            throw;
        }
    }

    /// <summary>🧪 Every AI call is timed and recorded in the Activity history (with the text sent, option C).</summary>
    static async Task<string> ChatOne(string system, string user, bool fast, bool json, Provider p, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await ChatOneCore(system, user, fast, json, p, ct);
            Activity.AiCall(LabelOf(p), system, user, r, sw.ElapsedMilliseconds);
            return r;
        }
        catch (Exception e)
        {
            Activity.AiCall(LabelOf(p), system, user, null, sw.ElapsedMilliseconds, e is OperationCanceledException ? "stopped / time limit" : e.Message);
            throw;
        }
    }

    static async Task<string> ChatOneCore(string system, string user, bool fast, bool json, Provider p, CancellationToken ct)
    {
        p ??= Active() ?? throw new AiException("No AI key yet — add one in Settings → AI & API keys");
        var key = Store.Key(p.Id);
        if (p.Id is "local" or "server") { if (key.Length == 0) key = "nova"; }   // local servers need no key
        else if (key.Length == 0) throw new AiException($"No {p.Label} key — add it in Settings → AI & API keys");
        var model = Store.Model(p.Id);
        Exception last = null;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return await Once(p, key, model, system, user, fast, json, ct); }
            catch (Busy e) { last = e; await Task.Delay(1500 * (attempt + 1), ct); }
            catch (HttpRequestException e) when (p.Id == "local") { throw new AiException($"The local AI is not running at {LocalUrl} — start the server in LM Studio (Developer → Start server) ({e.Message})"); }
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
                var slow = p.Id is "local" or "server";
                var r = await Post(Url(p) + "/chat/completions", body, new() { ["Authorization"] = "Bearer " + key }, ct, slow);
                var text = r["choices"]?[0]?["message"]?["content"]?.ToString() ?? "";
                // DeepSeek R1 and other "thinking" models: only the answer, not the thinking.
                text = Regex.Replace(text, @"<think>[\s\S]*?</think>", "", RegexOptions.IgnoreCase);
                var cut = text.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
                if (cut >= 0) text = text[(cut + 8)..];
                return text.Trim();
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

    /// <summary>
    /// 👁 Nova LOOKS at a picture or a drawing page with your question in mind (like a web AI): the vision of your local AI, Gemini,
    /// or your current AI when it can see (Claude, OpenAI and compatible servers). Only after you agreed to send it.
    /// </summary>
    public static async Task<string> Look(byte[] image, string mime, string question, CancellationToken ct = default)
    {
        var ask = "You are a senior architect / technical coordinator looking at this picture or drawing for the user. " +
                  (string.IsNullOrWhiteSpace(question) ? "" : $"The user's question: “{question}”. ") +
                  "Describe precisely what it shows that matters for the question: the kind of drawing or photo, elements and materials, dimensions, levels, notes, symbols, title block (number, revision, title), " +
                  "and anything wrong, missing, damaged or not as drawn (for site photos). Read every visible text and dimension that is relevant. Be factual; say when something is unclear.";
        if (VisionBy == "local" && HasKey("local")) return await Vision(image, mime, ct, ask);
        if (Store.Key("gemini").Length > 0) return await Vision(image, mime, ct, ask);
        var p = Current();
        if (p == null || p.Id == "local") throw new AiException("Seeing pictures needs a vision AI: a Gemini key, or a vision model on your local AI (Settings → AI & API keys)");
        var key = Store.Key(p.Id);
        var model = Store.Model(p.Id);
        var b64 = Convert.ToBase64String(image);
        if (p.Api == "anthropic")
        {
            var body = new JsonObject
            {
                ["model"] = model, ["max_tokens"] = 3000,
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = b64 } },
                        new JsonObject { ["type"] = "text", ["text"] = ask })
                })
            };
            var r = await Post(p.BaseUrl + "/messages", body, new() { ["x-api-key"] = key, ["anthropic-version"] = "2023-06-01" }, ct, true);
            var sb = new StringBuilder();
            foreach (var c in r["content"]?.AsArray() ?? new JsonArray()) if (c?["type"]?.ToString() == "text") sb.Append(c["text"]?.ToString());
            return sb.ToString().Trim();
        }
        var ob = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(
                    new JsonObject { ["type"] = "text", ["text"] = ask },
                    new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{b64}" } })
            })
        };
        var or = await Post(Url(p) + "/chat/completions", ob, new() { ["Authorization"] = "Bearer " + key }, ct, true);
        return Regex.Replace(or["choices"]?[0]?["message"]?["content"]?.ToString() ?? "", @"<think>[\s\S]*?</think>", "").Trim();
    }

    /// <summary>Who will look at pictures (for the question asked before sending).</summary>
    public static string LookerLabel => VisionBy == "local" && HasKey("local") ? "your local AI (on this PC)" : Store.Key("gemini").Length > 0 ? "Gemini" : LabelOf(Current());
    public static bool LooksLocally => VisionBy == "local" && HasKey("local");

    /// <summary>Gemini vision: all the text on a scanned page or picture (only after the user agreed to send it).</summary>
    public static async Task<string> Vision(byte[] image, string mime, CancellationToken ct = default, string ask = null)
    {
        var Ask = ask ?? "Transcribe ALL the text visible in this scanned page or drawing: title block, notes, labels, dimensions, tables, stamps and handwriting, in any language (English, Arabic, French…). Keep a natural reading order, one item per line. Reply with the text only.";
        if (VisionBy == "local" && HasKey("local"))
        {
            // The local AI reads the page on this PC (a vision model such as Gemma, Qwen-VL or LLaVA).
            var vm = Store.Str("win_local_vision_model");
            var lb = new JsonObject
            {
                ["model"] = vm.Length > 0 ? vm : Store.Model("local"),
                ["messages"] = new JsonArray(new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray(
                        new JsonObject { ["type"] = "text", ["text"] = Ask },
                        new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{Convert.ToBase64String(image)}" } })
                })
            };
            try
            {
                var lr = await Post(LocalUrl + "/chat/completions", lb, new() { ["Authorization"] = "Bearer nova" }, ct, true);
                return Regex.Replace(lr["choices"]?[0]?["message"]?["content"]?.ToString() ?? "", @"<think>[\s\S]*?</think>", "").Trim();
            }
            catch (HttpRequestException e) { throw new AiException($"The local AI is not running at {LocalUrl} ({e.Message})"); }
        }
        var key = Store.Key("gemini");
        if (key.Length == 0) throw new AiException("Gemini vision needs a Gemini key (Settings → AI & API keys)");
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(
                    new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = Convert.ToBase64String(image) } },
                    new JsonObject { ["text"] = Ask })
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

    static async Task<JsonObject> Post(string url, JsonObject body, Dictionary<string, string> headers, CancellationToken ct, bool slow = false)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        foreach (var h in headers)
        {
            if (h.Key == "Authorization") req.Headers.Authorization = AuthenticationHeaderValue.Parse(h.Value);
            else req.Headers.TryAddWithoutValidation(h.Key, h.Value);
        }
        using var res = await (slow ? SlowHttp : Http).SendAsync(req, ct);
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
