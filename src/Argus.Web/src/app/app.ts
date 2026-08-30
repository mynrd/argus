import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  effect,
  inject,
  signal,
} from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ArgusService } from './core/argus.service';
import { SessionService } from './core/session.service';
import { LockComponent } from './pages/lock.component';

/** How long the Release keys result stays on screen. Long enough to read, short enough to ignore. */
const NOTE_MS = 4000;

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, LockComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit, OnDestroy {
  protected readonly argus = inject(ArgusService);
  protected readonly session = inject(SessionService);

  private readonly title = inject(Title);

  /** In flight, so a double tap does not fire two release passes. */
  protected readonly releasing = signal(false);

  /**
   * What the last release pass found, shown next to the error banner. Not folded into lastError:
   * "Nothing was stuck" is a normal answer, and the error banner is red.
   */
  protected readonly releaseNote = signal<string | null>(null);

  private noteTimer?: ReturnType<typeof setTimeout>;

  constructor() {
    // The connections follow the lock rather than the page load. Connecting while locked would be
    // pointless anyway - the server refuses the hub and the frame socket without a session.
    effect(() => {
      if (this.session.unlocked()) void this.argus.start();
      else this.argus.stop();
    });

    // Which machine this tab is driving, in the place you can read without switching to it. The
    // host is only known once /api/session answers, so the bare name is what shows until then.
    effect(() => {
      const host = this.session.host();
      this.title.setTitle(host ? `${host} - Argus` : 'Argus');
    });
  }

  ngOnInit(): void {
    void this.session.refresh();
  }

  protected lock(): void {
    void this.session.lock();
  }

  /**
   * Lifts every modifier off the host keyboard.
   *
   * The escape hatch for the case nothing else covers: a viewer that locked Ctrl and then closed
   * its tab, or a combo that half-arrived, leaves the key physically down on the machine and turns
   * every later keystroke over there into a shortcut. What it found is worth saying either way -
   * "Nothing was stuck" means the problem is somewhere else.
   */
  protected async releaseKeys(): Promise<void> {
    if (this.releasing()) return;
    this.releasing.set(true);

    try {
      const result = await this.argus.releaseKeys();

      if (result.reason) {
        this.argus.lastError.set(result.reason);
        this.showNote(null);
      } else {
        this.showNote(
          result.released.length > 0
            ? `Released ${result.released.join(', ')}`
            : 'Nothing was stuck',
        );
      }
    } finally {
      this.releasing.set(false);
    }
  }

  private showNote(note: string | null): void {
    clearTimeout(this.noteTimer);
    this.releaseNote.set(note);
    if (note) this.noteTimer = setTimeout(() => this.releaseNote.set(null), NOTE_MS);
  }

  ngOnDestroy(): void {
    clearTimeout(this.noteTimer);
  }
}
