#!/usr/bin/env python3
import json, re, subprocess, threading, webbrowser
from datetime import datetime, timezone
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
AI = ROOT / "Assets/Scripts/Ai/V2"
SCORE = AI / "Evaluation/TaskScore.cs"
CONFIG = AI / "Foundation/AiConfigV2.TaskScore.cs"
LOG = ROOT / "Logs/AiDebug.log"
CALIBRATION = HERE / "calibration.json"   # written by Unity: AI > TaskScore > Calibration Report

SLOT_RE = re.compile(r"public\s+readonly\s+float\s+(\w+)\s*;")
CONST_RE = re.compile(r"public\s+const\s+(float|int|bool|string)\s+(\w+)\s*=\s*([^;]+);")
CALL_RE = re.compile(r"TaskScoreEvaluator\.(\w+)\s*\(")
METHOD_RE = re.compile(r"(?:public|private|internal|protected)\s+(?:static\s+)?(?:[\w<>,?.\[\]]+\s+)+(\w+)\s*\(")

def read(path):
    return path.read_text(encoding="utf-8-sig")

def line_no(text, pos):
    return text.count("\n", 0, pos) + 1

def literal(expr):
    x = expr.strip()
    if re.fullmatch(r"[-+]?\d+(?:\.\d+)?f", x, re.I):
        return float(x[:-1])
    if re.fullmatch(r"[-+]?\d+", x):
        return int(x)
    if x in ("true", "false"):
        return x == "true"
    return x.strip('"')

def git(*args):
    try:
        return subprocess.check_output(["git", "-C", str(ROOT), *args], text=True,
            stderr=subprocess.DEVNULL, timeout=2).strip() or None
    except Exception:
        return None

def nearest_method(lines, i):
    for j in range(i, max(-1, i - 80), -1):
        m = METHOD_RE.search(lines[j])
        if m and not lines[j].lstrip().startswith(("if", "for", "foreach", "while", "switch")):
            return m.group(1)
    return None

# The fold category (Benefit / Cost / Risk / Opportunity) is read from TaskScoreEvaluator.CategoryOf.
# Only Benefit slots are further grouped by the task family whose facts they carry — the same
# sections as the calibration table in AiConfigV2.TaskScore.cs.
BENEFIT_FAMILY = {
    "EconomicHexBenefit": "Economy", "Payback": "Economy", "Airfield": "Economy",
    "GlobalCardEffect": "Economy", "EconomicExpansionValue": "Economy",
    "InfoGain": "Recon", "Staleness": "Recon",
    "RaidReward": "Military", "EventReward": "Military", "WinChance": "Military", "AttackReadiness": "Military",
    "PreventedDamage": "Military",
    "ForceAmplification": "Development",
    "FrontProgress": "Positional", "CorridorAlignment": "Positional",
    "OwnTerritoryProximity": "Positional", "TerrainDefense": "Positional",
    # Shared by Recon, ActiveDefence and Attack: what the target is / where the enemy is.
    "StrategicRelevance": "Positional", "ThreatDirection": "Positional",
}
CATEGORY_ORDER = ["Benefit · Economy", "Benefit · Recon", "Benefit · Military",
    "Benefit · Development", "Benefit · Positional", "Benefit · Other",
    "Cost", "Risk", "Opportunity", "Other"]

def slot_categories(score):
    """slot -> Benefit/Cost/Risk/Opportunity, parsed from TaskScoreEvaluator.CategoryOf."""
    m = re.search(r"static\s+TaskSlotCategory\s+CategoryOf\s*\(TaskSlot\s+slot\)", score)
    if not m:
        return {}
    body = score[m.end():score.find("default:", m.end())]
    result, pending = {}, []
    for tok in re.finditer(r"case\s+TaskSlot\.(\w+)\s*:|return\s+TaskSlotCategory\.(\w+)\s*;", body):
        if tok.group(1):
            pending.append(tok.group(1))
        else:
            for s in pending:
                result[s] = tok.group(2)
            pending = []
    return result

def property_category(name, code_category):
    if code_category != "Benefit":
        return code_category
    return "Benefit · " + BENEFIT_FAMILY.get(name, "Other")

