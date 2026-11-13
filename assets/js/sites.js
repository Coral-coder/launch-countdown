// What is known about each launch region beyond the launch list itself: which weather radar
// covers it, which stations to quote, where the nearest balloons and tide gauge are.
// A region missing from this table still gets everything that works worldwide (model forecast,
// soundings, clouds, sea state, space weather); US regions also get NWS data automatically.

const SITES = {
  'Florida, USA': {
    radar: 'KMLB', area: 'FL', forecaster: 'the 45th Weather Squadron',
    stations: [['KXMR', 'Cape Canaveral Skid Strip'], ['KTTS', 'KSC Shuttle Landing Facility'], ['KCOF', 'Patrick SFB'], ['KTIX', 'Titusville'], ['KMLB', 'Melbourne']],
    raobs: [['KTBW', 'Tampa Bay'], ['KJAX', 'Jacksonville'], ['KMFL', 'Miami']],
    tide: ['8721604', 'Port Canaveral'], raobNote: "the Cape's own range balloons are not published",
  },
  'California, USA': {
    radar: 'KVBX', area: 'CA', forecaster: 'Space Launch Delta 30 weather',
    raobs: [['KOAK', 'Oakland'], ['KNKX', 'San Diego']], tide: ['9411340', 'Santa Barbara'],
  },
  'Texas, USA': { radar: 'KBRO', area: 'TX', raobs: [['KBRO', 'Brownsville'], ['KCRP', 'Corpus Christi']], tide: ['8779749', 'South Padre Island'] },
  'Virginia, USA': { radar: 'KDOX', area: 'VA', raobs: [['KIAD', 'Sterling'], ['KMHX', 'Morehead City']], tide: ['8631044', 'Wachapreague'] },
  'New Mexico, USA': { radar: 'KHDX', area: 'NM', raobs: [['KEPZ', 'Santa Teresa'], ['KABQ', 'Albuquerque']] },
  'Alaska, USA': { area: 'AK', raobs: [['PADQ', 'Kodiak']] },
};

export const siteFor = (region) => SITES[region] || {};
export const inUS = (region) => /, USA$/.test(region || '');
