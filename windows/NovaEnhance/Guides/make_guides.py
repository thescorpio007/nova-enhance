# Builds the 📘 Nova Studio guides (PDF) embedded in Nova. Run: python3 make_guides.py   · Designed by A.HAMROUNI
import os
from reportlab.lib.pagesizes import A4
from reportlab.lib.styles import ParagraphStyle
from reportlab.lib.colors import HexColor
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, PageBreak
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
D = "/usr/share/fonts/truetype/dejavu/"
pdfmetrics.registerFont(TTFont("DV", D + "DejaVuSans.ttf")); pdfmetrics.registerFont(TTFont("DVB", D + "DejaVuSans-Bold.ttf"))
INK, ACC, MUT = HexColor("#1d2232"), HexColor("#5b3fd6"), HexColor("#5f6678")
T = ParagraphStyle("t", fontName="DVB", fontSize=20, leading=25, textColor=ACC, spaceAfter=4)
S = ParagraphStyle("s", fontName="DV", fontSize=10.5, leading=14, textColor=MUT, spaceAfter=14)
H = ParagraphStyle("h", fontName="DVB", fontSize=13, leading=17, textColor=INK, spaceBefore=10, spaceAfter=5)
P = ParagraphStyle("p", fontName="DV", fontSize=10.5, leading=15, textColor=INK, spaceAfter=5)
B = ParagraphStyle("b", parent=P, leftIndent=14, bulletIndent=2)
TIP = ParagraphStyle("tip", parent=P, backColor=HexColor("#efeafd"), borderPadding=7, spaceBefore=8, spaceAfter=10)
def foot(c, d):
    c.saveState(); c.setFont("DV", 8); c.setFillColor(MUT)
    c.drawString(40, 25, "Nova Studio guide · Designed by A.HAMROUNI"); c.drawRightString(A4[0] - 40, 25, "page %d" % d.page); c.restoreState()
def build(name, title, sub, parts):
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), name + ".pdf")
    doc = SimpleDocTemplate(out, pagesize=A4, leftMargin=48, rightMargin=48, topMargin=46, bottomMargin=46, title=title, author="A.HAMROUNI")
    st = [Paragraph(title, T), Paragraph(sub, S)]
    for kind, txt in parts:
        if kind == "h": st.append(Paragraph(txt, H))
        elif kind == "p": st.append(Paragraph(txt, P))
        elif kind == "b": st += [Paragraph(x, B, bulletText="•") for x in txt]
        elif kind == "n": st += [Paragraph(x, B, bulletText="%d." % (i + 1)) for i, x in enumerate(txt)]
        elif kind == "tip": st.append(Paragraph("<b>Tip · </b>" + txt, TIP))
        elif kind == "t":
            t = Table([[Paragraph(c, P) for c in r] for r in txt], colWidths=[130, 350])
            t.setStyle(TableStyle([("GRID", (0, 0), (-1, -1), 0.4, HexColor("#c9c5dd")), ("BACKGROUND", (0, 0), (-1, 0), HexColor("#efeafd")), ("VALIGN", (0, 0), (-1, -1), "TOP")]))
            st += [t, Spacer(1, 8)]
        elif kind == "br": st.append(PageBreak())
    doc.build(st, onFirstPage=foot, onLaterPages=foot)

