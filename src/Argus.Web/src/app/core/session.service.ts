import { Injectable, computed, inject, signal } from '@angular/core';
import { SettingsService } from './settings.service';

/** Events that count as the user still being there. */
const ACTIVITY_EVENTS = ['pointerdown', 'pointermove', 'keydown', 'wheel', 'touchstart'] as const;

/**
 * How often the idle check runs. A single interval beats resetting a timer on every pointermove,
 * and it keeps working when a backgrounded tab has its timers throttled.
 */
const IDLE_TICK_MS = 5000;

interface SessionResponse {
  required: boolean;
  unlocked: boolean;
  host: string;
}

interface UnlockResponse {
  unlocked: boolean;
  reason?: string;
}

/**
 * Whether this browser is allowed to reach the host, and the idle clock that takes it away again.
 *
 * The server is the authority - it rejects the hub, the frame socket and every API call without a
 * session cookie. What lives here is only what the UI needs in order to show the right screen.
 */
@Injectable({ providedIn: 'root' })
export class SessionService {
  private readonly settings = inject(SettingsService);

  /** True once /api/session has answered, so the shell does not flash the lock screen on load. */
  readonly ready = signal(false);
  readonly required = signal(false);
  readonly unlocked = signal(false);

  /** The machine Argus is running on, as Windows names it. Empty until /api/session answers. */
  readonly host = signal('');

  readonly locked = computed(() => this.ready() && this.required() && !this.unlocked());

  private lastActivity = Date.now();

  constructor() {
    // Capture phase: activity counts even when a component stops the event from bubbling.
    for (const type of ACTIVITY_EVENTS) {
      window.addEventListener(type, this.onActivity, { passive: true, capture: true });
    }

    // The server may have restarted, or the session may have aged out, while the tab was hidden.
    document.addEventListener('visibilitychange', () => {
      if (document.visibilityState === 'visible') void this.refresh();
    });

    // Never cleared: this service is rooted for the lifetime of the page.
    setInterval(() => this.checkIdle(), IDLE_TICK_MS);
  }

  async refresh(): Promise<void> {
    try {
      const response = await fetch('/api/session', { credentials: 'same-origin' });
      const state = (await response.json()) as SessionResponse;

      this.required.set(state.required);
      this.unlocked.set(state.unlocked);
      this.host.set(state.host ?? '');
      if (state.unlocked) this.lastActivity = Date.now();
    } catch {
      // Cannot reach the agent at all. Claiming it is unlocked would be a lie, but claiming it is
      // locked would hide the connection error behind a password box - leave the last known state.
    } finally {
      this.ready.set(true);
    }
  }

  /** Returns null on success, or the reason to show under the password box. */
  async unlock(password: string): Promise<string | null> {
    try {
      const response = await fetch('/api/unlock', {
        method: 'POST',
        credentials: 'same-origin',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ password }),
      });

      const result = (await response.json()) as UnlockResponse;
      if (!result.unlocked) return result.reason ?? 'Wrong password';

      this.lastActivity = Date.now();
      this.required.set(true);
      this.unlocked.set(true);
      return null;
    } catch {
      return 'Cannot reach the Argus agent';
    }
  }

  async lock(): Promise<void> {
    // The lock screen is fixed to the viewport, and only the fullscreen element's own subtree
    // paints while something is fullscreen - locking the viewer mid-fullscreen would otherwise
    // leave the desktop on screen with the lock drawn nowhere.
    if (document.fullscreenElement) await document.exitFullscreen().catch(() => undefined);

    // Locked locally first and regardless of the response: if the request failed, the last thing
    // to do is leave the desktop on screen while waiting to find out why.
    this.unlocked.set(false);

    try {
      await fetch('/api/lock', { method: 'POST', credentials: 'same-origin' });
    } catch {
      // The server drops the session when it next sees the cookie is gone, or on restart.
    }
  }

  private readonly onActivity = (): void => {
    this.lastActivity = Date.now();
  };

  private checkIdle(): void {
    if (!this.unlocked() || !this.required() || !this.settings.autoLock()) return;

    const idleFor = Date.now() - this.lastActivity;
    if (idleFor >= this.settings.idleMinutes() * 60_000) void this.lock();
  }
}