def axis_task(path, method):
    # Dashboard task columns are semantic task families, never arbitrary helper methods.
    # First use single-family evaluator ownership where the file itself proves the task.
    stem = path.stem.lower()
    if stem == "aggressionobjectiveevaluator":
        return "Aggression", "Raid"
    if stem == "activedefenceobjectiveevaluator":
        return "Aggression", "ActiveDefence"
    if stem == "attackobjectiveevaluator":
        return "Aggression", "Attack"

    h = (path.stem + " " + (method or "")).lower().replace("_", "")
    pairs = [
        ("Recon","AirSweep",("airsweep",)), ("Recon","Explore",("explore",)),
        ("Recon","Refresh",("refresh",)),
        ("Aggression","ActiveDefence",("activedefence",)),
        ("Aggression","Attack",("attack",)), ("Aggression","Raid",("raid",)),
        ("Economy","FoundBase",("foundbase","baseexpansion")),
        ("Economy","BuildExtraction",("buildextraction","extraction")),
        ("Economy","MobileCollection",("mobilecollection",)),
        ("Economy","CollectorCapability",("collectorcapability","collector")),
        ("Development","CardUpgrade",("cardupgrade",)),
        ("Development","Development",("developmentopportunity","development")),
    ]
    for axis, task, keys in pairs:
        if any(k in h for k in keys):
            return axis, task

    # Preserve the owning axis for diagnostics, but deliberately leave task unset.
    # An unset task can appear in source-usage detail, never as a matrix column.
    if "recon" in h: return "Recon", None
    if "econom" in h: return "Economy", None
    if "aggression" in h or "attack" in h or "defence" in h or "raid" in h:
        return "Aggression", None
    if "development" in h: return "Development", None
    if "production" in h: return "Production", None
    return "Other", None

def statement_from(lines, i, max_lines=5):
    parts = []
    for j in range(i, min(len(lines), i + max_lines)):
        parts.append(lines[j].strip())
        joined = " ".join(parts)
        if ";" in joined or ("," in joined and joined.count("(") <= joined.count(")")):
            break
    return " ".join(parts)

def calls_in(expr):
    calls = []
    for m in re.finditer(r"\b((?:[A-Za-z_]\w*\.)*[A-Za-z_]\w*)\s*\(", expr or ""):
        name = m.group(1)
        if name in ("if","for","foreach","while","switch","new"):
            continue
        if name.startswith("Mathf.") or name.startswith("Math.") or name.startswith("System.Math."):
            continue
        if name not in calls:
            calls.append(name)
    return calls

def nearest_assignment(lines, i, variable):
    pat = re.compile(rf"\b(?:var|float|double|int|bool|TaskScore|[A-Z]\w*(?:<[^>]+>)?)\s+{re.escape(variable)}\s*=")
    for j in range(i - 1, max(-1, i - 90), -1):
        if pat.search(lines[j]):
            return statement_from(lines, j)
    return ""

def component_sources(lines, i, arg_name, slot):
    stmt = statement_from(lines, i)
    m = re.search(rf"\b{re.escape(arg_name)}\s*:\s*(.+)", stmt)
    expr = m.group(1) if m else stmt
    # Trim the next named argument if statement_from crossed into it.
    expr = re.split(r",\s*[a-z]\w*\s*:", expr, maxsplit=1)[0].strip().rstrip(",);")
    direct_calls = calls_in(expr)
    if direct_calls:
        return direct_calls

    # A named argument often receives a precomputed local (Economy FoundBase is the main case).
    simple = re.fullmatch(r"([A-Za-z_]\w*)", expr)
    if simple:
        assign = nearest_assignment(lines, i, simple.group(1))
        resolved = calls_in(assign)
        if resolved:
            return [simple.group(1) + " ← " + x for x in resolved]
        if assign:
            rhs = assign.split("=", 1)[1].strip().rstrip(";")
            return [simple.group(1) + " ← " + rhs[:90]]

    # Preserve explicit score/property copies and direct config sources.
    member = re.fullmatch(r"([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)+)", expr)
    if member:
        return [member.group(1)]
    refs = re.findall(r"AiConfigV2\.(\w+)", expr)
    if refs:
        return ["AiConfigV2." + x for x in refs]
    if expr and expr not in ("0f","0","0.0f"):
        return [expr[:100]]
    return []