build("studio-overview", "Nova Studio — develop Nova from inside Nova", "What it is, how a change travels, and the five habits that make it easy.", [
    ("h", "What Nova Studio does"),
    ("p", "Nova Studio lets you improve Nova with your own AI, without a web session. You describe a wish in plain words, attach a screenshot or an error, and the Studio prepares the change. Nothing changes until you press <b>GO</b>."),
    ("h", "How every change travels"),
    ("n", ["You describe the wish (what + where + a screenshot).", "The Studio AI reads Nova's code and the diagnostics it needs.", "It shows the plan and the exact change (the diff).", "You press <b>GO</b> — or ask for a correction.", "GitHub (or this PC in mode B) builds Nova and runs the tests.", "A signed build appears in Settings → Updates.", "You install it. If anything feels wrong: <b>Back to the previous build</b>, one click."]),
    ("h", "Five habits"),
    ("b", ["One wish per message. Two wishes = two messages.", "Always say <b>where</b>: the window, the button, the section.", "Add a screenshot or press <b>Add today's errors</b> — the AI then sees what you see.", "For screens, ask for a <b>mockup first</b>.", "Never install a build whose tests are red."]),
    ("tip", "Keys and passwords never go in the chat and never in a guide. Nova keeps them encrypted on this PC and gives the Studio only what it needs, without showing them."),
])
build("studio-which-mode", "Which mode is right for me?", "Three ways for Nova to develop itself — you can switch at any time.", [
    ("t", [["Mode", "In short"],
           ["C · Live extensions", "Brain rules and saved prompts written by the AI, applied at once. No build, no GitHub. Best for: new rules, a new report question, a filing code, a reminder."],
           ["A · GitHub pipeline", "The AI reads Nova's source on GitHub and proposes exact edits. On GO, GitHub builds, tests and signs. 10–15 minutes. Best for: real features and fixes, without tools on your PC."],
           ["B · Coding agent on this PC", "A coding agent edits, builds and checks its own copy of Nova here, then pushes on GO. The strongest. Needs Git, .NET SDK, Node.js and the agent (installed for you)."]]),
    ("h", "How to choose"),
    ("b", ["Start with <b>C</b> for everything a rule can do — it is instant.", "Use <b>A</b> when a screen or behaviour must change and you prefer nothing installed.", "Use <b>B</b> for bigger work: several files, careful building, quick rounds."]),
    ("tip", "A good rhythm: try C first; if the Studio says “this needs code”, switch to A or B with the same message."),
])
build("studio-setup-ai-key", "Set up · the AI key", "The Studio uses your own AI account. A coding model gives the best results.", [
    ("h", "Get a key (Anthropic · Claude)"),
    ("n", ["Open console.anthropic.com and sign in (or create the account).", "Billing: add credit, and set a monthly spend limit there too.", "API Keys → Create Key → name it “Nova Studio” → copy it once.", "In Nova: Settings → AI &amp; API keys → Claude → paste the key → Test.", "In Nova Studio → ② Set up → AI key: choose Claude, set your monthly budget, press Test."]),
    ("h", "Good to know"),
    ("b", ["The Studio stops by itself when the monthly budget is reached.", "Gemini or another provider also works for mode C and A; mode B needs the Claude key.", "The key is stored encrypted on this PC, never in a backup, never sent to the phone."]),
    ("tip", "If a key was ever shown or pasted somewhere else, delete it in the console and create a new one — it takes one minute."),
])
build("studio-setup-github", "Set up · GitHub", "Modes A and B need a GitHub key that can write to Nova's repository.", [
    ("h", "Create the key (fine-grained token)"),
    ("n", ["github.com → your picture → Settings → Developer settings → Personal access tokens → Fine-grained tokens → Generate new token.", "Name: Nova Studio · Expiration: 90 days.", "Repository access: Only select repositories → thescorpio007/nova-enhance.", "Permissions → Repository: <b>Contents: Read and write</b>, <b>Actions: Read-only</b> (Metadata is added by itself).", "Generate token → copy it once.", "In Nova Studio → ② Set up → GitHub: paste → Test. You should see: your login · the repository · can push."]),
    ("h", "Good to know"),
    ("b", ["Your GitHub password and 2FA are never typed into Nova — only this key.", "When the key expires, Nova shows ⚠ and you repeat these steps.", "Sign out deletes the key from this PC."]),
])
build("studio-setup-build", "Set up · build check", "Proves that GitHub can build, test and sign Nova.", [
    ("p", "Press <b>Run again</b>. Nova reads the build workflow and the last build on GitHub."),
    ("b", ["✓ workflows found and the last build passed: ready.", "⚠ a signing secret is missing: GitHub → the repository → Settings → Secrets and variables → Actions → add the secret named in Nova's message.", "⚠ the last build failed: open the Studio chat, press Add today's errors and write “the last build failed — fix it”."]),
    ("tip", "Every change from the Studio goes through the same build and tests as before. A red build is never offered as an update."),
])
build("studio-setup-tools", "Set up · tools and the coding agent (mode B)", "Installed for you with Windows' own installer.", [
    ("h", "The tools"),
    ("t", [["Tool", "Why"], ["Git", "keeps the agent's own copy of Nova's source and sends the change"], [".NET SDK 8", "builds Nova on this PC before anything is sent"], ["Node.js", "runs the coding agent"], ["Coding agent (Claude Code)", "reads, edits and builds the code like a developer"]]),
    ("h", "Steps"),
    ("n", ["Press Install next to each missing tool and accept Windows' prompt.", "Restart Nova once after the installs.", "Press Connect for the coding agent: it uses your Claude key.", "The agent works only in Documents\\Nova Enhance\\Studio\\source — never in your project folders."]),
    ("tip", "If an install fails, install the tool from its website and press Check again."),
])
build("studio-how-to-ask", "How to ask for a feature", "Clear wishes give the best changes, first time.", [
    ("h", "The recipe"),
    ("n", ["<b>What</b>: the result you want, in one sentence.", "<b>Where</b>: the window and the place (e.g. Submittal log, the line under the filters).", "<b>Example</b>: a document number, a file, a screenshot.", "<b>Done when</b>: how you will check it works."]),
    ("h", "Good examples"),
    ("b", ["“Submittal log: when opened for a number, show ‘Showing: number — title’ above the table. Done when SLV-ZUC-AR-SSM-00183 shows its row.”", "“Find related: add a Copy timeline button that copies the dates and codes as a table.”", "“Mode C: a rule — RFI answers from the Engineer govern their item, newest first.”"]),
    ("h", "Before GO"),
    ("b", ["Read the summary: does it match your wish?", "Look at the files named: no surprise files.", "Mode B: “Built here ✓” must be shown."]),
])
build("studio-safety", "Safety rules of the Studio", "What the Studio AI may see, what needs your OK, and what never leaves this PC.", [
    ("t", [["", "What"], ["Free (read-only)", "Nova's source code · error log · freezes · crashes · performance · jobs · settings without secrets · index counts · folder structure · add-ins"],
           ["Your OK each time", "sending a document, an email or a screenshot to the AI · installing a change"],
           ["Never", "passwords · keys · tokens · admin PIN · VPN password · OTP · private (locked) files · email content you did not attach"]]),
    ("h", "Why"),
    ("b", ["Whatever you attach is sent to the AI company — attach only what the wish needs.", "Emails and PDFs come from outside parties: the Studio never reads them by itself, so a sentence hidden in an email can never steer a change.", "Every change is built and tested before it can be installed, and one click brings the previous build back."]),
])
print("ok")
