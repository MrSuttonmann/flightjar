import { test } from 'node:test';
import assert from 'node:assert/strict';

import { initAirportTooltip } from '../../app/static/tooltip.js';

// Tiny document stub. We record listeners so tests can fire synthetic
// events, and createElement returns objects that look enough like DOM
// nodes for initAirportTooltip to work with.
function fakeDocument() {
  const body = {
    children: [],
    appendChild(c) { this.children.push(c); c.parentNode = this; },
  };
  const listeners = new Map();
  return {
    body,
    createElement(tag) {
      return {
        tag,
        children: [],
        style: {},
        hidden: false,
        textContent: '',
        offsetWidth: 80,
        offsetHeight: 20,
        appendChild(c) { this.children.push(c); c.parentNode = this; },
        getBoundingClientRect: () => ({
          left: 50, top: 50, right: 100, bottom: 70, width: 50, height: 20,
        }),
      };
    },
    addEventListener(type, handler, useCapture) {
      if (!listeners.has(type)) listeners.set(type, []);
      listeners.get(type).push({ handler, useCapture });
    },
    fire(type, event) {
      for (const { handler } of listeners.get(type) || []) handler(event);
    },
  };
}

function fakeTarget(name) {
  // Fakes enough of an airport-code span for closest() to match.
  return {
    tag: 'span',
    dataset: { title: name },
    getBoundingClientRect: () => ({
      left: 100, top: 60, right: 140, bottom: 80, width: 40, height: 20,
    }),
    matches(sel) { return sel === '.airport-code[data-title]'; },
    closest(sel) { return this.matches(sel) ? this : null; },
  };
}

function bareTarget() {
  return { matches: () => false, closest: () => null };
}

test('initAirportTooltip mounts a hidden #airport-tooltip div on body', () => {
  const doc = fakeDocument();
  initAirportTooltip(doc);
  assert.equal(doc.body.children.length, 1);
  const tip = doc.body.children[0];
  assert.equal(tip.tag, 'div');
  assert.equal(tip.hidden, true);
});

test('Clicking an airport code shows the tooltip with the country name', () => {
  const doc = fakeDocument();
  initAirportTooltip(doc);
  const tip = doc.body.children[0];
  const target = fakeTarget('London Heathrow');
  let stopped = false;
  doc.fire('click', { target, stopPropagation: () => { stopped = true; } });
  assert.equal(tip.hidden, false);
  assert.equal(tip.textContent, 'London Heathrow');
  // Click on an airport code swallows the event so the enclosing row
  // doesn't also fire its click handler.
  assert.equal(stopped, true);
});

test('Clicking outside an airport code hides an open tooltip', () => {
  const doc = fakeDocument();
  initAirportTooltip(doc);
  const tip = doc.body.children[0];
  // Open it first.
  doc.fire('click', {
    target: fakeTarget('Paris CDG'),
    stopPropagation: () => {},
  });
  assert.equal(tip.hidden, false);
  // Now click away.
  doc.fire('click', { target: bareTarget(), stopPropagation: () => {} });
  assert.equal(tip.hidden, true);
});

test('Hovering an airport code shows the tooltip (non-persistent)', () => {
  const doc = fakeDocument();
  initAirportTooltip(doc);
  const tip = doc.body.children[0];
  doc.fire('mouseover', { target: fakeTarget('New York JFK') });
  assert.equal(tip.hidden, false);
  assert.equal(tip.textContent, 'New York JFK');
  // Pointer leaves → tooltip hides again.
  doc.fire('mouseout', { target: fakeTarget('New York JFK') });
  assert.equal(tip.hidden, true);
});
