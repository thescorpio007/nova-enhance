namespace NovaEnhance;

/// <summary>
/// 🌐 Universal Nova: each person picks a FIELD (first start, or Settings → About me). The field pack gives Nova the right person to be
/// in every AI instruction, the document codes to read, the short forms to search and neutral examples. Nothing personal is built in:
/// your own words, rules and projects live in your settings (and your backup). Designed by A.HAMROUNI
/// </summary>
class Pack
{
    public string Id = "", Icon = "", Name = "", Desc = "";
    /// <summary>Default role ("architect / technical coordinator") and the expert Nova acts as.</summary>
    public string Person = "professional", Expert = "senior professional in this field";
    public Dictionary<string, string> Disciplines = new(StringComparer.OrdinalIgnoreCase), Types = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Examples shown in boxes: a project code, a document number, a search, an "about me".</summary>
    public string ExProject = "ACME", ExDoc = "ACME-REP-0012", ExSearch = "latest report about the budget", ExAbout = "", ExTerms = "", ExStandards = "";
}

static class Packs
{
    public static readonly List<Pack> All = new()
    {
        new Pack
        {
            Id = "construction", Icon = "🏗", Name = "Construction & Architecture", Desc = "drawings, submittals, RFIs, codes",
            Person = "architect / technical coordinator", Expert = "senior architect / technical coordinator",
            Disciplines = new(StringComparer.OrdinalIgnoreCase)
            {
                ["AR"] = "Architecture", ["ID"] = "Interior design", ["LS"] = "Landscape", ["ST"] = "Structure", ["MS"] = "Mechanical",
                ["ME"] = "MEP", ["MEP"] = "MEP", ["EL"] = "Electrical", ["PL"] = "Plumbing", ["FF"] = "Firefighting", ["FS"] = "Fire safety",
                ["HV"] = "HVAC", ["CW"] = "Civil works", ["CV"] = "Civil", ["CI"] = "Civil", ["IN"] = "Infrastructure", ["PM"] = "Project management",
                ["QA"] = "Quality", ["QC"] = "Quality", ["HS"] = "HSE", ["FA"] = "Façade", ["FC"] = "Façade", ["LT"] = "Lighting", ["LI"] = "Lighting",
                ["AV"] = "Audio-visual", ["IT"] = "ICT", ["ICT"] = "ICT", ["SG"] = "Signage", ["VT"] = "Vertical transport", ["LF"] = "Lifts",
                ["GE"] = "General", ["GN"] = "General", ["SU"] = "Survey", ["GT"] = "Geotechnical", ["WP"] = "Waterproofing", ["KT"] = "Kitchen"
            },
            Types = new(StringComparer.OrdinalIgnoreCase)
            {
                ["SSM"] = "Sample", ["MAR"] = "Material approval", ["MAT"] = "Material", ["MIR"] = "Material inspection", ["WIR"] = "Work inspection",
                ["RFI"] = "RFI", ["SDS"] = "Shop drawing", ["SD"] = "Shop drawing", ["DRW"] = "Drawing", ["MSS"] = "Method statement", ["MST"] = "Method statement",
                ["MS"] = "Method statement", ["NCR"] = "Non-conformance", ["TQ"] = "Technical query", ["LTR"] = "Letter", ["TRN"] = "Transmittal",
                ["SI"] = "Site instruction", ["EI"] = "Engineer's instruction", ["PQ"] = "Prequalification", ["CAL"] = "Calculation", ["RPT"] = "Report",
                ["MOM"] = "Minutes", ["DET"] = "Detail", ["GEN"] = "General", ["COM"] = "Commercial", ["VO"] = "Variation", ["SUB"] = "Submittal",
                ["TS"] = "Technical submittal", ["DS"] = "Design submittal", ["ASB"] = "As-built", ["SNG"] = "Snag", ["HO"] = "Handover", ["IR"] = "Inspection"
            },
            ExProject = "PRJ", ExDoc = "PRJ-ABC-AR-SDS-00123", ExSearch = "latest IFC architectural plans for level 02, PDF only, sorted by drawing number",
            ExAbout = "Site architect: design development and coordination with structural and MEP", ExTerms = "IFC, RFI, MEP, GFRC", ExStandards = "BS EN, ASTM, NFPA, local fire code"
        },
        new Pack { Id = "engineering", Icon = "⚙", Name = "Engineering", Desc = "specs, calculations, reports", Person = "engineer", Expert = "senior engineer",
            Types = new(StringComparer.OrdinalIgnoreCase) { ["SPEC"] = "Specification", ["CAL"] = "Calculation", ["RPT"] = "Report", ["DRW"] = "Drawing", ["TQ"] = "Technical query", ["NCR"] = "Non-conformance" },
            ExDoc = "PRJ-CAL-0042", ExSearch = "latest load calculation report", ExAbout = "Mechanical engineer: design and testing of pumping systems", ExTerms = "FAT, SAT, P&ID", ExStandards = "ISO, IEC, ASME" },
        new Pack { Id = "medical", Icon = "🩺", Name = "Medical & Health", Desc = "patients, protocols, drug names", Person = "health professional", Expert = "senior clinician",
            ExDoc = "PROT-2026-07", ExSearch = "latest protocol for antibiotic stewardship", ExAbout = "Clinical pharmacist in a hospital", ExTerms = "ICU, PRN, BID, eGFR", ExStandards = "WHO, NICE, local health authority guidelines" },
        new Pack { Id = "legal", Icon = "⚖", Name = "Legal", Desc = "contracts, clauses, cases", Person = "legal professional", Expert = "senior lawyer",
            ExDoc = "CTR-2026-018", ExSearch = "latest draft of the supply contract", ExAbout = "Corporate lawyer: contracts and disputes", ExTerms = "NDA, SPA, LOI", ExStandards = "applicable law and regulations" },
        new Pack { Id = "finance", Icon = "💰", Name = "Finance & Accounting", Desc = "invoices, audits, budgets", Person = "finance professional", Expert = "senior accountant",
            Types = new(StringComparer.OrdinalIgnoreCase) { ["INV"] = "Invoice", ["PO"] = "Purchase order", ["CN"] = "Credit note", ["RPT"] = "Report" },
            ExDoc = "INV-2026-00451", ExSearch = "unpaid invoices from last month", ExAbout = "Accountant: payables, month-end closing and audits", ExTerms = "AP, AR, VAT, IFRS", ExStandards = "IFRS, local tax rules" },
        new Pack { Id = "teaching", Icon = "🎓", Name = "Teaching & Education", Desc = "lessons, students, reports", Person = "teacher", Expert = "experienced teacher",
            ExDoc = "Grade 8 · Unit 3", ExSearch = "lesson plan for fractions", ExAbout = "Math teacher for grades 7–9", ExTerms = "IEP, KPI", ExStandards = "the curriculum framework" },
        new Pack { Id = "student", Icon = "📚", Name = "Student", Desc = "courses, assignments, exams", Person = "student", Expert = "helpful tutor",
            ExDoc = "BIO-201 Assignment 2", ExSearch = "notes about photosynthesis", ExAbout = "University student in biology", ExTerms = "", ExStandards = "the course material" },
        new Pack { Id = "business", Icon = "💼", Name = "Business & Sales", Desc = "clients, offers, orders", Person = "business professional", Expert = "senior business manager",
            Types = new(StringComparer.OrdinalIgnoreCase) { ["QT"] = "Quotation", ["PO"] = "Purchase order", ["INV"] = "Invoice" },
            ExDoc = "QT-2026-117", ExSearch = "offers sent to the client last week", ExAbout = "Sales manager for industrial equipment", ExTerms = "RFQ, PO, CRM, KPI", ExStandards = "company policy" },
        new Pack { Id = "it", Icon = "💻", Name = "IT & Software", Desc = "tickets, releases, systems", Person = "IT professional", Expert = "senior software engineer",
            ExDoc = "TICKET-4521", ExSearch = "incident reports about the VPN", ExAbout = "System administrator for a mid-size company", ExTerms = "VPN, SSO, SLA, API", ExStandards = "ITIL, ISO 27001" },
        new Pack { Id = "government", Icon = "🏛", Name = "Government & Admin", Desc = "letters, approvals, circulars", Person = "administrative officer", Expert = "senior administrator",
            ExDoc = "REF/2026/0345", ExSearch = "circulars about leave policy", ExAbout = "Administrative officer handling approvals and correspondence", ExTerms = "", ExStandards = "official regulations" },
        new Pack { Id = "general", Icon = "🌐", Name = "General", Desc = "everyday writing", Person = "person", Expert = "helpful assistant",
            ExDoc = "", ExSearch = "the email about the family trip", ExAbout = "", ExTerms = "", ExStandards = "" },
    };

