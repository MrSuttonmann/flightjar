// List + map filter matchers. Keeps sidebar.js and trails.js from
// having to import each other just to share this small predicate.

import { militaryLabel } from './notable_aircraft.js';
import { state } from './state.js';

// True if `a` matches any active list filter (OR semantics). Returns
// true when no filters are active so callers can use the same helper
// for the "show everything" default.
export function matchesActiveFilters(a) {
  const active = state.activeFilters;
  if (active.size === 0) return true;
  if (active.has('mil') && militaryLabel(a.icao)) return true;
  if (active.has('emergency') && a.emergency) return true;
  if (active.has('watched') && state.watchlist?.has(a.icao)) return true;
  return false;
}
