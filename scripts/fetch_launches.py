#!/usr/bin/env python3
"""Build data/launches.json for the countdown site.

Primary source: nextspaceflight.com (launch list filtered to Florida pads, plus
each launch's detail page). The site is a Next.js app that embeds its data as
JSON in the page's React Server Component payload, which is what we read.

Enrichment: Launch Library 2 (thespacedevs.com) detailed records for the Cape /
KSC, matched to each Next Spaceflight launch by pad, time and name.

Standard library only, so it runs on a bare GitHub Actions runner.
"""
from __future__ import annotations

import argparse
import difflib
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime, timezone

NSF = "https://nextspaceflight.com"
LL2 = "https://ll.thespacedevs.com/2.3.0"
UA = "Mozilla/5.0 (compatible; launch-countdown-bot; +https://github.com/)"
PUSH_RE = re.compile(r"self\.__next_f\.push\((\[1,.*?\])\)</script>", re.S)


def log(*a):
    print(*a, file=sys.stderr, flush=True)


def get(url: str, tries: int = 3, timeout: int = 40) -> str:
    last = None
    for i in range(tries):
        try:
            req = urllib.request.Request(url, headers={"User-Agent": UA, "Accept": "*/*"})
            with urllib.request.urlopen(req, timeout=timeout) as r:
                return r.read().decode("utf-8", "replace")
        except (urllib.error.URLError, TimeoutError, OSError) as e:  # noqa: PERF203
            last = e
            if isinstance(e, urllib.error.HTTPError) and e.code in (403, 404, 429):
                break
            time.sleep(2 * (i + 1))
    raise RuntimeError(f"GET {url} failed: {last}")


def rsc_payload(html: str) -> str:
    """Concatenate the React Server Component chunks embedded in a Next.js page."""
    return "".join(json.loads(m.group(1))[1] for m in PUSH_RE.finditer(html))


def decode_at(text: str, marker: str):
    """JSON-decode the value that starts right after `marker` in `text`."""
    i = text.find(marker)
    if i < 0:
        return None
    try:
        return json.JSONDecoder().raw_decode(text, i + len(marker))[0]
    except json.JSONDecodeError:
        return None


# ---------------------------------------------------------------- nextspaceflight

def nsf_list(query: str = "") -> tuple[list, str]:
    payload = rsc_payload(get(f"{NSF}/launches/{query}"))
    return decode_at(payload, '"initialLaunches":') or [], payload


def locations(payload: str) -> dict[str, list[str]]:
    """Launch sites from the page's pad filter: {location name: [pad ids]}."""
    out: dict[str, list[str]] = {}
    for _pad, loc, pid in re.findall(r'\{"label":"([^"]*?) @ ([^"]*)","value":"(\d+)"\}', payload):
        out.setdefault(loc, []).append(pid)
    return out


def region(location: str) -> str:
    """Group sites the way a viewer thinks of them: US state, otherwise country."""
    parts = [p.strip() for p in (location or "Unknown").split(",")]
    if parts[-1] == "USA" and len(parts) >= 2:
        return f"{parts[-2]}, USA"
    if parts[-1] == "France" and len(parts) >= 3:
        return parts[-2]
    return parts[-1]


HOME_REGION = "Florida, USA"
SWEEP_MINUTES = 55     # how often every site is re-read; in between only the busy ones are
HOT_HOURS = 72         # a site with a launch this close is re-read on every run


def nsf_detail(launch_id: int) -> dict | None:
    payload = rsc_payload(get(f"{NSF}/launches/details/{launch_id}/"))
    for m in re.finditer(r'\{"id":%d,"missions"' % launch_id, payload):
        try:
            return json.JSONDecoder().raw_decode(payload, m.start())[0]
        except json.JSONDecodeError:
            continue
    return None