def top_level_additive(expr):
    """True if expr has a +/- at paren depth 0 that is NOT a unary sign — i.e. the expression is
    a SUM of parts, not one product/quotient chain. A constant can only be scaled by a simple
    ratio (newTerm = oldTerm * draft/current) when it multiplies the WHOLE expression; if the
    expression adds another part that does not involve the constant, that part would wrongly get
    rescaled too (or not rescaled at all) by a naive ratio."""
    depth = 0
    for i, ch in enumerate(expr):
        if ch in "([":
            depth += 1
        elif ch in ")]":
            depth -= 1
        elif ch in "+-" and depth == 0:
            j = i - 1
            while j >= 0 and expr[j] == " ":
                j -= 1
            prev = expr[j] if j >= 0 else ""
            if prev in ("", "(", ",", "*", "/", "+", "-", "="):
                continue  # unary sign, e.g. the "-1f" in a literal — not a binary split
            return True
    return False

def linear_params(formula, param_names):
    """Which of this converter's AiConfigV2 constants are a pure trailing multiplier (or bare
    pass-through) of the WHOLE return expression — the only shape where the Tuning tab's
    in-browser what-if preview can rescale a logged term exactly, with no other input needed.
    Conservative by construction: anything not provably of this shape (a denominator, a Clamp
    bound, one of several summed parts) is left out, so the preview only ever shows numbers it
    can prove, never a plausible-looking guess."""
    matches = list(re.finditer(r"(?:return\s+|=>\s*)([^;]+);", formula, re.S))
    if not matches:
        return []
    expr = matches[-1].group(1).strip()
    if top_level_additive(expr):
        return []
    out = []
    for name in param_names:
        occ = list(re.finditer(r"AiConfigV2\." + re.escape(name) + r"\b", expr))
        if len(occ) != 1:
            continue
        m = occ[0]
        bare = re.sub(r"\s+", "", expr) == "AiConfigV2." + name
        j = m.start() - 1
        while j >= 0 and expr[j] in " \t\r\n":
            j -= 1
        preceding = expr[j] if j >= 0 else ""
        if bare or preceding == "*":
            out.append(name)
    return out

def method_fragment(text, name):
    """The exact source of one method — no more, no less. Brace-bodied methods are extracted by
    counting braces to the matching close, not a flat character window: a flat window silently
    swallowed whatever method happened to follow in the file, which both corrupted the formula
    shown to the user and fed the wrong method's constants into linear_params."""
    m = re.search(rf"(?:internal|public|private)\s+static\s+float\s+{re.escape(name)}\s*\(", text)
    if not m: return ""
    paren_start = text.index("(", m.start())
    depth, i = 0, paren_start
    while i < len(text):
        if text[i] == "(": depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0: break
        i += 1
    j = i + 1
    while j < len(text) and text[j] in " \t\r\n": j += 1
    if text[j:j+2] == "=>":
        semi = text.find(";", j)
        return text[m.start():semi+1] if semi >= 0 else ""
    if j < len(text) and text[j] == "{":
        depth, k = 0, j
        while k < len(text):
            if text[k] == "{": depth += 1
            elif text[k] == "}":
                depth -= 1
                if depth == 0: break
            k += 1
        return text[m.start():k+1]
    return ""

