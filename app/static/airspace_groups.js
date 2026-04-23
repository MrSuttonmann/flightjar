// Airspace subcategory grouping — pure data + one helper, kept in its
// own module (no Leaflet / DOM imports) so the grouping logic is
// importable from Node tests. openaip.js re-exports these.

export const AIRSPACE_GROUPS = [
  { key: 'restricted', label: 'Restricted / Prohibited',
    swatch: { color: '#dc2626', fill: '#dc2626', fillOpacity: 0.18, dashArray: '6 3' },
    hint: 'Prohibited or restricted airspace — entry forbidden or subject to authorisation. Includes temporary reserved/segregated areas (TRA / TSA).' },
  { key: 'danger', label: 'Danger / Warning / Alert',
    swatch: { color: '#dc2626', fill: '#dc2626', fillOpacity: 0.10, dashArray: '2 4' },
    hint: 'Hazard areas — live firing, military exercises, published risks. Flight is not forbidden, but expect activity you need to avoid.' },
  { key: 'controlled', label: 'Controlled (CTR / TMA / CTA / Class A–D)',
    swatch: { color: '#2563eb', fill: '#2563eb', fillOpacity: 0.12 },
    hint: 'ATC-controlled airspace where clearance and radio contact are required. CTR around airports, TMA/CTA at altitude, ICAO Class A–D.' },
  { key: 'class_ef', label: 'Class E / F',
    swatch: { color: '#16a34a', fill: '#16a34a', fillOpacity: 0.07 },
    hint: 'Lightly controlled airspace — IFR separated from IFR, VFR traffic largely self-separating. Class F is advisory only.' },
  { key: 'airways', label: 'Class G / FIR / Airways',
    swatch: { color: '#6b7280', fill: '#6b7280', fillOpacity: 0.06, dashArray: '2 3' },
    hint: 'Uncontrolled airspace (Class G), Flight Information Regions, upper information regions, and published airway routes.' },
  { key: 'atz_matz', label: 'ATZ / MATZ',
    swatch: { color: '#84cc16', fill: '#84cc16', fillOpacity: 0.10 },
    hint: 'Aerodrome and Military Aerodrome Traffic Zones — small protected volumes around (military) airfields. Radio contact expected.' },
  { key: 'mandatory', label: 'TMZ / RMZ',
    swatch: { color: '#f59e0b', fill: '#f59e0b', fillOpacity: 0.09, dashArray: '4 2' },
    hint: 'Transponder Mandatory Zone and Radio Mandatory Zone — equipment/comms required to transit, but not controlled airspace.' },
  { key: 'gliding', label: 'Gliding',
    swatch: { color: '#a855f7', fill: '#a855f7', fillOpacity: 0.10 },
    hint: 'Active gliding and aerial-sporting areas. Expect concentrated non-radio traffic circling in lift.' },
  { key: 'other', label: 'Other',
    swatch: { color: '#6b7280', fill: '#6b7280', fillOpacity: 0.06 },
    hint: 'Airspaces that don’t fit the named groups — ADIZ, information zones (TIZ/TIA), VFR-specific structures, uncategorised entries.' },
];

const TYPE_TO_GROUP = {
  Prohibited: 'restricted', Restricted: 'restricted',
  TRA: 'restricted', TSA: 'restricted', Protected: 'restricted',
  Danger: 'danger', Warning: 'danger', Alert: 'danger',
  MTR: 'danger', MTA: 'danger', MRT: 'danger', TFR: 'danger',
  'Low Overflight': 'danger',
  CTR: 'controlled', CTA: 'controlled', TMA: 'controlled',
  MCTR: 'controlled', LTA: 'controlled', UTA: 'controlled',
  ATZ: 'atz_matz', MATZ: 'atz_matz', HTZ: 'atz_matz',
  TMZ: 'mandatory', RMZ: 'mandatory', ADIZ: 'mandatory',
  Airway: 'airways', FIR: 'airways', UIR: 'airways',
  ACC: 'airways', FIS: 'airways',
  Gliding: 'gliding', 'Aerial Sporting': 'gliding',
  // TRP / TIZ / TIA / VFR / Other fall through to 'other'.
};

const CLASS_TO_GROUP = {
  A: 'controlled', B: 'controlled', C: 'controlled', D: 'controlled',
  E: 'class_ef', F: 'class_ef',
  G: 'airways',
};

// Resolve an airspace to its group key. `type_name` wins (an explicit
// "Danger" wins over any ICAO class tag); `class` is the fallback for
// plain controlled airspace that doesn't carry a more specific type.
// Unknown → 'other', never dropped.
export function airspaceGroup(a) {
  const byType = a && a.type_name && TYPE_TO_GROUP[a.type_name];
  if (byType) return byType;
  const byClass = a && a.class && CLASS_TO_GROUP[a.class];
  if (byClass) return byClass;
  return 'other';
}