    public static Pack ById(string id) => All.FirstOrDefault(p => p.Id == id) ?? All.First(p => p.Id == "general");

    /// <summary>The person's field (Settings → About me). Old installs with an architecture profile keep the construction pack.</summary>
    public static Pack Current => ById(Store.Str("win_field", "general"));
    public static bool IsConstruction => Store.Str("win_field", "general") == "construction";
}

/// <summary>Who Nova works for, built from the profile — used in every AI instruction instead of a fixed job.</summary>
static class Persona
{
    public static string Role { get => Store.Str("p_role"); set => Store.Set("p_role", value?.Trim() ?? ""); }
    public static string Country { get => Store.Str("p_country"); set => Store.Set("p_country", value?.Trim() ?? ""); }

    static string A(string w) => w.Length > 0 && "aeiouAEIOU".Contains(w[0]) ? "an " + w : "a " + w;

    /// <summary>"an architect / technical coordinator in the United Arab Emirates" · "a pharmacist" · "a person".</summary>
    public static string Who
    {
        get
        {
            var role = Role.Length > 0 ? Role : Packs.Current.Person;
            return A(role) + (Country.Length > 0 ? " in " + Country : "");
        }
    }

    /// <summary>"senior architect / technical coordinator" · "senior clinician" …</summary>
    public static string Expert => Packs.Current.Expert;
    public static string Where => Country.Length > 0 ? " in " + Country : "";

