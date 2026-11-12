import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../extension/vesper-companion.js', import.meta.url), 'utf8');
const atlas = readFileSync(new URL('../../extension/icons/vesper-companion-atlas.png', import.meta.url));

function fixture() {
  let now = 0, nextId = 0, frames = 0;
  const tasks = new Map();
  const sprites = [];
  const schedule = (callback, delay) => {
    const id = ++nextId;
    tasks.set(id, { callback, time: now + delay });
    return id;
  };
  const context = {
    performance: { now: () => now },
    setTimeout: schedule,
    clearTimeout: id => tasks.delete(id),
    requestAnimationFrame: callback => schedule(time => { frames++; callback(time); }, 16),
    cancelAnimationFrame: id => tasks.delete(id),
    Image: class {
      naturalWidth = atlas.readUInt32BE(16);
      naturalHeight = atlas.readUInt32BE(20);
    },
    document: { createElement: () => ({ style: {}, setAttribute() {}, remove() {}, getContext: () => ({
      clearRect() {}, drawImage(_image, x, y) { sprites.push(x / 80 + y / 80 * 10); }
    }) }) }
  };
  const button = { append() {}, style: {}, classList: new Set() };
  vm.createContext(context);
  vm.runInContext(source, context);
  const companion = context.VesperCompanion.create(button, 'fixture.png', false);
  companion.setPerch(500, 40, true, false);
  companion.setVisible(true);
  companion.atlas.onload();

  return { companion, button, sprites, tasks, frames: () => frames,
    reduce() { companion.setStill(true); },
    advance(ms) {
      const end = now + ms;
      while (tasks.size) {
        const [id, task] = [...tasks].sort((a, b) => a[1].time - b[1].time)[0];
        if (task.time > end) break;
        now = task.time;
        tasks.delete(id);
        task.callback(now);
      }
      now = end;
    }
  };
}

test('the supplied atlas uses less than 4 MiB decoded and a perched companion has no animation frames', () => {
  assert.equal(atlas.readUInt32BE(16), 800);
  assert.equal(atlas.readUInt32BE(20), 1200);
  assert.ok(800 * 1200 * 4 < 4 * 1024 * 1024);
  const f = fixture();
  f.advance(30_000);
  assert.deepEqual(f.sprites, [2]);
  assert.equal(f.frames(), 0);
  assert.equal(f.tasks.size, 0);
  assert.equal(f.companion.canvas.style.transform, 'scaleX(-1)');
  f.companion.dispose();
  assert.equal(f.tasks.size, 0);
});

test('opening the picker flies beside it, and a confirmed handoff returns to the perch', () => {
  const f = fixture();
  f.companion.setPickerOpen(true);
  f.advance(800);
  assert.equal(f.companion.mode, 'beside-picker');
  assert.match(f.button.style.transform, /466\.00px,22\.00px/);
  const flightFrames = f.frames();
  f.advance(400);
  assert.equal(f.frames(), flightFrames);
  assert.ok(f.sprites.some(frame => frame >= 23 && frame < 33));
  assert.equal(f.tasks.size, 0);

  f.companion.setPickerOpen(false, true);
  f.advance(3000);
  assert.ok(f.sprites.includes(83));
  assert.equal(f.companion.mode, 'perched');
  assert.match(f.button.style.transform, /500\.00px,40\.00px/);
});

test('rapid player moves settle on the latest perch and hidden or reduced-motion stops animation', () => {
  const f = fixture();
  f.companion.setPerch(450, 70, true, false);
  f.advance(40);
  f.companion.setPerch(410, 100, true, false);
  f.advance(80);
  assert.equal(f.frames(), 0);
  f.advance(200);
  assert.ok(f.frames() > 0);
  f.companion.setVisible(false);
  assert.equal(f.tasks.size, 0);
  f.companion.setVisible(true);
  f.reduce();
  f.companion.setPerch(360, 150, false, false);
  f.companion.setPickerOpen(true);
  assert.match(f.button.style.transform, /360\.00px,150\.00px/);
  assert.equal(f.tasks.size, 0);
});

test('moving over the side perch reacts once and still mode stops it immediately', () => {
  const f = fixture();
  f.companion.pointerMove(520, 61);
  assert.equal(f.companion.mode, 'reacting');
  f.advance(125);
  f.reduce();
  assert.equal(f.companion.mode, 'perched');
  assert.equal(f.sprites.at(-1), 2);
  assert.equal(f.tasks.size, 0);
  f.companion.setStill(false);
  f.companion.setPerch(482, 40, false, false);
  f.advance(160);
  assert.equal(f.companion.mode, 'walking');
  f.advance(1100);
  assert.equal(f.companion.mode, 'perched');
  assert.equal(f.companion.canvas.style.transform, '');
  assert.equal(f.tasks.size, 0);
});
