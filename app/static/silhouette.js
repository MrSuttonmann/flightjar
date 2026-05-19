// Pure resolver for the tar1090 silhouette fallback chain. Lives apart
// from icons.js so it can be unit-tested without a Leaflet stub.
//
// `resolveSilhouette` mirrors tar1090's getBaseMarker fallback (minus the
// halloween / ATC-style branches we don't carry). Given an aircraft's
// type designator, ADS-B category + set letter, and the four lookup
// tables, returns the [shapeName, scaleFactor] entry that the marker
// renderer should use, or null when nothing matches and the caller
// should draw the generic arrow.
//
// Tables (all four are optional — pass null when one isn't loaded yet):
//   - typeDesignatorIcons: map<TYPE, [shape, scale]>            (e.g. "B738")
//   - typeDescriptionIcons: map<DESC | DESC-WTC | KIND, [shape, scale]>
//                                                               (e.g. "L1P", "L2J-M", "H")
//   - categoryIcons:       map<SET+CAT, [shape, scale]>         (e.g. "A1")
//   - typeDesignatorMeta:  map<TYPE, { desc, wtc }>             (e.g. C172 -> { desc:"L1P", wtc:"L" })

export function resolveSilhouette({
  typeIcao,
  category,
  categorySet,
  typeDesignatorIcons,
  typeDescriptionIcons,
  categoryIcons,
  typeDesignatorMeta,
}) {
  // 1. Most specific: ICAO type designator.
  if (typeDesignatorIcons && typeIcao) {
    const entry = typeDesignatorIcons[typeIcao.toUpperCase()];
    if (entry) return entry;
  }
  // 2. ICAO type description ("L1P", "L2J", "H1T", ...) ± wake turbulence.
  if (typeDescriptionIcons && typeDesignatorMeta && typeIcao) {
    const meta = typeDesignatorMeta[typeIcao.toUpperCase()];
    const desc = meta?.desc;
    const wtc = meta?.wtc;
    if (desc && desc.length === 3) {
      if (wtc && wtc.length === 1) {
        const withWtc = typeDescriptionIcons[`${desc}-${wtc}`];
        if (withWtc) return withWtc;
      }
      const bare = typeDescriptionIcons[desc];
      if (bare) return bare;
      const basic = typeDescriptionIcons[desc.charAt(0)];
      if (basic) return basic;
    }
  }
  // 3. ADS-B emitter category. The set letter ("A"/"B"/"C") came from the
  // BDS-08 typecode in the backend; default to "A" when the backend
  // didn't capture it (older snapshots, peer aircraft from a pre-field
  // build) since A is by far the most common set.
  if (categoryIcons && category != null && category > 0) {
    const setLetter = (categorySet || 'A').toString().toUpperCase();
    const entry = categoryIcons[`${setLetter}${category}`];
    if (entry) return entry;
  }
  return null;
}