def fetch_nsf(prev: dict, max_details: int) -> tuple[list[dict], bool]:
    """Read upcoming launches site by site.

    The list page returns at most 18 launches per query, so each site is queried on its own.
    A full sweep of every site runs about once an hour; other runs re-read the home region and
    any site with a launch in the next few days, and reuse the last result for the rest.
    """
    _, payload = nsf_list()
    locs = locations(payload)
    if not locs:
        raise RuntimeError("could not find the pad filters on nextspaceflight.com")

    now = datetime.now(timezone.utc)
    cached: dict[str, list[dict]] = {}
    for x in prev.get("launches", []):
        if x.get("id", "").startswith("nsf-") and not x.get("past"):
            cached.setdefault(x["pad"].get("location") or "", []).append(x)
    swept = parse_t(prev.get("swept_at"))
    full = not swept or (now - swept).total_seconds() > SWEEP_MINUTES * 60 or not cached
    hot = {loc for loc, rows in cached.items() for x in rows
           if not x.get("time_tbd") and parse_t(x.get("net")) and abs((parse_t(x["net"]) - now).total_seconds()) < HOT_HOURS * 3600}

    items: list[dict] = []
    read = 0
    for loc, ids in locs.items():
        home = region(loc) == HOME_REGION
        if not (full or home or loc in hot):
            # carry the last result forward untouched until the next full sweep
            items += [{"cached": x} for x in cached.get(loc, [])]
            continue
        try:
            rows, _ = nsf_list("?q=&f=pad_" + "_".join(ids))
            read += 1
            time.sleep(0.15)
        except RuntimeError as e:
            log("list failed", loc, e)
            items += [{"cached": x} for x in cached.get(loc, [])]
            continue
        items += [{"summary": r, "past": False, "home": home} for r in rows]
        if home:
            try:
                past, _ = nsf_list("?q=&tab=past&f=pad_" + "_".join(ids))
                items += [{"summary": r, "past": True, "home": True} for r in past[:3]]
            except RuntimeError as e:
                log("past list failed", loc, e)

    # Detail pages: every launch with a real date, soonest first, plus everything at home.
    old = {x["id"]: x for x in prev.get("launches", [])}
    fresh = [it for it in items if "summary" in it]
    fresh.sort(key=lambda it: (not it["home"], "23:59:59.0000" in (it["summary"].get("net") or ""), it["summary"].get("net") or "9"))
    budget = max_details
    for it in fresh:
        s = it["summary"]
        before = old.get(f"nsf-{s['id']}") or {}
        placeholder = "23:59:59.0000" in (s.get("net") or "")
        have = (before.get("nsf") or {}).get("missions") is not None
        if budget > 0 and (it["past"] or not placeholder or not have):
            try:
                it["detail"] = nsf_detail(s["id"])
                budget -= 1
                time.sleep(0.25)
            except RuntimeError as e:
                log("detail failed", s["id"], e)
        if not it.get("detail") and have:
            it["detail"] = before["nsf"]  # keep the last detail we had rather than lose it
    log(f"nextspaceflight: {'full sweep' if full else 'partial'}, {read} of {len(locs)} sites read, "
        f"{len(fresh)} launches listed, {max_details - budget} detail pages, {len(items) - len(fresh)} carried over")
    return items, full


# ---------------------------------------------------------------- launch library 2

DROP_KEYS = {"url", "response_mode", "license", "variants", "social_logo", "nationality_name_composed",
             "celestial_body", "map_image", "single_use"}


def prune(o, depth=0):
    """Drop API plumbing and empty values so the published JSON stays small."""
    if isinstance(o, dict):
        if depth > 2 and {"image_url", "thumbnail_url"} <= o.keys():
            return {"image_url": o.get("image_url"), "credit": o.get("credit")}
        r = {}
        for k, v in o.items():
            if k in DROP_KEYS:
                continue
            v = prune(v, depth + 1)
            if v in (None, "", [], {}):
                continue
            r[k] = v
        return r
    if isinstance(o, list):
        return [prune(v, depth + 1) for v in o]
    return o


def fetch_ll2(limit: int) -> list[dict]:
    q = urllib.parse.urlencode({"limit": limit, "mode": "detailed", "format": "json"})
    data = json.loads(get(f"{LL2}/launches/upcoming/?{q}", tries=2))
    rows = [prune(r) for r in data.get("results", [])]
    log(f"launch library: {len(rows)} launches")
    return rows


def pad_key(name: str) -> str:
    """'SLC-41' and 'Space Launch Complex 41' both become '41'."""
    m = re.search(r"(\d+[A-Z]?)\s*$", (name or "").upper().replace("STARSHIP PAD", "").strip())
    return m.group(1) if m else (name or "").upper()


def parse_t(s):
    if not s:
        return None
    try:
        return datetime.fromisoformat(re.sub(r"\.\d+", "", s).replace("Z", "+00:00"))
    except ValueError:
        return None


def match_ll2(n: dict, ll2: list[dict], used: set) -> dict | None:
    nt = parse_t(n.get("net"))
    best, best_score = None, 0.0
    for r in ll2:
        if r["id"] in used:
            continue
        mission = (r.get("mission") or {}).get("name") or r.get("name", "").split("|")[-1]
        name_sim = difflib.SequenceMatcher(None, n["name"].lower().strip(), mission.lower().strip()).ratio()
        same_pad = pad_key(n.get("pad_name")) == pad_key((r.get("pad") or {}).get("name"))
        rt = parse_t(r.get("net"))
        dt = abs((nt - rt).total_seconds()) if nt and rt else 1e12
        score = name_sim + (0.3 if same_pad else 0) + (0.5 if dt < 7200 else 0.2 if dt < 86400 * 3 else 0)
        if (name_sim >= 0.75 or (same_pad and dt < 7200)) and score > best_score:
            best, best_score = r, score
    if best:
        used.add(best["id"])
    return best


