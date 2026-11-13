# Launch Countdown

A static GitHub Pages site that counts down, with millisecond precision, to the next launch from the sites you choose (Florida by default), and puts the weather and range picture for that launch on one page.

## What is on the page

**Sites**: the Sites menu chooses which launch regions to follow (US states, otherwise countries); the choice is saved per browser, and `?sites=Florida, USA|Texas, USA` in the address sets it directly. Everything follows the selected launch's pad. What is available depends on where it is:

| | Florida, California, Texas, Virginia, New Mexico | Other US sites | Outside the US |
|---|---|---|---|
| Model forecast, soundings, clouds, wind field, sea state, space weather, 3D corridor and telemetry | yes | yes | yes |
| NWS observations, forecast, alerts, forecast discussion | yes | yes | no |
| Site NEXRAD radar on the map and in 3D | yes | national composite only | worldwide mosaic on the map, none in 3D |
| Observed balloon soundings | yes | Alaska only | no |
| Tide gauge | Florida, California, Texas, Virginia | no | no |

Per-site details live in `assets/js/sites.js`.

Each section is its own tab; nothing scrolls as one long page. **Slideshow** (`P`) cycles through the tabs on a timer you set in seconds, and the **Tabs** menu picks which ones it visits. Arrow keys step between tabs. The site reloads itself when a new version is deployed and re-reads launch data every minute; an entered T-0 survives both unless the source publishes a new one.

- **Volumetric radar** (3D tab): the browser downloads the newest KMLB Level II volume from the public NEXRAD archive on AWS, decodes every reflectivity tilt in a web worker, resamples it to a 192 × 192 × 40 grid around the pad and ray-marches it with the NWS colour scale. It checks for a new volume every two minutes; volumes arrive roughly 5 to 8 minutes after the scan starts.
- **Themes**: Mission Control (default), Agency, Deep Blue, Tactical, Restricted, Console and Daylight, chosen from the Theme menu and remembered per browser. `?theme=tactical` in the address sets one directly, which is how to theme the screensaver. They are colour and type treatments only: no agency logos or insignia, and the Restricted theme's marking bars read UNCLASSIFIED because everything shown is public data.
- **Aircraft, vessels and hazard areas** (3D tab): aircraft from ADS-B (adsb.lol) and vessels from AIS (aisstream.io) near the pad, each with a projected track to where it will be in ten minutes (dots at 5 and 10 minutes, capped at 25 km so fast aircraft stay readable). FAA space-operations TFRs are drawn as NOTAM airspace and NGA broadcast warnings as NOTMAR sea areas; anything inside an area that is active or starts within two hours flashes red and yellow and is labelled. None of these sources can be read from a browser, so `scripts/fetch_traffic.py` snapshots them on each deploy (about every ten minutes) and the page dead-reckons positions forward from the snapshot. Vessels need a free aisstream.io key saved as the `AISSTREAM_API_KEY` repository secret.
- The telemetry box on the 3D view can be hidden (telemetry checkbox) or dragged anywhere; double-click it to put it back.
- **Terminal count**: from T-1 h to T+15 min the page switches by itself to one fixed layout: count top centre, the 3D corridor on the left, live video on the right with the countdown timeline beneath it. The slideshow pauses for it. Before liftoff the corridor view sits close on the pad; after liftoff the camera follows the vehicle and pulls back as it accelerates.
- **Calculated telemetry** (3D view): altitude, downrange, velocity, flight-path angle, Mach, acceleration, dynamic pressure, outside temperature, pressure and density, and the wind at the vehicle's height with head/tail and cross components. It is computed from the nominal ascent profile flown through the forecast sounding over the pad, not received from the vehicle.
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

## Windows screensaver

`screensaver/` is a small WinForms program that shows the site full-screen in WebView2 as a `.scr`. Download the latest build from the [screensaver release](https://github.com/Coral-coder/launch-countdown/releases/tag/screensaver), extract the folder, right-click `LaunchCountdown.scr` and choose **Install**.

It opens the site with `?screensaver=1&secs=20`, which forces the slideshow, hides the controls and skips the video tab. The build is manual: run the **Build Windows screensaver** workflow from the Actions tab to publish a new version.

It does not keep the display awake: there is no video playback and no power request, so the Windows display timer applies as usual, and the screensaver's own timer (default 30 minutes, set under **Settings…**) switches the monitor off as well.

## How the data gets there

Next Spaceflight does not allow cross-origin requests, so a GitHub Actions workflow (`.github/workflows/pages.yml`) runs `scripts/fetch_launches.py` about every ten minutes and deploys the site. Florida and any site with a launch in the next three days are re-read on every run; every other site is re-read about once an hour. It writes `data/launches.json` (a light index of every launch), `data/launch/<id>.json` (the full records for one launch, loaded when selected), `data/cache.json` (carried between runs) and `data/next.json` (for the screensaver). The page re-reads that file every minute. All weather feeds are fetched live by the browser.

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
