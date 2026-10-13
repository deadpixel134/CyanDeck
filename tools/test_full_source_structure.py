#!/usr/bin/env python3
from pathlib import Path
import re, sys, json
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
SRC = ROOT / 'src' / 'CyanDeck'
fail=[]; passed=0

def check(cond,msg):
    global passed
    if cond:
        passed+=1
        print('PASS:',msg)
    else:
        fail.append(msg); print('FAIL:',msg)

# XML/XAML
xamls=sorted(SRC.rglob('*.xaml'))
check(len(xamls)>=5, f'XAML file count {len(xamls)}')
for p in xamls:
    try: ET.fromstring(p.read_text(encoding='utf-8-sig')); ok=True
    except Exception as e: ok=False; fail.append(f'{p.relative_to(ROOT)} XML: {e}')
    if ok: passed+=1; print('PASS:',p.relative_to(ROOT),'XML parse')

# Event handler existence in code-behind.
events=['Click','Checked','Unchecked','SelectionChanged','ValueChanged','TextChanged','KeyDown','KeyUp','MouseDown','MouseUp','MouseMove','MouseEnter','MouseLeave','MouseDoubleClick','PreviewMouseDown','PreviewMouseUp','PreviewMouseMove','PreviewMouseWheel','PreviewKeyDown','PreviewMouseRightButtonUp','Drop','DragEnter','DragOver','DragLeave','Loaded','Unloaded','Closing','Closed','Activated','Deactivated','SizeChanged']
for xaml in xamls:
    cs=Path(str(xaml)+'.cs')
    if not cs.exists(): continue
    xt=xaml.read_text(encoding='utf-8-sig'); ct=cs.read_text(encoding='utf-8-sig')
    for ev in events:
        for m in re.finditer(rf'\b{ev}\s*=\s*"([A-Za-z_][A-Za-z0-9_]*)"',xt):
            h=m.group(1)
            check(re.search(rf'\b{re.escape(h)}\s*\(',ct) is not None, f'{xaml.relative_to(ROOT)} {ev}={h}')

# Lexical delimiter validation for C# source.
def lexical_balanced(text):
    stack=[]; i=0; state='code'; verbatim=False
    pairs={'}':'{',')':'(',']':'['}
    while i < len(text):
        c=text[i]; n=text[i+1] if i+1<len(text) else ''
        if state=='code':
            if c=='/' and n=='/': state='line'; i+=2; continue
            if c=='/' and n=='*': state='block'; i+=2; continue
            if c=='@' and n=='"': state='vstr'; i+=2; continue
            if c=='$' and n=='@' and i+2<len(text) and text[i+2]=='"': state='vstr'; i+=3; continue
            if c=='@' and n=='$' and i+2<len(text) and text[i+2]=='"': state='vstr'; i+=3; continue
            if c=='"': state='str'; i+=1; continue
            if c=="'": state='char'; i+=1; continue
            if c in '{([': stack.append(c)
            elif c in '})]':
                if not stack or stack.pop()!=pairs[c]: return False, f'mismatched {c} at {i}'
            i+=1; continue
        if state=='line':
            if c=='\n': state='code'
            i+=1; continue
        if state=='block':
            if c=='*' and n=='/': state='code'; i+=2
            else: i+=1
            continue
        if state=='str':
            if c=='\\': i+=2; continue
            if c=='"': state='code'
            i+=1; continue
        if state=='char':
            if c=='\\': i+=2; continue
            if c=="'": state='code'
            i+=1; continue
        if state=='vstr':
            if c=='"' and n=='"': i+=2; continue
            if c=='"': state='code'
            i+=1; continue
    return (state in ('code','line') and not stack), f'state={state} stack={stack[-8:]}'

csfiles=sorted(SRC.rglob('*.cs'))
check(len(csfiles)>=170, f'C# file count {len(csfiles)}')
for p in csfiles:
    ok,why=lexical_balanced(p.read_text(encoding='utf-8-sig',errors='strict'))
    check(ok, f'{p.relative_to(ROOT)} lexical balance' + ('' if ok else f' ({why})'))

# Duplicate top-level type names within same namespace (rough but useful).
types={}
for p in csfiles:
    t=p.read_text(encoding='utf-8-sig')
    ns_m=re.search(r'\bnamespace\s+([A-Za-z_][A-Za-z0-9_.]*)\s*;',t)
    ns=ns_m.group(1) if ns_m else ''
    for m in re.finditer(r'(?m)^(?:public|internal|static|sealed|abstract|partial|readonly|unsafe|\s)*\b(?:class|struct|record(?:\s+struct)?|enum|interface)\s+([A-Za-z_][A-Za-z0-9_]*)',t):
        key=(ns,m.group(1)); types.setdefault(key,[]).append(p)
dups={k:v for k,v in types.items() if len(v)>1 and not any('partial' in p.read_text(encoding='utf-8-sig')[:max(0,p.read_text(encoding='utf-8-sig').find(k[1])+20)] for p in v)}
check(not dups, f'no duplicate top-level types ({len(dups)})')
if dups:
    for k,v in list(dups.items())[:20]: print(' DUP',k,[str(x.relative_to(ROOT)) for x in v])

# Version/schema consistency.
csproj=(SRC/'CyanDeck.csproj').read_text(encoding='utf-8')
models=(SRC/'Models/Models.cs').read_text(encoding='utf-8')
schema=(SRC/'Services/StateSchema.cs').read_text(encoding='utf-8')
package=(ROOT/'package.ps1').read_text(encoding='utf-8')
manifest=(ROOT/'update-manifest.example.json').read_text(encoding='utf-8')
project_version = ET.fromstring(csproj).findtext('.//Version')
schema_match = re.search(r'public\s+const\s+int\s+Current\s*=\s*(\d+)', schema)
actual_schema = int(schema_match.group(1)) if schema_match else None
example_manifest = json.loads(manifest)
check(bool(project_version and re.fullmatch(r'\d+\.\d+\.\d+', project_version)),
      f'csproj semantic version {project_version}')
check(actual_schema == 35,'current state schema 35 preserved')
check(f'public int Version {{ get; set; }} = {actual_schema};' in models,
      'DeckState default matches StateSchema.Current')
check('$projectXml.Project.PropertyGroup.Version' in package and '$schemaMatch.Groups[1].Value' in package,
      'package derives version and schema from source')
check(example_manifest.get('version') == project_version and example_manifest.get('schema') == actual_schema,
      'example manifest version/schema matches source')

# Artifact hygiene.
bad=[p for p in ROOT.rglob('*') if p.is_file() and (p.suffix in {'.pyc','.pyo'} or '__pycache__' in p.parts)]
check(not bad,'no Python bytecode/cache in source tree')

print(f'RESULT {passed}/{passed+len(fail)} PASS')
if fail:
    print('FAILURES:')
    for x in fail: print('-',x)
    sys.exit(1)
