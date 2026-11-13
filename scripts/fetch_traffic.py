#!/usr/bin/env python3
"""Snapshot aircraft, vessels and launch hazard areas for the 3D view.

None of these sources can be called from a browser (no cross-origin access, or a key that must
stay private), so the deploy workflow takes a snapshot each run:

  data/traffic.json  aircraft (ADS-B, adsb.lol) and vessels (AIS, aisstream.io) near active pads
  data/hazards.json  FAA space-operations TFRs (NOTAM areas) and NGA broadcast warnings (NOTMAR areas)

Vessels need a free aisstream.io key in the AISSTREAM_API_KEY environment variable and the
`websockets` package; without them the vessel list is empty and the file says why.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import math
import os
import re
import sys
import time
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone

UA = "Mozilla/5.0 (compatible; launch-countdown-bot)"
RADIUS_NM = 150          # aircraft search radius around each pad
BOX_DEG = 2.5            # half-size of the AIS box around each pad
AIS_SECONDS = 45         # how long to listen for vessel reports
HAZARD_KM = 2500         # keep hazard areas within this distance of a pad we follow
WORDS = re.compile(r"ROCKET|SPACE (?:DEBRIS|OPERATIONS|VEHICLE|LAUNCH)|MISSILE|HAZARDOUS OPERATIONS|SPLASHDOWN", re.I)
MONTHS = {m: i + 1 for i, m in enumerate("JAN FEB MAR APR MAY JUN JUL AUG SEP OCT NOV DEC".split())}


def log(*a):
    print(*a, file=sys.stderr, flush=True)


def get(url: str, timeout: int = 40) -> str:
    req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "application/json, */*"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return r.read().decode("utf-8", "replace")


def km(a, b) -> float:
    """Great-circle distance between (lat, lon) points."""
    la1, lo1, la2, lo2 = map(math.radians, (*a, *b))
    h = math.sin((la2 - la1) / 2) ** 2 + math.cos(la1) * math.cos(la2) * math.sin((lo2 - lo1) / 2) ** 2
    return 12742 * math.asin(math.sqrt(h))


def centers(index_path: str, home: str) -> list[dict]:
    """Pads worth watching: the next one in the home region, plus any pad launching within a day."""
    d = json.load(open(index_path, encoding="utf-8"))
    now = datetime.now(timezone.utc)
    out, seen = [], set()
    for x in d["launches"]:
        if x.get("past") or x["pad"].get("lat") is None:
            continue
        try:
            t = datetime.fromisoformat(re.sub(r"\.\d+", "", x["net"]).replace("Z", "+00:00"))
        except ValueError:
            continue
        soon = not x.get("time_tbd") and -3600 < (t - now).total_seconds() < 86400
        first_home = x.get("region") == home and home not in seen
        if not (soon or first_home):
            continue
        key = (round(x["pad"]["lat"], 1), round(x["pad"]["lon"], 1))
        if key in seen:
            continue
        seen.update({key, x.get("region")} if first_home else {key})
        out.append({"lat": x["pad"]["lat"], "lon": x["pad"]["lon"], "region": x.get("region"), "pad": x["pad"]["name"]})
    return out[:6]


# ---------------------------------------------------------------- aircraft

def aircraft(cs: list[dict]) -> list[dict]:
    out, seen, now = [], set(), time.time()
    for c in cs:
        try:
            d = json.loads(get(f"https://api.adsb.lol/v2/lat/{c['lat']:.3f}/lon/{c['lon']:.3f}/dist/{RADIUS_NM}"))
        except Exception as e:  # noqa: BLE001
            log("adsb failed", c["pad"], e)
            continue
        for a in d.get("ac", []):
            if a.get("lat") is None or a["hex"] in seen:
                continue
            seen.add(a["hex"])
            alt = a.get("alt_geom", a.get("alt_baro"))
            out.append({
                "id": a["hex"], "name": (a.get("flight") or a.get("r") or a["hex"]).strip(), "type": a.get("t"),
                "lat": round(a["lat"], 5), "lon": round(a["lon"], 5),
                "alt_ft": 0 if alt in (None, "ground") else alt, "ground": alt == "ground",
                "kt": a.get("gs"), "track": a.get("track"), "fpm": a.get("geom_rate", a.get("baro_rate")),
                "t": round(now - (a.get("seen_pos") or 0)),
            })
        time.sleep(1.1)  # the API allows about one request a second
    return out


# ---------------------------------------------------------------- vessels

async def _ais(key: str, boxes: list) -> dict:
    import websockets  # only needed when a key is configured

    ships: dict = {}
    async with websockets.connect("wss://stream.aisstream.io/v0/stream") as ws:
        await ws.send(json.dumps({"APIKey": key, "BoundingBoxes": boxes,
                                  "FilterMessageTypes": ["PositionReport", "StandardClassBPositionReport", "ShipStaticData"]}))
        end = time.time() + AIS_SECONDS
        while time.time() < end:
            try:
                m = json.loads(await asyncio.wait_for(ws.recv(), timeout=max(0.5, end - time.time())))
            except asyncio.TimeoutError:
                break
            meta, body = m.get("MetaData", {}), next(iter(m.get("Message", {}).values()), {})
            s = ships.setdefault(meta.get("MMSI"), {"id": str(meta.get("MMSI")), "name": (meta.get("ShipName") or "").strip()})
            if "Sog" in body:
                s.update(lat=round(meta["latitude"], 5), lon=round(meta["longitude"], 5), kt=body.get("Sog"),
                         track=body.get("Cog") if body.get("Cog", 360) < 360 else None,
                         heading=body.get("TrueHeading") if body.get("TrueHeading", 511) < 360 else None, t=round(time.time()))
            if "Type" in body:
                s["ship_type"] = body.get("Type")
    return ships


def vessels(cs: list[dict]) -> tuple[list[dict], str | None]:
    key = os.environ.get("AISSTREAM_API_KEY")
    if not key:
        return [], "No AIS key configured: add an aisstream.io key as the AISSTREAM_API_KEY repository secret."
    boxes = [[[c["lat"] - BOX_DEG, c["lon"] - BOX_DEG], [c["lat"] + BOX_DEG, c["lon"] + BOX_DEG]] for c in cs]
    try:
        ships = asyncio.run(_ais(key, boxes))
    except Exception as e:  # noqa: BLE001
        log("ais failed", e)
        return [], f"AIS feed failed: {e}"
    return [s for s in ships.values() if "lat" in s], None


# ---------------------------------------------------------------- hazard areas

def tfr_areas(cs: list[dict]) -> list[dict]:
    """FAA temporary flight restrictions for space operations, with altitudes and times from each detail record."""
    q = urllib.parse.urlencode({"service": "WFS", "version": "1.1.0", "request": "GetFeature", "typeName": "TFR:V_TFR_LOC", "outputFormat": "application/json"})
    feats = json.loads(get(f"https://tfr.faa.gov/geoserver/TFR/ows?{q}"))["features"]
    out = []
    for f in feats:
        p, g = f["properties"], f["geometry"]
        if p.get("LEGAL") != "SPACE OPERATIONS" or g["type"] not in ("Polygon", "MultiPolygon"):
            continue
        polys = [g["coordinates"]] if g["type"] == "Polygon" else g["coordinates"]
        rings = [[[round(y, 5), round(x, 5)] for x, y in poly[0]] for poly in polys]
        if not any(km(rings[0][0], (c["lat"], c["lon"])) < HAZARD_KM for c in cs):
            continue
        notam = p["NOTAM_KEY"].split("-")[0]
        area = {"kind": "notam", "id": f"FDC {notam}", "title": p.get("TITLE"), "rings": rings, "floor_ft": 0, "ceil_ft": None, "from": None, "to": None}
        try:
            x = get(f"https://tfr.faa.gov/download/detail_{notam.replace('/', '_')}.xml", timeout=25)
            one = lambda tag: (re.search(rf"<{tag}>([^<]*)</{tag}>", x) or [None, None])[1]  # noqa: E731
            area["from"], area["to"] = (one("dateEffective") or "") + "Z" or None, (one("dateExpire") or "") + "Z" or None
            up, uom = one("valDistVerUpper"), one("uomDistVerUpper")
            if up and up.isdigit():
                area["ceil_ft"] = None if (uom == "FL" and int(up) >= 999) else int(up) * (100 if uom == "FL" else 1)
            lo, luom = one("valDistVerLower"), one("uomDistVerLower")
            if lo and lo.isdigit():
                area["floor_ft"] = int(lo) * (100 if luom == "FL" else 1)
            time.sleep(0.2)
        except Exception as e:  # noqa: BLE001
            log("tfr detail failed", notam, e)
        out.append(area)
    return out


COORD = re.compile(r"(\d{1,2})-(\d{2}(?:\.\d+)?)([NS])\s+(\d{1,3})-(\d{2}(?:\.\d+)?)([EW])")


def parse_warning(text: str, year: int) -> tuple[list, str | None, str | None]:
    """Pull the bounded areas and the first time window out of a broadcast warning's text."""
    rings = []
    # areas are listed as "A. 28-38.00N 080-37.00W, ..." blocks, or a single list after "BOUND BY"
    body = re.split(r"\bBOUND(?:ED)? BY:?", text, maxsplit=1, flags=re.I)
    blocks = re.split(r"\n\s*[A-Z]\.\s", "\n" + body[-1]) if len(body) > 1 else [text]
    for b in blocks:
        b = re.split(r"\n\s*\d+\.\s", b)[0]  # stop at the next numbered paragraph ("2. CANCEL THIS MSG ...")
        pts = [[(int(d1) + float(m1) / 60) * (1 if h1 == "N" else -1), (int(d2) + float(m2) / 60) * (1 if h2 == "E" else -1)]
               for d1, m1, h1, d2, m2, h2 in COORD.findall(b)]
        if len(pts) >= 3:
            rings.append([[round(a, 5), round(o, 5)] for a, o in pts])
    start = end = None
    m = re.search(r"(\d{2})(\d{4})Z\s+TO\s+(\d{2})(\d{4})Z\s+([A-Z]{3})", text)
    if m and m.group(5) in MONTHS:
        mon = MONTHS[m.group(5)]
        try:
            a = datetime(year, mon, int(m.group(1)), int(m.group(2)[:2]), int(m.group(2)[2:]), tzinfo=timezone.utc)
            b = datetime(year, mon, int(m.group(3)), int(m.group(4)[:2]), int(m.group(4)[2:]), tzinfo=timezone.utc)
            if b < a:
                b += timedelta(days=28)
            start, end = a.strftime("%Y-%m-%dT%H:%M:%SZ"), b.strftime("%Y-%m-%dT%H:%M:%SZ")
        except ValueError:
            pass
    return rings, start, end


