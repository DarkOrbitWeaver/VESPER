/* Motion and sprite timing adapted from VESPER Butterfly Companion v3.
 * The player overlay owns geometry, visibility, and the picker. This controller only draws the butterfly.
 */
globalThis.VesperCompanion = (() => {
  'use strict';

  const CELL = 80, COLUMNS = 10, SIZE = 44, FOOT_Y = 66 * SIZE / CELL;
  const clips = {
    perch_three_quarter: [1, 1, 0], perch_side: [2, 1, 0],
    hover_reaction: [3, 8, 50], takeoff: [23, 10, 50], flight_cycle: [33, 10, 82],
    bank_left: [43, 7, 86], bank_right: [50, 7, 86], approach: [57, 9, 84],
    touchdown: [66, 9, 78], wing_settle: [75, 8, 85], sent_acknowledgement: [83, 8, 82],
    fullscreen_arrive: [91, 7, 86], fullscreen_depart: [98, 7, 84],
    edge_walk: [117, 12, 72], edge_turn: [129, 6, 62]
  };
  const clamp = (value, min, max) => Math.min(max, Math.max(min, value));
  const curve = (t, start, end, speed, seconds) =>
    start + (end - start) * t * t * (3 - 2 * t) + speed * seconds * t * (1 - t) ** 2;
  const velocity = (t, start, end, speed, seconds) =>
    (end - start) * 6 * t * (1 - t) / seconds + speed * (1 - 4 * t + 3 * t * t);

  class Companion {
    constructor(button, atlasUrl, still) {
      this.button = button;
      this.still = still;
      this.canvas = document.createElement('canvas');
      this.canvas.width = this.canvas.height = CELL;
      this.canvas.setAttribute('aria-hidden', 'true');
      this.canvas.hidden = true;
      this.context = this.canvas.getContext('2d', { alpha: true });
      this.button.append(this.canvas);
      this.atlas = new Image();
      this.atlas.decoding = 'async';
      this.ready = this.visible = this.pickerOpen = false;
      this.frame = this.spriteTimer = this.retargetTimer = 0;
      this.near = false;
      this.clip = 'perch_side';
      this.lastSprite = -1;
      this.x = this.y = this.vx = this.vy = 0;
      if (!this.context) return;
      this.atlas.onload = () => {
        if (this.atlas.naturalWidth !== 800 || this.atlas.naturalHeight !== 1200) return;
        this.ready = true;
        this.canvas.hidden = false;
        this.button.classList.add('companion-ready');
        this.resetPose();
        if (this.pickerOpen && !this.still) this.moveToPerch();
      };
      this.atlas.src = atlasUrl;
    }

    setStill(still) {
      if (this.still === still) return;
      this.still = still;
      this.resetPose();
      if (this.pickerOpen && !still) this.moveToPerch();
    }

    setPerch(x, y, faceLeft, fullscreen) {
      const previous = this.perch;
      if (previous?.x === x && previous.y === y && previous.faceLeft === faceLeft && previous.fullscreen === fullscreen) return;
      this.perch = { x, y, faceLeft, fullscreen };
      if (!previous || !this.visible || !this.ready || this.still) return this.resetPose();
      clearTimeout(this.retargetTimer);
      const transition = previous.fullscreen === fullscreen ? undefined : fullscreen ? 'fullscreen_arrive' : 'fullscreen_depart';
      // Let rapid scroll and reel changes settle before selecting a new landing point.
      this.retargetTimer = setTimeout(() => {
        this.retargetTimer = 0;
        this.moveToPerch(transition);
      }, 100);
    }

    setVisible(visible) {
      if (this.visible === visible) return;
      this.visible = visible;
      this.resetPose();
      if (visible && this.pickerOpen && !this.still) this.moveToPerch();
    }

    setPickerOpen(open, acknowledged = false) {
      if (this.pickerOpen === open) return;
      this.pickerOpen = open;
      this.moveToPerch(acknowledged ? 'sent_acknowledgement' : undefined);
    }

    pointerMove(x, y) {
      const nearby = Math.hypot(x - this.x - SIZE / 2, y - this.y - SIZE / 2) < 46;
      const entering = nearby && !this.near;
      this.near = nearby;
      if (entering && this.visible && !this.pickerOpen && !this.still && this.mode === 'perched') this.react('hover_reaction');
    }

    moveToPerch(clip) {
      if (!this.perch) return;
      if (!this.visible || !this.ready || this.still) return this.resetPose();
      const x = this.pickerOpen ? Math.max(4, this.perch.x + (this.perch.faceLeft ? -34 : 34)) : this.perch.x;
      const y = this.pickerOpen ? Math.max(4, this.perch.y - 18) : this.perch.y;
      const distance = Math.hypot(x - this.x, y - this.y);
      if (distance < 2 && !this.flight && !this.walk) {
        if (this.pickerOpen) this.play('perch_three_quarter', 'beside-picker');
        else if (clip) this.react(clip);
        else this.rest();
        return;
      }
      this.face(x < this.x);
      if (!clip && !this.pickerOpen && this.mode === 'perched' && Math.abs(y - this.y) <= 2 && distance <= 28) {
        this.walk = { x, y, startX: this.x, start: performance.now() };
        this.play('edge_walk', 'walking', true);
        return;
      }
      this.flight = {
        x, y, startX: this.x, startY: this.y, vx: clamp(this.vx, -550, 550), vy: clamp(this.vy, -550, 550),
        start: performance.now(), seconds: clamp(.28 + distance / 700, .38, 1.34),
        arc: Math.min(55, 12 + distance * .065, Math.max(4, Math.min(y, this.y) - 3))
      };
      this.walk = null;
      this.play(clip || (this.mode === 'perched' ? 'takeoff' : x < this.x ? 'bank_left' : 'bank_right'), 'flying');
    }

    position(x, y) {
      this.x = x;
      this.y = y;
      this.button.style.transform = `translate3d(${x.toFixed(2)}px,${y.toFixed(2)}px,0)`;
    }

    face(left) { this.canvas.style.transform = left ? 'scaleX(-1)' : ''; }

    resetPose() {
      clearTimeout(this.retargetTimer);
      this.retargetTimer = 0;
      if (this.perch) this.position(this.perch.x, this.perch.y);
      this.near = false;
      this.rest();
    }

    rest() {
      this.flight = this.walk = null;
      this.vx = this.vy = 0;
      this.face(this.perch?.faceLeft);
      this.play('perch_side', 'perched');
    }

    react(clip) { this.play(clip, 'reacting', false, () => this.rest()); }

    play(clip, mode, repeat = false, after = null) {
      this.cancelAnimation();
      this.clip = clip;
      this.mode = mode;
      this.clipStart = performance.now();
      this.repeat = repeat;
      this.after = after;
      this.draw(0);
      this.scheduleFrame();
    }

    draw(offset) {
      const sprite = clips[this.clip][0] + offset;
      if (!this.ready || sprite === this.lastSprite) return;
      this.lastSprite = sprite;
      this.context.clearRect(0, 0, CELL, CELL);
      this.context.drawImage(this.atlas, (sprite % COLUMNS) * CELL, Math.floor(sprite / COLUMNS) * CELL,
        CELL, CELL, 0, 0, CELL, CELL);
    }

    scheduleFrame() {
      if (!this.ready || !this.visible || this.still || this.frame || this.spriteTimer) return;
      if (this.flight || this.walk) {
        this.frame = requestAnimationFrame(time => { this.frame = 0; this.tick(time); });
      } else if (clips[this.clip][1] > 1) {
        const interval = clips[this.clip][2];
        const delay = interval - Math.max(0, performance.now() - this.clipStart) % interval;
        this.spriteTimer = setTimeout(() => { this.spriteTimer = 0; this.tick(performance.now()); }, Math.max(4, delay));
      }
    }

    tick(time) {
      if (!this.visible || this.still) return this.resetPose();
      if (this.flight) this.advanceFlight(time);
      if (this.walk) {
        const walk = this.walk;
        const progress = clamp((time - walk.start) / 540, 0, 1);
        this.position(walk.startX + (walk.x - walk.startX) * progress, walk.y - Math.abs(Math.sin(progress * Math.PI * 4)) * .7);
        if (progress === 1) {
          this.walk = null;
          this.play('edge_turn', 'turning', false, () => this.rest());
        }
      }
      const [, count, interval] = clips[this.clip];
      if (count > 1) {
        const offset = Math.max(0, Math.floor((time - this.clipStart) / interval));
        if (this.repeat) this.draw(offset % count);
        else if (offset < count) this.draw(offset);
        else if (this.after) this.after();
        else if (this.flight) this.play('flight_cycle', 'flying', true);
        else this.rest();
      }
      this.scheduleFrame();
    }

    advanceFlight(time) {
      const flight = this.flight;
      const t = clamp((time - flight.start) / (flight.seconds * 1000), 0, 1);
      const arc = Math.sin(Math.PI * t);
      this.position(curve(t, flight.startX, flight.x, flight.vx, flight.seconds),
        curve(t, flight.startY, flight.y, flight.vy, flight.seconds) - flight.arc * arc ** 2);
      this.vx = velocity(t, flight.startX, flight.x, flight.vx, flight.seconds);
      this.vy = velocity(t, flight.startY, flight.y, flight.vy, flight.seconds)
        - flight.arc * Math.PI * Math.sin(2 * Math.PI * t) / flight.seconds;
      if (t === 1) {
        this.flight = null;
        if (this.pickerOpen) this.play('perch_three_quarter', 'beside-picker');
        else this.play('touchdown', 'landing', false, () => this.play('wing_settle', 'settling', false, () => this.rest()));
      } else if (t > .72 && this.clip !== 'approach') this.play('approach', 'flying');
    }

    cancelAnimation() {
      cancelAnimationFrame(this.frame);
      clearTimeout(this.spriteTimer);
      this.frame = this.spriteTimer = 0;
    }

    dispose() {
      this.cancelAnimation();
      clearTimeout(this.retargetTimer);
      this.atlas.onload = null;
      this.canvas.remove();
    }
  }

  return { footY: FOOT_Y, create: (button, atlasUrl, still) => new Companion(button, atlasUrl, still) };
})();
