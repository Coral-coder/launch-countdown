# Florida Launch Countdown

A static GitHub Pages site that counts down to the next launch from Cape Canaveral / Kennedy Space Center with millisecond precision, and puts the weather and range picture for that launch on one page.

## What is on the page

Each section is its own tab; nothing scrolls as one long page. **Slideshow** (`P`) cycles through the tabs on a timer you set in seconds, and the **Tabs** menu picks which ones it visits. Arrow keys step between tabs. The site reloads itself when a new version is deployed and re-reads launch data every minute; an entered T-0 survives both unless the source publishes a new one.

- **Volumetric radar** (3D tab): the browser downloads the newest KMLB Level II volume from the public NEXRAD archive on AWS, decodes every reflectivity tilt in a web worker, resamples it to a 192 × 192 × 40 grid around the pad and ray-marches it with the NWS colour scale. It checks for a new volume every two minutes; volumes arrive roughly 5 to 8 minutes after the scan starts.
- **3D Ascent & Clouds**: a WebGL scene with a modelled ascent corridor along the published azimuth, a ray-marched cloud volume (300 × 300 × 16 km) built from forecast-model cloud fraction on 14 pressure levels, the wind profile over the pad, the freezing level, and a vehicle marker that follows the clock after liftoff. The corridor is a generic two-stage profile, not provider trajectory data, and the fine cloud texture is synthetic.

- **Countdown** to T-0 in days, hours, minutes, seconds and milliseconds, with **HOLD**, **SCRUB** and **RESET** controls in the top bar (`H` toggles a hold).
  - HOLD freezes the T clock. RESUME picks the count up where it stopped, so T-0 slips by the length of the hold.
  - SET T-0 (`T`) opens a dialog to enter a new T-0 in UTC, Eastern or local time, or slip the current one by a preset amount. It works at any time, releases a hold or scrub, and gives undated launches a clock.
  - SCRUB stops the attempt; RECYCLE returns to the published T-0.
  - The state is saved per launch in the browser, and every action is written to the controller log. It is a local control: it does not change the published launch status, which is shown next to it.
- **Launch details** from Next Spaceflight (mission, vehicle, pad, recovery, statistics, countdown timeline, webcast), enriched with Launch Library 2 (updates, boosters, crew, programme).
- **Pad and weather stream** (IBM Video channel 23615375) and the launch webcast.
- **Map**: KMLB NEXRAD Level II super-resolution reflectivity and velocity, Level III products (N0B, N0U, N0S, hydrometeor class, rainfall), MRMS composite, GOES-East imagery, NWS alert polygons, a two-hour radar loop, the ground track drawn from the published launch azimuth, 5 and 10 nmi rings around the pad, and an animated wind field at ten levels from the surface to 200 hPa.
- **Weather**: pad forecast at T-0 and now, range METARs, hourly charts, NWS forecast, alerts and forecast discussion.
- **Upper air**: skew-T, hodograph and level table for the model profile over the pad (at T-0 or now) and the latest observed balloons from Tampa, Jacksonville and Miami.
- **Sea and space**: downrange sea state, Port Canaveral tide station, NOAA space weather.
- **Commit-criteria watch**: an unofficial screen of the public data against the usual weather constraints.
- **Raw data**: every record and feed the page has loaded, as JSON.

## How the data gets there

Next Spaceflight does not allow cross-origin requests, so a GitHub Actions workflow (`.github/workflows/pages.yml`) runs `scripts/fetch_launches.py` about every ten minutes, writes `data/launches.json`, and deploys the site. The page re-reads that file every minute. All weather feeds are fetched live by the browser.

GitHub Pages must be set to **Source: GitHub Actions** (Settings → Pages). GitHub pauses scheduled workflows after 60 days without repository activity; re-enable it from the Actions tab if that happens.

## Run locally

```bash
python3 scripts/fetch_launches.py
```

```bash
python3 -m http.server 8000
```

## Caveats

- Not an official source. The commit-criteria screen uses generic thresholds; the 45th Weather Squadron and the launch provider make the real calls with instruments that are not public.
- The ground track is a straight great-circle line from the published azimuth, not a flown trajectory.
- The Cape's own range balloon soundings are not published, so observed soundings come from the nearest NWS sites.
