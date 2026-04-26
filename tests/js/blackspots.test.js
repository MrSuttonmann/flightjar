import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  ALT_STOPS_M, DEFAULT_STOP_INDEX, bandFor, blockerShade,
  blockerTooltipFor, flLabel, tooltipFor,
} from '../../app/static/blackspots_format.js';

test('bandFor picks the right band by threshold', () => {
  assert.equal(bandFor(0).fill, '#fde68a');
  assert.equal(bandFor(20).fill, '#fde68a');
  assert.equal(bandFor(21).fill, '#fdba74');
  assert.equal(bandFor(50).fill, '#fdba74');
  assert.equal(bandFor(51).fill, '#f87171');
  assert.equal(bandFor(100).fill, '#f87171');
  assert.equal(bandFor(101).fill, '#7c3aed');
});

test('bandFor treats null required-height as "unreachable"', () => {
  assert.equal(bandFor(null).fill, '#7c3aed');
});

test('tooltipFor reports required MSL antenna and the AGL delta', () => {
  // Receiver at 40 m ground + 10 m AGL antenna = 50 m MSL. Required 82 m MSL.
  const cell = { lat: 51.5, lon: -0.1, required_antenna_msl_m: 82.4 };
  const params = {
    target_altitude_m: 3048, antenna_msl_m: 50, ground_elevation_m: 40, max_agl_m: 100,
  };
  const tip = tooltipFor(cell, params);
  assert.match(tip, /Blind spot/);
  assert.match(tip, /FL100/);
  assert.match(tip, /≥ 82 m MSL/);
  assert.match(tip, /\+32 m/);
  assert.match(tip, /50 m MSL \(10 m AGL\)/);
});

test('tooltipFor describes unreachable cells with the absolute ceiling', () => {
  const cell = { lat: 51.5, lon: -0.1, required_antenna_msl_m: null };
  const params = {
    target_altitude_m: 3048, antenna_msl_m: 50, ground_elevation_m: 40, max_agl_m: 100,
  };
  const tip = tooltipFor(cell, params);
  assert.match(tip, /Unreachable/);
  assert.match(tip, /FL100/);
  // Ceiling is ground + max_agl = 40 + 100 = 140 m MSL.
  assert.match(tip, /≤ 140 m MSL/);
});

test('tooltipFor handles non-FL100 altitudes', () => {
  // 5000 m MSL ≈ 16404 ft ≈ FL164
  const cell = { lat: 51.5, lon: -0.1, required_antenna_msl_m: 10 };
  const params = {
    target_altitude_m: 5000, antenna_msl_m: 5, ground_elevation_m: 0, max_agl_m: 100,
  };
  const tip = tooltipFor(cell, params);
  assert.match(tip, /FL164/);
});

test('flLabel renders metres as a zero-padded flight level', () => {
  assert.equal(flLabel(914), 'FL030');
  assert.equal(flLabel(3048), 'FL100');
  assert.equal(flLabel(12192), 'FL400');
});

test('flLabel renders the 0 ground-level sentinel as GND', () => {
  assert.equal(flLabel(0), 'GND');
});

test('ALT_STOPS_M default index maps to FL100', () => {
  assert.equal(flLabel(ALT_STOPS_M[DEFAULT_STOP_INDEX]), 'FL100');
});

test('ALT_STOPS_M includes GND and FL250 stops', () => {
  assert.equal(ALT_STOPS_M[0], 0);
  assert.ok(ALT_STOPS_M.includes(7620), 'FL250 stop missing');
});

test('ALT_STOPS_M is strictly increasing', () => {
  for (let i = 1; i < ALT_STOPS_M.length; i++) {
    assert.ok(ALT_STOPS_M[i] > ALT_STOPS_M[i - 1],
      `stop ${i} (${ALT_STOPS_M[i]}) should exceed stop ${i - 1} (${ALT_STOPS_M[i - 1]})`);
  }
});

test('blockerShade uses neutral grey and scales opacity with blocked count', () => {
  const s1 = blockerShade(1);
  const s8 = blockerShade(8);
  const s64 = blockerShade(64);
  const s10000 = blockerShade(10000);
  // Neutral grey across the board — colour stays constant, opacity carries
  // the magnitude. No competing hue with the coloured shadow rectangles.
  assert.equal(s1.fillColor, '#1f2937');
  assert.equal(s64.fillColor, '#1f2937');
  // Opacity scales monotonically with the cell count.
  assert.ok(s8.fillOpacity > s1.fillOpacity);
  assert.ok(s64.fillOpacity > s8.fillOpacity);
  // And caps at 0.55 so very prominent ridges don't render as a black hole.
  assert.ok(s10000.fillOpacity <= 0.55);
  // Stroke is suppressed — adjacent blocker bins should read as one
  // continuous shaded region, not a grid.
  assert.equal(s1.weight, 0);
});

test('blockerTooltipFor describes the obstruction with elevation and cell count', () => {
  const blocker = { lat: 51.5, lon: -1.4, blocked_count: 7, max_elev_msl_m: 423.6 };
  const params = { target_altitude_m: 3048, antenna_msl_m: 50, ground_elevation_m: 40, max_agl_m: 100 };
  const tip = blockerTooltipFor(blocker, params);
  assert.match(tip, /Obstruction at 424 m MSL/);
  assert.match(tip, /Blocking 7 cells/);
  assert.match(tip, /FL100/);
});

test('blockerTooltipFor uses singular cell label for single-cell offenders', () => {
  const blocker = { lat: 51.5, lon: -1.4, blocked_count: 1, max_elev_msl_m: 200 };
  const params = { target_altitude_m: 305, antenna_msl_m: 5, ground_elevation_m: 0, max_agl_m: 100 };
  const tip = blockerTooltipFor(blocker, params);
  assert.match(tip, /Blocking 1 cell\b/);  // word-boundary so "cells" doesn't match
});
