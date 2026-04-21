// Static tables for recognising aircraft that are interesting in
// their own right — famous registrations, VIP callsigns, and military
// hex blocks. Kept intentionally small: curated > comprehensive. Add
// to the tables when you spot something worth flagging.
//
// Hex codes are lowercase throughout. Callsign prefixes are upper.

// -------- Notable by hex --------

// Hex-keyed entries fire a one-off toast on first sighting per session
// and add a ⭐ badge. The emoji renders as a prefix in the sidebar row.
export const NOTABLE_HEX = Object.freeze({
  // United States — VIP fleet
  'adfdf8': { name: 'VC-25A (Air Force One)', emoji: '🇺🇸' },
  'adfdf9': { name: 'VC-25A (Air Force One)', emoji: '🇺🇸' },
  // NASA — research / support
  'a39d68': { name: 'NASA 515 (B747 SCA, retired)', emoji: '🚀' },
  'a0cf76': { name: 'NASA Armstrong', emoji: '🚀' },
  // United Kingdom — royal + military VIP
  '43c6dc': { name: 'UK Royal Flight', emoji: '👑' },
  '43c4dd': { name: 'RAF Voyager "ZZ336"', emoji: '🇬🇧' },
  // Germany — government
  '3c4ad3': { name: 'German Air Force VIP', emoji: '🇩🇪' },
  // Holy See — Papal flight (varies; callsign-prefix match usually
  // more reliable, see NOTABLE_CALLSIGN_PREFIXES below).
});

// -------- Notable by callsign prefix --------

// Matched against the first N characters of the broadcast callsign
// where N is the prefix length. Useful for tails that rotate but use a
// stable callsign convention.
export const NOTABLE_CALLSIGN_PREFIXES = Object.freeze({
  SHEPHERD: { name: 'Papal flight', emoji: '🕊️' },
  SAM: { name: 'USAF Special Air Mission', emoji: '🇺🇸' },
  RCH: { name: 'USAF Reach (air mobility)', emoji: '🇺🇸' },
  KITTY: { name: 'Kitty Hawk USAF', emoji: '🇺🇸' },
  KRF: { name: 'UK Royal Flight', emoji: '👑' },
  NASA: { name: 'NASA', emoji: '🚀' },
  GAF: { name: 'German Air Force', emoji: '🇩🇪' },
  IAM: { name: 'Italian Air Force', emoji: '🇮🇹' },
});

// Seasonal entries merged into the prefix table only on specific
// dates. Kept separate so the main table stays truly immutable.
const SEASONAL_CALLSIGN_PREFIXES = Object.freeze({
  // Christmas Eve — any "SANTA*" / "HOHO*" novelty flight gets a sleigh.
  XMAS_EVE: Object.freeze({
    SANTA: { name: 'Santa!', emoji: '🎅' },
    HOHO: { name: 'Ho ho ho', emoji: '🎄' },
  }),
});

function activeSeasonal(date = new Date()) {
  if (date.getMonth() === 11 && date.getDate() === 24) {
    return SEASONAL_CALLSIGN_PREFIXES.XMAS_EVE;
  }
  return null;
}

// -------- Military hex blocks --------

// Prefix → display label. The matcher is longest-prefix-first so a
// three-char entry takes precedence over a two-char entry that
// overlaps. Ranges are approximate — real-world MIL allocations have
// exceptions but the chip is indicative, not authoritative.
export const MIL_PREFIXES = Object.freeze({
  ae: 'MIL · US',
  af: 'MIL · US',
  '43c': 'MIL · UK',
  '3ea': 'MIL · DE',
  '3eb': 'MIL · DE',
  '3f4': 'MIL · DE',
  '3f5': 'MIL · DE',
  '3f6': 'MIL · DE',
  '3f7': 'MIL · DE',
  '3f8': 'MIL · DE',
  '3f9': 'MIL · DE',
  '3fa': 'MIL · DE',
  '3fb': 'MIL · DE',
  '3fc': 'MIL · DE',
  '3fd': 'MIL · DE',
  '3fe': 'MIL · DE',
  '3ff': 'MIL · DE',
  '33ff': 'MIL · IT',
  '3b7': 'MIL · FR',
  '7cf': 'MIL · AU',
});

// -------- lookups --------

/** Returns { name, emoji } for a notable tail, or null. The optional
 * `now` argument lets tests pin the date for the seasonal-prefix check
 * without monkey-patching Date. */
export function isNotable(icao, callsign, now = new Date()) {
  if (icao) {
    const hit = NOTABLE_HEX[icao.toLowerCase()];
    if (hit) return hit;
  }
  if (callsign) {
    const upper = callsign.toUpperCase();
    const seasonal = activeSeasonal(now) || {};
    // Merge seasonal first (so 'SANTA' etc. are in play) and
    // longest-prefix-first so multi-char entries take precedence.
    const merged = { ...NOTABLE_CALLSIGN_PREFIXES, ...seasonal };
    const prefixes = Object.keys(merged).sort((a, b) => b.length - a.length);
    for (const p of prefixes) {
      if (upper.startsWith(p)) return merged[p];
    }
  }
  return null;
}

/** Returns a military-hex label (e.g. 'MIL · US') or null. */
export function militaryLabel(icao) {
  if (!icao) return null;
  const lower = icao.toLowerCase();
  const prefixes = Object.keys(MIL_PREFIXES).sort((a, b) => b.length - a.length);
  for (const p of prefixes) {
    if (lower.startsWith(p)) return MIL_PREFIXES[p];
  }
  return null;
}