def payload():
    score = read(SCORE)
    config = read(CONFIG)

    # Value = Benefit - Cost - Risk - Opportunity: the sign follows the slot's code category
    # (TaskScoreEvaluator.CategoryOf); a slot it does not list is a Benefit.
    code_cats = slot_categories(score)
    enum_block = score[score.find("enum TaskSlot"):score.find("}", score.find("enum TaskSlot"))]
    enum_slots = re.findall(r"^\s+(\w+),\s*$", enum_block, re.M)
    signs = {s: ("+" if code_cats.get(s, "Benefit") == "Benefit" else "-") for s in enum_slots}

    params = []
    param_map = {}
    for m in CONST_RE.finditer(config):
        p = {"type":m.group(1),"name":m.group(2),"value":literal(m.group(3)),
             "expression":m.group(3).strip(),"line":line_no(config,m.start()),
             "source":str(CONFIG.relative_to(ROOT)).replace("\\","/")}
        params.append(p); param_map[p["name"]] = p

    cats = []
    names = []
    for m in SLOT_RE.finditer(score):
        name = m.group(1); names.append(name)
        conv_names = [name]
        if name == "Staleness": conv_names = ["PositiveStaleness"]
        # Both execution-cost slots go through the one price converter (ActionPrice).
        if name in ("CardPrice", "Delivery"): conv_names = ["Price"]
        converters, refs, frags = [], set(), []
        for c in conv_names:
            frag = method_fragment(score,c)
            if frag:
                converters.append(c)
                refs |= set(re.findall(r"AiConfigV2\.(\w+)", frag))
                frags.append(frag.strip())
        converter_params = [param_map[r] for r in sorted(refs) if r in param_map]
        formula = "\n\n".join(frags)
        cats.append({"name":name,"property":name,
            "category":property_category(name, code_cats.get(name, "Benefit")),
            "sign":signs.get(name,"?"),"converters":converters,
            "parameters":list(converter_params), "converterParameters":list(converter_params),
            "formula":formula,
            "linearParams":linear_params(formula, [p["name"] for p in converter_params]),
            "source":str(SCORE.relative_to(ROOT)).replace("\\","/"),
            "line":line_no(score,m.start()),"usages":[]})

    by_name = {c["name"]:c for c in cats}
    camel = {n[0].lower()+n[1:]:n for n in names}

    for path in AI.rglob("*.cs"):
        if path == SCORE: continue
        text = read(path); lines = text.splitlines()
        rel = str(path.relative_to(ROOT)).replace("\\","/")
        seen = set()
        for i,line in enumerate(lines):
            method = nearest_method(lines,i)
            axis,task = axis_task(path,method)
            targets = []
            target_components = {}
            for arg,slot in camel.items():
                if re.search(rf"\b{re.escape(arg)}\s*:", line):
                    targets.append(slot)
                    target_components.setdefault(slot, []).extend(
                        component_sources(lines, i, arg, slot))
            for cm in CALL_RE.finditer(line):
                c = cm.group(1)
                mapped = []
                if c in by_name: mapped = [c]
                elif c == "PositiveStaleness": mapped = ["Staleness"]
                elif c in ("WithResponse","WithActorResponse"):
                    mapped = ["WinChance","CardPrice","Delivery","MoverOpportunityCost"]
                for slot in mapped:
                    targets.append(slot)
                    target_components.setdefault(slot, []).append("TaskScoreEvaluator." + c)
            stmt = statement_from(lines, i)
            direct_refs = sorted(set(re.findall(r"AiConfigV2\.(\w+)", stmt)))
            for slot in set(targets):
                key=(slot,i+1)
                if key in seen: continue
                seen.add(key)
                components = []
                for x in target_components.get(slot, []):
                    if x and x not in components:
                        components.append(x)
                by_name[slot]["usages"].append({"file":rel,"line":i+1,"method":method,
                    "axis":axis,"task":task,"excerpt":stmt[:220],
                    "configRefs":direct_refs,"components":components})

    # A category can be fed both through its canonical converter and directly from an
    # AiConfigV2 constant (the RaidReward converter reads AiConfigV2.RaidReward directly).
    # Show both, but only when the source scan proves the relationship.
    for c in cats:
        known = {p["name"] for p in c["parameters"]}
        direct = sorted({r for u in c["usages"] for r in u.get("configRefs",[]) if r in param_map})
        c["parameters"].extend(param_map[r] for r in direct if r not in known)
        c["parameters"].sort(key=lambda p: p["name"])

    all_text = "\n".join(read(p) for p in AI.rglob("*.cs"))
    for p in params:
        p["references"] = len(re.findall(rf"\bAiConfigV2\.{re.escape(p['name'])}\b", all_text))
        p["categories"] = [c["name"] for c in cats if any(q["name"]==p["name"] for q in c["parameters"])]

    warnings=[]
    for c in cats:
        c["usageCount"]=len(c["usages"])
        c["axes"]=sorted({u["axis"] for u in c["usages"]})
        c["tasks"]=sorted({u["task"] for u in c["usages"] if u.get("task")})
        c["components"]=sorted({x for u in c["usages"] for x in u.get("components", [])})
        if not c["usages"]: warnings.append("Unused slot: "+c["name"])
        if not c["converters"] and c["name"]!="MoverOpportunityCost":
            warnings.append("No dedicated converter: "+c["name"])
    # Blast radius: which task families / tasks actually reach each parameter, via the slots
    # (p["categories"]) that read it. "Shared" (>1 family) vs "local" (exactly 1 family) is the
    # distinction the Tuning tab leads with.
    for p in params:
        slots = [by_name[s] for s in p["categories"] if s in by_name]
        p["families"] = sorted({a for s in slots for a in s["axes"]})
        p["tasksUsing"] = sorted({t for s in slots for t in s["tasks"]})
        p["shared"] = len(p["families"]) > 1
    for p in params:
        if not p["references"]: warnings.append("Unused config parameter: "+p["name"])
    for c in cats:
        if c["category"] == "Benefit · Other":
            warnings.append("Benefit slot without a family in BENEFIT_FAMILY: "+c["name"])
    missing = sorted(set(enum_slots) - {c["name"] for c in cats})
    if missing: warnings.append("TaskSlot without a TaskScore field: "+", ".join(missing))

    return {"generatedAt":datetime.now(timezone.utc).isoformat(),
        "repository":{"branch":git("branch","--show-current"),"head":git("rev-parse","HEAD"),
                      "dirty":bool(git("status","--porcelain"))},
        "categories":cats,"parameters":params,"warnings":warnings,
        "propertyCategories":CATEGORY_ORDER,
        "axes":sorted({a for c in cats for a in c["axes"]}),
        "tasks":sorted({t for c in cats for t in c["tasks"] if t})}