# ---------------------------------------------------------------- normalise

NSF_STATUS = {0: "TBD Date & Time", 1: "TBD Time", 2: "Go", 3: "Hold", 4: "Scrub", 5: "In-Flight", 6: "Success",
              7: "Partial Failure", 8: "Failure", 9: "Prelaunch Failure", 10: "Outcome Pending"}


def from_nsf(item: dict, ll: dict | None) -> dict:
    s, d = item["summary"], item.get("detail") or {}
    pad = d.get("pad") or {}
    llpad = (ll or {}).get("pad") or {}
    status = d.get("status") or {"id": s.get("status_id"), "name": NSF_STATUS.get(s.get("status_id"), "Unknown")}
    ll_precise = ((ll or {}).get("net_precision") or {}).get("abbrev") in ("SEC", "MIN")
    return {
        "id": f"nsf-{s['id']}",
        "name": s["name"],
        "past": item["past"],
        "net": d.get("net") or s.get("net"),
        "window_open": d.get("window_open") or (ll or {}).get("window_start"),
        "window_close": d.get("window_close") or (ll or {}).get("window_end"),
        # Next Spaceflight encodes placeholder dates as 23:59:59.0000xx; a real T-0 never has that shape.
        "time_tbd": status.get("id") in (0, 1) or "23:59:59.0000" in (s.get("net") or ""),
        "time_precise": status.get("id") in (2, 3, 5) or ll_precise,
        "status": {"id": status.get("id"), "name": status.get("name"), "color": status.get("color")},
        "provider": s.get("lsp_name"),
        "rocket": s.get("rocket_config_name"),
        "image": (d.get("rocket_config") or {}).get("image") or s.get("rocket_config_thumbnail")
                 or ((ll or {}).get("image") or {}).get("image_url"),
        "region": region(pad.get("location_name") or s.get("pad_location_name")),
        "pad": {
            "name": pad.get("name") or s.get("pad_name"),
            "location": pad.get("location_name") or s.get("pad_location_name"),
            "lat": pad.get("latitude") or _f(llpad.get("latitude")),
            "lon": pad.get("longitude") or _f(llpad.get("longitude")),
            "timezone": pad.get("timezone") or (llpad.get("location") or {}).get("timezone_name"),
        },
        "heading": d.get("heading"),
        "weather_go_pct": d.get("launch_weather") if d.get("launch_weather") is not None else (ll or {}).get("probability"),
        "nsf_url": f"{NSF}/launches/details/{s['id']}/",
        "nsf": d or s,
        "ll2": ll,
    }


def from_ll2(r: dict) -> dict:
    pad = r.get("pad") or {}
    st = r.get("status") or {}
    cfg = (r.get("rocket") or {}).get("configuration") or {}
    return {
        "id": f"ll2-{r['id']}",
        "name": (r.get("mission") or {}).get("name") or r.get("name"),
        "past": False,
        "net": r.get("net"),
        "window_open": r.get("window_start"),
        "window_close": r.get("window_end"),
        "time_tbd": st.get("abbrev") in ("TBD", "TBC"),
        "time_precise": (r.get("net_precision") or {}).get("abbrev") in ("SEC", "MIN"),
        "status": {"id": None, "name": st.get("name")},
        "provider": (r.get("launch_service_provider") or {}).get("name"),
        "rocket": cfg.get("full_name") or cfg.get("name"),
        "image": (r.get("image") or {}).get("image_url"),
        "region": region((pad.get("location") or {}).get("name", "").replace(", FL, USA", ", Florida, USA")),
        "pad": {"name": pad.get("name"), "location": (pad.get("location") or {}).get("name"),
                "lat": _f(pad.get("latitude")), "lon": _f(pad.get("longitude")),
                "timezone": (pad.get("location") or {}).get("timezone_name")},
        "heading": None,
        "weather_go_pct": r.get("probability"),
        "nsf_url": None,
        "nsf": None,
        "ll2": r,
    }


def _f(v):
    try:
        return float(v)
    except (TypeError, ValueError):
        return None


def load_previous(src: str | None) -> dict:
    if not src:
        return {}
    try:
        text = get(src, tries=1, timeout=20) if src.startswith("http") else open(src, encoding="utf-8").read()
        return json.loads(text)
    except (RuntimeError, OSError, ValueError) as e:
        log("no previous data:", e)
        return {}


INDEX_KEYS = ("id", "name", "past", "net", "window_open", "window_close", "time_tbd", "time_precise", "status", "provider",
              "rocket", "image", "region", "pad", "heading", "weather_go_pct", "nsf_url")


