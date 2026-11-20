#!/usr/bin/env python3
"""Maps the GC (sgen) functions of a stripped libmonosgen-2.0.so to address ranges.

The Mono runtime pack ships libmonosgen-2.0.so stripped (only exported symbols) next to libmonosgen-2.0.a, whose
object files keep their symbols and were compiled from the same sources in the same build. Every function of the
.so has an .eh_frame FDE (start, end); each one is matched to the archive function whose instruction mnemonics agree
best (relocated operands differ, mnemonics do not). Only sgen-*.c.o functions are emitted: that table lets the app
name GC frames in a native crash tombstone without the .dbg file.

usage: monosgen-symbols.py <runtime-pack native dir> > table.txt   (needs llvm-objdump, llvm-dwarfdump, readelf, ar)
output: "<buildid>" then lines "<start hex> <end hex> <function> <object>"
"""
import glob, os, re, subprocess, sys, tempfile

native = sys.argv[1]
so = os.path.join(native, 'libmonosgen-2.0.so')
archive = os.path.join(native, 'libmonosgen-2.0.a')
build_id = re.search(r'Build ID: ([0-9a-f]+)', subprocess.run(['readelf', '-n', so], capture_output=True, text=True).stdout).group(1)

def mnemonics(lines):
    # (address, instruction with registers kept and addresses, immediates and symbol names dropped)
    out = []
    for line in lines:
        m = re.match(r'^\s*([0-9a-f]+):\s+(\S+)\s*([^<]*)', line)
        if m:
            ops = re.sub(r'#?-?(0x[0-9a-f]+|\d+)\b', '#', m.group(3).split('//')[0]).replace(' ', '')
            out.append((int(m.group(1), 16), m.group(2) + ' ' + ops))
    return out

work = tempfile.mkdtemp()
subprocess.run(['ar', 'x', archive], cwd=work, check=True)
funcs = []  # (object, name, [mnemonic])
for obj in sorted(glob.glob(os.path.join(work, 'sgen-*.o'))):
    text = subprocess.run(['llvm-objdump', '-d', '--no-show-raw-insn', obj], capture_output=True, text=True).stdout
    name, body = None, []
    for line in text.splitlines() + ['0 <$end>:']:
        m = re.match(r'^[0-9a-f]+ <(.+)>:$', line)
        if m:
            if name and len(body) > 11 and not name.startswith(('$', '.')):
                funcs.append((os.path.basename(obj)[:-2], name, [x for _, x in mnemonics(body)]))
            name, body = m.group(1), []
        else:
            body.append(line)

fdes = []
dump = subprocess.run(['llvm-dwarfdump', '--eh-frame', so], capture_output=True, text=True).stdout
for m in re.finditer(r'pc=([0-9a-f]+)\.\.\.([0-9a-f]+)', dump):
    fdes.append((int(m.group(1), 16), int(m.group(2), 16)))
fdes.sort()

# Disassemble the whole .so once and slice it per FDE.
insns = mnemonics(subprocess.run(['llvm-objdump', '-d', '--no-show-raw-insn', so], capture_output=True, text=True).stdout.splitlines())
by_addr = {a: i for i, (a, _) in enumerate(insns)}
by_len = {}
for f in funcs:
    by_len.setdefault(len(f[2]), []).append(f)

print(build_id)
for start, end in fdes:
    count = (end - start) // 4
    i = by_addr.get(start)
    if i is None or count < 12:
        continue
    seq = [x for _, x in insns[i:i + count]]
    seq_m = [x.split(' ')[0] for x in seq]
    best, second = None, 0.0
    for n in range(int(count * 0.9), int(count * 1.1) + 2):
        for obj, name, body in by_len.get(n, []):
            longest = max(len(seq), len(body))
            mnem = sum(1 for a, b in zip(seq_m, body) if a == b.split(' ')[0]) / longest
            if mnem < 0.9:
                continue
            full = sum(1 for a, b in zip(seq, body) if a == b) / longest
            score = mnem + full / 2  # mnemonics decide, operands (registers) break ties
            if best is None or score > best[0]:
                second = best[0] if best else 0.0
                best = (score, obj, name)
            elif score > second:
                second = score
    if best and best[0] - second > 0.005:
        print(f'{start:x} {end:x} {best[2]} {best[1]}')
