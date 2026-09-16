from pathlib import Path
p = Path('.taskscore_cleanup_once.py')
s = p.read_text()
start = s.index("s = re.sub(r'        // Net-new-yield weight:")
end = s.index('config.write_text(s)', start)
s = s[:start] + '''# Retain every live game-policy constant; strip only obsolete commentary.
s = re.sub(r'(?m)^        //.*(?:economyBaseSpacingValue|BaseNetNewYield|destroys real, currently-collected income|legacy weighted|old 60-point extraction).*\\n', '', s)
''' + s[end:]
p.write_text(s)
print('PRECHECK: preserved live config constants and removed overly broad regex')
