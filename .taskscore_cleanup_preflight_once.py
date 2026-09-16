from pathlib import Path
p = Path('.taskscore_cleanup_once.py')
s = p.read_text()
start = s.index("s = re.sub(r'        // Net-new-yield weight:")
end = s.index('config.write_text(s)', start)
s = s[:start] + '''# Preserve every currently live gameplay constant. Only obsolete comments may go.
s = re.sub(r'(?m)^        //.*(?:economyBaseSpacingValue|BaseNetNewYield|destroys real, currently-collected income|legacy weighted|old 60-point extraction).*\\n', '', s)
''' + s[end:]
needle = 'for term in retired:\n'
if s.count(needle) != 1:
    raise RuntimeError('Missing retired-symbol sweep')
s = s.replace(needle, '''# Remove obsolete documentation comments left in C# sources; keep any actual code
# reference visible to the strict check below.
for p in ROOT.rglob('*.cs'):
    content = p.read_text()
    lines = content.splitlines(keepends=True)
    kept = [line for line in lines if not
        (line.lstrip().startswith('//') and any(
            re.search(r'\\b' + re.escape(term) + r'\\b', line) for term in retired))]
    if len(kept) != len(lines):
        p.write_text(''.join(kept))
''' + needle, 1)
p.write_text(s)
print('PRECHECK: preserved active constants; obsolete source comments excluded from sweep')
