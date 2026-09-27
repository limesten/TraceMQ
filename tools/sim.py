#!/usr/bin/env python3
"""Plays a believable assembly line into MQTT, in real time.

Five stations per part, each part keyed by a GUID in trigger.uid, parts pipelined so
their sequences interleave; a PLC heartbeat that carries no key; one vision reject;
and one line stop longer than a minute near the end, which the Delta column flags.

Real time because the API stamps messages on receipt: publishing faster would squash
every delta. A run takes about three minutes. Needs mosquitto_pub on the PATH.

    python3 tools/sim.py                          # codeit/sim/line2/..., for make run
    python3 tools/sim.py --root factory/line2     # anything else; see make demo
"""
import argparse
import json
import random
import subprocess
import time
import uuid
from datetime import datetime, timezone

ROOT = "codeit/sim/line2"
PART_EVERY = 2.6          # seconds between parts entering the line
PARTS_BEFORE_STOP = 38
PARTS_AFTER_STOP = 4
STOP_SECONDS = 68
REJECT_AT = 29            # index of the part that fails vision

RECIPE = {"id": "HX-220", "revision": 14, "variant": "left-hand"}


def iso(t: float) -> str:
    return datetime.fromtimestamp(t, timezone.utc).isoformat(timespec="milliseconds").replace("+00:00", "Z")


def scan(p, t):
    return {
        "trigger": {"uid": p["uid"], "source": "infeed", "at": iso(t)},
        "serial": p["serial"],
        "barcode": {"symbology": "DataMatrix", "grade": random.choice("AAAB"), "readMs": random.randint(18, 41)},
        "recipe": RECIPE,
        "carrier": f"C-{p['carrier']:03d}",
        "operator": "shift-B",
    }


def press(p, t):
    force = round(random.gauss(18.4, 0.35), 2)
    return {
        "trigger": {"uid": p["uid"], "station": "ST10", "at": iso(t)},
        "serial": p["serial"],
        "result": "OK",
        "press": {
            "peakForceKn": force,
            "finalPositionMm": round(random.gauss(42.10, 0.02), 3),
            "forceWindowKn": {"min": 17.0, "max": 19.8},
            "cycleMs": random.randint(1410, 1560),
        },
        "toolStrokes": 184_220 + p["index"],
    }


def vision(p, t):
    ok = p["index"] != REJECT_AT
    return {
        "trigger": {"uid": p["uid"], "station": "ST20", "at": iso(t)},
        "serial": p["serial"],
        "result": "OK" if ok else "NOK",
        "camera": "CAM-2",
        "scores": {
            "clipPresent": round(random.uniform(0.97, 0.998), 3),
            "sealContinuity": round(random.uniform(0.95, 0.99) if ok else 0.612, 3),
            "labelOffsetMm": round(random.gauss(0.12, 0.05), 2),
        },
        "threshold": 0.9,
        "failed": [] if ok else ["sealContinuity"],
        "inspectMs": random.randint(96, 140),
        "imageRef": f"img/{p['serial']}/st20.png",
    }


def torque(p, t):
    return {
        "trigger": {"uid": p["uid"], "station": "ST30", "at": iso(t)},
        "serial": p["serial"],
        "result": "OK",
        "spindle": "SP-2",
        "torqueNm": {f"s{n}": round(random.gauss(4.5, 0.06), 2) for n in (1, 2, 3, 4)},
        "limitsNm": {"min": 4.2, "max": 4.8},
        "rundownMs": random.randint(2100, 2600),
    }


def label(p, t):
    return {
        "trigger": {"uid": p["uid"], "station": "ST40", "at": iso(t)},
        "serial": p["serial"],
        "result": "OK",
        "printer": {"id": "ZT-1", "template": "HX-220-L", "dpi": 300},
        "verify": {"grade": "A", "decoded": p["serial"]},
        "totalCycleMs": int((t - p["start"]) * 1000),
    }


def reject(p, t):
    return {
        "trigger": {"uid": p["uid"], "station": "ST45", "at": iso(t)},
        "serial": p["serial"],
        "result": "REJECTED",
        "reason": {"station": "ST20", "check": "seal-continuity", "score": 0.612},
        "bin": "R-2",
        "operatorAck": False,
    }


STEPS = [
    (0.0, "infeed/scanner/read", scan),
    (1.1, "st10/press/result", press),
    (2.9, "st20/vision/inspect", vision),
    (4.6, "st30/torque/result", torque),
    (6.2, "st40/label/print", label),
]


def events():
    out = []
    t = 0.0
    index = 0

    def part(start):
        nonlocal index
        index += 1
        p = {
            "uid": str(uuid.uuid4()),
            "serial": f"HX220-{26_09_27_000 + 4180 + index}",
            "carrier": random.randint(1, 60),
            "index": index,
            "start": start,
        }
        for offset, topic, build in STEPS:
            at = start + offset + random.uniform(-0.15, 0.25)
            if build is label and p["index"] == REJECT_AT:
                out.append((at, "outfeed/reject/sort", reject, p))
            else:
                out.append((at, topic, build, p))

    for _ in range(PARTS_BEFORE_STOP):
        part(t)
        t += PART_EVERY + random.uniform(-0.3, 0.4)

    last_running = t + 6.5
    out.append((last_running, "plc/state", "stop", None))
    resume = last_running + STOP_SECONDS
    out.append((resume, "plc/state", "run", None))
    t = resume + 0.6
    for _ in range(PARTS_AFTER_STOP):
        part(t)
        t += PART_EVERY + random.uniform(-0.3, 0.4)

    # Heartbeat every 5 s while running; silent during the stop, which is the gap.
    hb = 0.0
    while hb < t + 6:
        if not (last_running < hb < resume):
            out.append((hb, "plc/heartbeat", "hb", None))
        hb += 5.0

    return sorted(out, key=lambda e: e[0])


def payload(kind, p, wall):
    if kind == "hb":
        return {"plc": "PLC-L2", "state": "RUN", "cycle": int(wall) % 100000, "uptimeS": 86_412 + int(wall) % 10_000}
    if kind == "stop":
        return {"plc": "PLC-L2", "state": "STOPPED", "reason": "E-stop ST30 guard door", "at": iso(wall)}
    if kind == "run":
        return {"plc": "PLC-L2", "state": "RUN", "reason": "reset by operator", "at": iso(wall)}
    return kind(p, wall)


def main():
    global ROOT
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=ROOT, help=f"topic prefix (default {ROOT})")
    parser.add_argument("--host", default="localhost")
    parser.add_argument("--port", type=int, default=1883)
    parser.add_argument("--seed", type=int, default=7, help="same seed, same line; the GUIDs differ every run")
    args = parser.parse_args()
    ROOT = args.root.rstrip("/")
    random.seed(args.seed)

    plan = events()
    start = time.time()
    print(f"{len(plan)} messages to {ROOT}/... over ~{plan[-1][0]:.0f}s", flush=True)
    for at, topic, kind, p in plan:
        delay = start + at - time.time()
        if delay > 0:
            time.sleep(delay)
        wall = time.time()
        # The scan is a part's first message; its wall time anchors the total cycle.
        if p is not None and "wallStart" not in p:
            p["wallStart"] = wall
        body = payload(kind, {**p, "start": p["wallStart"]} if p else None, wall)
        subprocess.run(
            ["mosquitto_pub", "-h", args.host, "-p", str(args.port), "-t", f"{ROOT}/{topic}", "-m", json.dumps(body)],
            check=True,
        )
    print("done", flush=True)


if __name__ == "__main__":
    main()
