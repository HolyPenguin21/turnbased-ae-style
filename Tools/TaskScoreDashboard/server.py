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

PROPERTY_CATEGORY = {
    "EconomicHexBenefit": "Economy",
    "Payback": "Economy",
    "Airfield": "Economy",
    "GlobalCardEffect": "Economy",
    "EconomicExpansionValue": "Economy",
    "InfoGain": "Recon",
    "Staleness": "Recon",
    "ContactRelevance": "Recon",
    "StrategicRelevance": "Positioning",
    "ThreatDirection": "Positioning",
    "FrontProgress": "Positioning",
    "CorridorAlignment": "Positioning",
    "OwnTerritoryProximity": "Positioning",
    "TerrainDefense": "Positioning",
    "MilitaryTargetRelevance": "Combat",
    "WinChance": "Combat",
    "CardPrice": "Cost",
    "Delivery": "Cost",
    "MoverOpportunityCost": "Cost",
    "HexThreatRisk": "Risk",
    "CitadelThreatRisk": "Risk",
    "BaseThreatRisk": "Risk",
    "DetectionRisk": "Risk",
}

def property_category(name):
    return PROPERTY_CATEGORY.get(name, "Other")

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
        ("Recon","Refresh",("refresh",)), ("Recon","Surveil",("surveil",)),
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

def method_fragment(text, name):
    m = re.search(rf"(?:internal|public|private)\s+static\s+float\s+{re.escape(name)}\s*\(", text)
    if not m: return ""
    tail = text[m.start():m.start()+1800]
    semi = tail.find(";")
    brace = tail.find("{")
    if "=>" in tail[:max(0, brace) if brace >= 0 else len(tail)] and semi >= 0:
        return tail[:semi+1]
    return tail

def payload():
    score = read(SCORE)
    config = read(CONFIG)

    fold_m = re.search(r"static\s+float\s+Fold\s*\(TaskScore\s+score\)\s*=>", score)
    fold = score[fold_m.end():score.find(";", fold_m.end())] if fold_m else ""
    signs = {}
    for m in re.finditer(r"([+-]?)\s*score\.(\w+)", fold):
        signs[m.group(2)] = "-" if m.group(1) == "-" else "+"

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
        if name == "Staleness": conv_names = ["PositiveStaleness","StaleIntelPenalty"]
        if name == "Delivery": conv_names = ["DeliveryFromEta"]
        converters, refs = [], set()
        for c in conv_names:
            frag = method_fragment(score,c)
            if frag:
                converters.append(c)
                refs |= set(re.findall(r"AiConfigV2\.(\w+)", frag))
        converter_params = [param_map[r] for r in sorted(refs) if r in param_map]
        cats.append({"name":name,"property":name,"category":property_category(name),
            "sign":signs.get(name,"?"),"converters":converters,
            "parameters":list(converter_params), "converterParameters":list(converter_params),
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
                elif c in ("PositiveStaleness","StaleIntelPenalty"): mapped = ["Staleness"]
                elif c == "DeliveryFromEta": mapped = ["Delivery"]
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
    # AiConfigV2 constant (RaidReward -> MilitaryTargetRelevance is the key example).
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
    for p in params:
        if not p["references"]: warnings.append("Unused config parameter: "+p["name"])

    return {"generatedAt":datetime.now(timezone.utc).isoformat(),
        "repository":{"branch":git("branch","--show-current"),"head":git("rev-parse","HEAD"),
                      "dirty":bool(git("status","--porcelain"))},
        "categories":cats,"parameters":params,"warnings":warnings,
        "propertyCategories":["Economy","Recon","Positioning","Combat","Cost","Risk","Other"],
        "axes":sorted({a for c in cats for a in c["axes"]}),
        "tasks":sorted({t for c in cats for t in c["tasks"] if t})}

class Handler(SimpleHTTPRequestHandler):
    def __init__(self,*args,**kwargs):
        super().__init__(*args,directory=str(HERE),**kwargs)
    def end_headers(self):
        self.send_header("Cache-Control","no-store")
        super().end_headers()
    def do_GET(self):
        if self.path.split("?",1)[0] == "/api/task-score":
            try:
                body=json.dumps(payload(),ensure_ascii=False).encode()
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