    /// <summary>A first start: nothing set up yet (no key, no profile, no projects) — the welcome screen shows.</summary>
    public static bool Fresh =>
        Store.Str("win_field", "").Length == 0 && Store.ProfileFields.Length == 0 && Store.ProfileAbout.Length == 0 &&
        !Ai.WithKeys.Any(p => p.Id != "local") && Projects.All().Count == 0;

    /// <summary>Installs from before 2.0 universal: an architecture profile (or projects) keeps today's behaviour.</summary>
    public static void Migrate()
    {
        if (Store.Str("win_field", "").Length > 0) return;
        if (Fresh) return;                                            // the welcome screen will ask
        var f = (Store.ProfileFields + " " + Store.ProfileAbout).ToLowerInvariant();
        var construction = f.Contains("archit") || f.Contains("construct") || f.Contains("structur") || f.Contains("mep") || Projects.All().Count > 0;
        Store.Set("win_field", construction ? "construction" : "general");
        if (construction)
        {
            if (Role.Length == 0) Role = "architect / technical coordinator";
            if (Country.Length == 0) Country = "the United Arab Emirates";
        }
        // Settings that used to be built in stay as they were for this PC.
        if (Store.Str("win_tc_relay_repo", "").Length == 0 && Store.Bool("win_tc_relay", false)) Store.Set("win_tc_relay_repo", "thescorpio007/nova-test-relay");
        if (Store.Bool("win_tc_on", false) || Store.Bool("win_tc_relay", false) || Store.Bool("win_tc_remote", false)) Store.SetBool("win_advanced", true);
    }
}
