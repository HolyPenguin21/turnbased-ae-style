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
            for arg,slot in camel.items():
                if re.search(rf"\b{re.escape(arg)}\s*:", line):
                    targets.append(slot)
            for cm in CALL_RE.finditer(line):
                c = cm.group(1)
                if c in by_name: targets.append(c)
                elif c in ("PositiveStaleness","StaleIntelPenalty"): targets.append("Staleness")
                elif c == "DeliveryFromEta": targets.append("Delivery")
                elif c in ("WithResponse","WithActorResponse"):
                    targets += ["WinChance","CardPrice","Delivery","MoverOpportunityCost"]
            direct_refs = sorted(set(re.findall(r"AiConfigV2\.(\w+)", line)))
            for slot in set(targets):
                key=(slot,i+1)
                if key in seen: continue
                seen.add(key)
                by_name[slot]["usages"].append({"file":rel,"line":i+1,"method":method,
                    "axis":axis,"task":task,"excerpt":line.strip()[:180],
                    "configRefs":direct_refs})

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
