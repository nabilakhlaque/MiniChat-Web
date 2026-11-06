# Genererar ding.wav och knock.wav (körs av GitHub Actions). prutt.wav och skratt.wav är egna inspelningar
# och laddas upp separat till mappen sounds/.
import math, os, random, struct, wave
rate = 22050
os.makedirs("sounds", exist_ok=True)

def write(path, samples):
    with wave.open(path, "wb") as w:
        w.setnchannels(1); w.setsampwidth(2); w.setframerate(rate)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1, min(1, s)) * 32000)) for s in samples))

def tone(freq, dur, vol):
    n = int(rate * dur)
    for i in range(n):
        t = i / rate
        env = math.exp(-6 * t / dur) * min(1.0, i / (rate * 0.004))
        yield vol * env * (math.sin(2 * math.pi * freq * t) + 0.25 * math.sin(2 * math.pi * freq * 2 * t))
if not os.path.exists("sounds/ding.wav"):
    write("sounds/ding.wav", list(tone(880, 0.13, 0.55)) + list(tone(1174.66, 0.22, 0.5)))

def knock(dur=0.09, vol=0.9):
    n = int(rate * dur); out = []; random.seed(7)
    for i in range(n):
        t = i / rate; env = math.exp(-28 * t)
        s = 0.55 * math.sin(2 * math.pi * 180 * t * (1 - 0.3 * t)) + 0.25 * math.sin(2 * math.pi * 95 * t) + 0.2 * (random.random() * 2 - 1) * math.exp(-80 * t)
        out.append(vol * env * s)
    return out
silence = lambda d: [0.0] * int(rate * d)
if not os.path.exists("sounds/knock.wav"):
    write("sounds/knock.wav", knock() + silence(0.11) + knock() + silence(0.25) + knock(0.08, 0.7))
print("ljud klara")