def notmar_areas(cs: list[dict]) -> list[dict]:
    """NGA broadcast warnings (NAVAREA IV / XII, HYDROLANT, HYDROPAC) that close sea areas for launches."""
    out = []
    for nav in ("4", "12", "A", "P"):
        try:
            d = json.loads(get(f"https://msi.nga.mil/api/publications/broadcast-warn?navArea={nav}&status=active&output=json"))
        except Exception as e:  # noqa: BLE001
            log("nga failed", nav, e)
            continue
        for w in d.get("broadcast-warn", []):
            text = w.get("text") or ""
            if not WORDS.search(text):
                continue
            rings, start, end = parse_warning(text, int(w.get("msgYear") or datetime.now(timezone.utc).year))
            rings = [r for r in rings if any(km(r[0], (c["lat"], c["lon"])) < HAZARD_KM for c in cs)]
            if rings:
                name = {"4": "NAVAREA IV", "12": "NAVAREA XII", "A": "HYDROLANT", "P": "HYDROPAC"}[nav]
                out.append({"kind": "notmar", "id": f"{name} {w.get('msgNumber')}/{w.get('msgYear')}", "title": text.strip().split("\n")[0][:80],
                            "text": text.strip()[:1500], "rings": rings, "floor_ft": 0, "ceil_ft": 0, "from": start, "to": end})
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="data")
    ap.add_argument("--home", default="Florida, USA")
    a = ap.parse_args()
    cs = centers(os.path.join(a.data, "launches.json"), a.home)
    stamp = datetime.now(timezone.utc).isoformat(timespec="seconds")
    log("watching", [c["pad"] for c in cs])

    notes = []
    planes = aircraft(cs)
    ships, why = vessels(cs)
    if why:
        notes.append(why)
    with open(os.path.join(a.data, "traffic.json"), "w", encoding="utf-8") as f:
        json.dump({"generated_at": stamp, "centers": cs, "aircraft": planes, "vessels": ships, "notes": notes}, f, separators=(",", ":"))

    areas, errors = [], []
    for name, fn in (("FAA TFR", tfr_areas), ("NGA broadcast warnings", notmar_areas)):
        try:
            got = fn(cs)
            areas += got
            if name.startswith("NGA") and not got:
                errors.append("No maritime launch warnings found, or the NGA service did not answer.")
        except Exception as e:  # noqa: BLE001
            log(name, "failed", e)
            errors.append(f"{name}: {e}")
    with open(os.path.join(a.data, "hazards.json"), "w", encoding="utf-8") as f:
        json.dump({"generated_at": stamp, "areas": areas, "notes": errors}, f, separators=(",", ":"))
    log(f"traffic: {len(planes)} aircraft, {len(ships)} vessels; hazards: {sum(x['kind'] == 'notam' for x in areas)} NOTAM, "
        f"{sum(x['kind'] == 'notmar' for x in areas)} NOTMAR areas; notes: {notes + errors}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
