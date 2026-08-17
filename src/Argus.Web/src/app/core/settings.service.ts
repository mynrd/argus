import { Injectable, effect, signal } from '@angular/core';

const STORAGE_KEY = 'argus.settings';

/** Auto lock is on by default: the whole point of the password is that walking away is safe. */
const DEFAULT_AUTO_LOCK = true;
const DEFAULT_IDLE_MINUTES = 10;

const MIN_IDLE_MINUTES = 1;
const MAX_IDLE_MINUTES = 240;

interface StoredSettings {
  autoLock?: boolean;
  idleMinutes?: number;
}

/**
 * Viewer preferences, kept in localStorage rather than on the server.
 *
 * They belong to the device holding the browser, not to the watched PC - a phone on the sofa wants
 * a shorter idle timeout than the desktop sitting in a locked office.
 */
@Injectable({ providedIn: 'root' })
export class SettingsService {
  readonly autoLock = signal(DEFAULT_AUTO_LOCK);
  readonly idleMinutes = signal(DEFAULT_IDLE_MINUTES);

  constructor() {
    this.load();
    effect(() => this.save({ autoLock: this.autoLock(), idleMinutes: this.idleMinutes() }));
  }

  /** Clamped on the way in, so a hand-edited localStorage cannot disable the lock with a huge number. */
  setIdleMinutes(minutes: number): void {
    const rounded = Math.round(Number(minutes));
    if (!Number.isFinite(rounded)) return;

    this.idleMinutes.set(Math.min(MAX_IDLE_MINUTES, Math.max(MIN_IDLE_MINUTES, rounded)));
  }

  private load(): void {
    try {
      const raw = localStorage.getItem(STORAGE_KEY);
      if (!raw) return;

      const stored = JSON.parse(raw) as StoredSettings;
      if (typeof stored.autoLock === 'boolean') this.autoLock.set(stored.autoLock);
      if (typeof stored.idleMinutes === 'number') this.setIdleMinutes(stored.idleMinutes);
    } catch {
      // Corrupt or unavailable storage (private browsing) just means the defaults.
    }
  }

  private save(settings: StoredSettings): void {
    try {
      localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
    } catch {
      // Not being able to remember the preference is not worth breaking the page over.
    }
  }
}