LOG_RE = re.compile(
    r"\[AI\]\[V2\]\[TaskScore\] (?P<player>\S+) T(?P<turn>\d+) (?P<kind>\w+) (?P<key>.+?) "
    r"eff=(?P<eff>-?[\d.]+) \| value=(?P<value>-?[\d.]+)"
    r"(?: \| benefit (?P<benefit>-?[\d.]+)(?: \((?P<bterms>[^)]*)\))?"
    r" \| cost (?P<cost>-?[\d.]+)(?: \((?P<cterms>[^)]*)\))?"
    r" \| risk (?P<risk>-?[\d.]+)(?: \((?P<rterms>[^)]*)\))?"
    r" \| opportunity (?P<opp>-?[\d.]+))?")

def median(xs):
    xs = sorted(xs)
    if not xs:
        return None
    n = len(xs)
    return xs[n // 2] if n % 2 else (xs[n // 2 - 1] + xs[n // 2]) / 2

def terms(text):
    out = {}
    for part in (text or "").split(","):
        bits = part.strip().rsplit(" ", 1)
        if len(bits) == 2:
            try: out[bits[0]] = float(bits[1])
            except ValueError: pass
    return out

def log_scores():
    """Per task family distribution of the [AI][V2][TaskScore] lines of the current log."""
    if not LOG.exists():
        return {"log": str(LOG), "exists": False, "families": []}
    fams = {}
    restored = 0
    for line in LOG.read_text(encoding="utf-8", errors="replace").splitlines():
        m = LOG_RE.search(line)
        if not m:
            continue
        if m.group("benefit") is None:
            restored += 1
            continue
        f = fams.setdefault(m.group("kind"), {"values": [], "benefits": [], "costs": [],
            "risks": [], "eff": [], "terms": {}, "players": set(), "turns": set()})
        f["values"].append(float(m.group("value"))); f["eff"].append(float(m.group("eff")))
        f["benefits"].append(float(m.group("benefit"))); f["costs"].append(float(m.group("cost")))
        f["risks"].append(float(m.group("risk")))
        f["players"].add(m.group("player")); f["turns"].add(int(m.group("turn")))
        for group in ("bterms", "cterms", "rterms"):
            for name, v in terms(m.group(group)).items():
                f["terms"].setdefault(name, []).append(v)
    out = []
    for kind, f in sorted(fams.items()):
        out.append({"kind": kind, "count": len(f["values"]),
            "players": sorted(f["players"]), "turns": [min(f["turns"]), max(f["turns"])],
            "value": {"median": median(f["values"]), "max": max(f["values"]), "min": min(f["values"])},
            "effective": {"median": median(f["eff"])},
            "benefit": {"median": median(f["benefits"]), "max": max(f["benefits"])},
            "cost": {"median": median(f["costs"])}, "risk": {"median": median(f["risks"])},
            "terms": sorted(({"name": n, "median": median(v), "max": max(v), "count": len(v)}
                             for n, v in f["terms"].items()), key=lambda t: -abs(t["median"] or 0))})
    return {"log": str(LOG), "exists": True, "families": out, "restored": restored,
            "modified": datetime.fromtimestamp(LOG.stat().st_mtime, timezone.utc).isoformat()}

def calibration():
    if not CALIBRATION.exists():
        return {"exists": False, "hint": "Unity: AI > TaskScore > Calibration Report"}
    data = json.loads(read(CALIBRATION))
    data["exists"] = True
    return data

# Cap on how many individual scored proposals the Pipeline tab ships to the browser — most
# recent turns first, per family, so a long-running log still loads instantly.
PIPELINE_LIMIT_PER_FAMILY = 60

def task_instances():
    """Individual scored proposals (not family-aggregated) — the raw material for the Pipeline
    tab's Value -> RadarScale -> EffectiveValue cascade. RadarScale is never parsed from a
    separate radar log line: eff and value are both already on the same TaskScore log line, so
    RadarScale = eff / value is exact, not a reconstruction."""
    if not LOG.exists():
        return {"log": str(LOG), "exists": False, "instances": []}
    by_family = {}
    for line in LOG.read_text(encoding="utf-8", errors="replace").splitlines():
        m = LOG_RE.search(line)
        if not m or m.group("benefit") is None:
            continue
        value = float(m.group("value"))
        eff = float(m.group("eff"))
        term_map = {}
        for group in ("bterms", "cterms", "rterms"):
            term_map.update(terms(m.group(group)))
        inst = {
            "player": m.group("player"), "turn": int(m.group("turn")),
            "kind": m.group("kind"), "key": m.group("key"),
            "value": value, "effective": eff,
            "radarScale": (eff / value) if abs(value) > 1e-6 else None,
            "benefit": float(m.group("benefit")), "cost": float(m.group("cost")),
            "risk": float(m.group("risk")), "opportunity": float(m.group("opp") or 0.0),
            "terms": term_map,
        }
        by_family.setdefault(inst["kind"], []).append(inst)
    out = []
    for kind, insts in by_family.items():
        insts.sort(key=lambda x: -x["turn"])
        out.extend(insts[:PIPELINE_LIMIT_PER_FAMILY])
    out.sort(key=lambda x: (-x["turn"], x["kind"], x["key"]))
    return {"log": str(LOG), "exists": True, "instances": out,
            "modified": datetime.fromtimestamp(LOG.stat().st_mtime, timezone.utc).isoformat()}

class Handler(SimpleHTTPRequestHandler):
    def __init__(self,*args,**kwargs):
        super().__init__(*args,directory=str(HERE),**kwargs)
    def end_headers(self):
        self.send_header("Cache-Control","no-store")
        super().end_headers()
    def do_GET(self):
        route = {"/api/task-score": payload, "/api/log-scores": log_scores,
                 "/api/calibration": calibration,
                 "/api/task-instances": task_instances}.get(self.path.split("?",1)[0])
        if route:
            try:
                body=json.dumps(route(),ensure_ascii=False).encode()
                self.send_response(200)
            except Exception as e:
                body=json.dumps({"error":str(e)},ensure_ascii=False).encode()
                self.send_response(500)
            self.send_header("Content-Type","application/json; charset=utf-8")
            self.send_header("Content-Length",str(len(body))); self.end_headers()
            self.wfile.write(body); return
        super().do_GET()
    def log_message(self,fmt,*args):
        pass

if __name__=="__main__":
    server=ThreadingHTTPServer(("127.0.0.1",8765),Handler)
    url="http://127.0.0.1:8765/"
    print("TaskScore Inspector:",url)
    print("Read-only source:",ROOT)
    threading.Timer(.3,lambda:webbrowser.open(url)).start()
    try: server.serve_forever()
    except KeyboardInterrupt: pass