def write_json(path: str, obj) -> None:
    os.makedirs(os.path.dirname(path) or ".", exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        json.dump(obj, f, ensure_ascii=False, separators=(",", ":"))


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default="data/launches.json")
    ap.add_argument("--previous", help="path or URL of the last published cache.json (carried-over data and fallback)")
    ap.add_argument("--max-details", type=int, default=90)
    ap.add_argument("--ll2-limit", type=int, default=50)
    a = ap.parse_args()
    out_dir = os.path.dirname(a.out) or "."

    prev = load_previous(a.previous) or load_previous(os.path.join(out_dir, "cache.json"))
    sources, errors = [], []
    swept_at = prev.get("swept_at")

    try:
        nsf, full = fetch_nsf(prev, a.max_details)
        sources.append("nextspaceflight.com")
        if full:
            swept_at = datetime.now(timezone.utc).isoformat(timespec="seconds")
    except Exception as e:  # noqa: BLE001 - any scrape failure falls back to other sources
        log("nextspaceflight failed:", e)
        errors.append(f"nextspaceflight: {e}")
        nsf = []

    try:
        ll2 = fetch_ll2(a.ll2_limit)
        sources.append("Launch Library 2")
    except Exception as e:  # noqa: BLE001
        log("launch library failed:", e)
        errors.append(f"launch library: {e}")
        # Reuse the enrichment from the last run (LL2 rate-limits shared CI addresses).
        ll2 = [x["ll2"] for x in prev.get("launches", []) if x.get("ll2")]
        if ll2:
            sources.append("Launch Library 2 (cached)")

    if nsf:
        used: set = set()
        launches = []
        for it in nsf:
            if "cached" in it:
                launches.append(it["cached"])
            else:
                # Launch Library rows are upcoming launches, so never pair them with a flown one.
                launches.append(from_nsf(it, None if it["past"] else match_ll2(it["summary"], ll2, used)))
        # A launch listed without a detail page has no coordinates: borrow them from its pad's other launches.
        pads = {(x["pad"]["location"], x["pad"]["name"]): x["pad"] for x in launches + prev.get("launches", []) if x["pad"].get("lat") is not None}
        for x in launches:
            known = pads.get((x["pad"]["location"], x["pad"]["name"]))
            if x["pad"].get("lat") is None and known:
                x["pad"].update(lat=known["lat"], lon=known["lon"], timezone=x["pad"].get("timezone") or known.get("timezone"))
        seen: set = set()
        launches = [x for x in launches if not (x["id"] in seen or seen.add(x["id"]))]
    elif ll2:
        launches = [from_ll2(r) for r in ll2]
    elif prev.get("launches"):
        log("all sources failed; keeping previous data")
        prev["errors"] = errors
        prev["stale"] = True
        launches = None
    else:
        log("all sources failed and there is no previous data")
        return 1

    if launches is not None:
        launches.sort(key=lambda x: (x["past"], x["net"] or "9999"))
        prev = {
            "generated_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
            "swept_at": swept_at,
            "sources": sources,
            "errors": errors,
            "stale": False,
            "launches": launches,
        }

    # cache.json: everything, for the next run. launches.json: the light index the page polls.
    # launch/<id>.json: the full source records for one launch, loaded when it is selected.
    write_json(os.path.join(out_dir, "cache.json"), prev)
    regions = sorted({x.get("region") or "Unknown" for x in prev["launches"]}, key=lambda r: (r != HOME_REGION, r))
    write_json(a.out, {**{k: v for k, v in prev.items() if k != "launches"}, "home_region": HOME_REGION, "regions": regions,
                       "launches": [{k: x.get(k) for k in INDEX_KEYS} for x in prev["launches"]]})
    for x in prev["launches"]:
        write_json(os.path.join(out_dir, "launch", f"{x['id']}.json"), {"id": x["id"], "nsf": x.get("nsf"), "ll2": x.get("ll2")})

    # A tiny file for the Windows screensaver helper, which only needs to know when to wake the display.
    now = datetime.now(timezone.utc)

    def brief(x):
        return {"id": x["id"], "name": x["name"], "rocket": x["rocket"], "pad": x["pad"]["name"], "region": x.get("region"),
                "net": parse_t(x["net"]).strftime("%Y-%m-%dT%H:%M:%SZ")}

    firm = [x for x in prev["launches"] if not x.get("past") and not x.get("time_tbd") and parse_t(x.get("net"))
            and (parse_t(x["net"]) - now).total_seconds() > -900]
    by_region: dict = {}
    for x in firm:
        by_region.setdefault(x.get("region") or "Unknown", brief(x))
    home = by_region.get(HOME_REGION) or {"id": None, "name": None, "rocket": None, "pad": None, "region": HOME_REGION, "net": None}
    write_json(os.path.join(out_dir, "next.json"), {"generated_at": prev.get("generated_at"), **home, "by_region": by_region})
    log(f"wrote {a.out}: {len(prev['launches'])} launches in {len(regions)} regions from {', '.join(prev.get('sources', []))}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
