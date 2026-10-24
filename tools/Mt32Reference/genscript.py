"""Writes a seeded MIDI event script that exercises the MT-32 emulator broadly.

    python genscript.py <seed> <seconds> <out.txt>
"""
import random
import sys

RATE = 32000
seed, seconds, out = int(sys.argv[1]), float(sys.argv[2]), sys.argv[3]
rnd = random.Random(seed)


def sysex(addr, data):
    body = [(addr >> 16) & 0x7F, (addr >> 8) & 0x7F, addr & 0x7F] + data
    chk = (-sum(body)) & 0x7F
    return [0xF0, 0x41, 0x10, 0x16, 0x12] + body + [chk, 0xF7]


def msg(status, d1=0, d2=0):
    return status | (d1 << 8) | (d2 << 16)


events = []
end = int(seconds * RATE)

# setup: a few memory timbres from random data, patches pointing at them, rhythm keys, reverb
for t in range(6):
    data = [rnd.randrange(0, 128) for _ in range(246)]
    for i in range(10):
        data[i] = rnd.randrange(0x41, 0x5B)
    data[10] = rnd.randrange(0, 13)  # partial structure 1&2
    data[11] = rnd.randrange(0, 13)  # partial structure 3&4
    data[12] = rnd.randrange(1, 16)  # partial mute
    # sysex addresses are in 7-bit units: timbre t starts at 0x080000 + t * 0x200 (sysex-padded)
    addr = 0x080000 + (t << 9)
    # split into two writes, like Sierra's drivers send 246 bytes
    events.append((rnd.randrange(0, 2000), 'S', sysex(addr, data[:128])))
    events.append((rnd.randrange(2000, 4000), 'S', sysex(addr + 0x100, data[128:])))

for p in range(16):
    patch = [2, rnd.randrange(0, 6), rnd.randrange(12, 37), rnd.randrange(30, 71), rnd.randrange(0, 25), rnd.randrange(0, 4), rnd.randrange(0, 2), 0]
    events.append((rnd.randrange(4000, 6000), 'S', sysex(0x050000 + p * 8, patch)))

events.append((6000, 'S', sysex(0x030110, sum(([rnd.randrange(0, 94), rnd.randrange(50, 101), rnd.randrange(0, 15), 1] for _ in range(20)), []))))
events.append((6500, 'S', sysex(0x200000, list(b"  Daventry  test  !!"))))

frame = 8000
held = {}
while frame < end:
    frame += rnd.randrange(1, 900)
    roll = rnd.random()
    chan = rnd.randrange(1, 10)
    if roll < 0.45:
        note = rnd.randrange(24, 109) if chan == 9 else rnd.randrange(30, 96)
        events.append((frame, 'M', msg(0x90 | chan, note, rnd.randrange(1, 128))))
        held.setdefault(chan, []).append(note)
    elif roll < 0.75 and held.get(chan):
        note = held[chan].pop(rnd.randrange(len(held[chan])))
        events.append((frame, 'M', msg(0x80 | chan, note, 64)))
    elif roll < 0.80:
        events.append((frame, 'M', msg(0xC0 | chan, rnd.randrange(0, 128))))
    elif roll < 0.88:
        cc = rnd.choice([1, 7, 10, 11, 64, 1, 7, 11])
        events.append((frame, 'M', msg(0xB0 | chan, cc, rnd.randrange(0, 128))))
    elif roll < 0.93:
        bend = rnd.randrange(0, 16384)
        events.append((frame, 'M', msg(0xE0 | chan, bend & 0x7F, bend >> 7)))
    elif roll < 0.95:
        # RPN 0: pitch bend range
        events.append((frame, 'M', msg(0xB0 | chan, 101, 0)))
        events.append((frame, 'M', msg(0xB0 | chan, 100, 0)))
        events.append((frame, 'M', msg(0xB0 | chan, 6, rnd.randrange(0, 25))))
    elif roll < 0.965:
        events.append((frame, 'S', sysex(0x100001, [rnd.randrange(0, 4), rnd.randrange(0, 8), rnd.randrange(0, 8)])))
    elif roll < 0.975:
        events.append((frame, 'M', msg(0xB0 | chan, rnd.choice([121, 123]), 0)))
    elif roll < 0.98:
        events.append((frame, 'S', sysex(0x100004, [rnd.randrange(0, 9) for _ in range(9)])))
    elif roll < 0.985:
        events.append((frame, 'S', sysex(0x100016, [rnd.randrange(40, 101)])))
    elif roll < 0.99:
        # patch temp of a part: timbre, key shift, fine tune, bender, assign mode
        part = rnd.randrange(0, 8)
        events.append((frame, 'S', sysex(0x030000 + part * 0x10, [rnd.randrange(0, 4), rnd.randrange(0, 64), rnd.randrange(0, 49), rnd.randrange(0, 101), rnd.randrange(0, 25), rnd.randrange(0, 4)])))
    elif roll < 0.993:
        events.append((frame, 'S', sysex(0x100000, [rnd.randrange(0, 128)])))

events.sort(key=lambda e: e[0])
with open(out, 'w') as f:
    for frame, kind, payload in events:
        if kind == 'M':
            f.write(f"{frame} M {payload:x}\n")
        else:
            f.write(f"{frame} S {''.join(f'{b:02x}' for b in payload)}\n")
    f.write(f"{end} E\n")
print(len(events), 'events,', end, 'frames')
