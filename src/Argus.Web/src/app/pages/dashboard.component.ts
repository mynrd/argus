import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { RouterLink } from '@angular/router';
import { FrameViewComponent } from '../core/frame-view.component';
import { ArgusService } from '../core/argus.service';
import {
  BrowseEntry,
  BrowseListing,
  QualityLevel,
  STATUS_LABELS,
  WindowListItem,
  WindowStatus,
  WindowStatusUpdate,
} from '../core/models';

/**
 * The fields close and kill need. Both lists on this page supply them - a watched tile carries a
 * WindowStatusUpdate, an available row carries a WindowListItem - so the actions take neither.
 */
type AppRef = Pick<WindowListItem, 'handle' | 'title' | 'processName'>;

/**
 * Tile grid of every attached window. Every tile subscribes at Preview quality - small and slow on
 * purpose, because all of them stream at once.
 */
@Component({
  selector: 'argus-dashboard',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, FrameViewComponent],
  templateUrl: './dashboard.component.html',
  styleUrl: './dashboard.component.scss',
})
export class DashboardComponent implements OnInit, OnDestroy {
  protected readonly argus = inject(ArgusService);
  private subscribed = new Set<number>();

  protected readonly command = signal('');
  protected readonly running = signal(false);
  protected readonly runError = signal<string | null>(null);
  protected readonly browsing = signal(false);
  protected readonly killTarget = signal<AppRef | null>(null);
  protected readonly listing = signal<BrowseListing | null>(null);

  protected readonly tiles = computed(() =>
    [...this.argus.statuses().values()].sort((a, b) =>
      a.processName.localeCompare(b.processName) || a.title.localeCompare(b.title),
    ),
  );

  /** Open windows nobody asked to watch yet - the pick list under the tiles. */
  protected readonly available = computed(() =>
    this.argus
      .windows()
      .filter((w) => !w.selected)
      .sort((a, b) => a.processName.localeCompare(b.processName) || a.title.localeCompare(b.title)),
  );

  constructor() {
    // Keep subscriptions in step with whatever is attached, including windows the watchdog
    // re-attaches under a new handle while the dashboard is open.
    effect(() => {
      const wanted = new Set(this.tiles().map((t) => t.handle));

      for (const handle of wanted) {
        if (!this.subscribed.has(handle)) {
          this.subscribed.add(handle);
          void this.argus.subscribe(handle, QualityLevel.Preview);
        }
      }

      for (const handle of [...this.subscribed]) {
        if (!wanted.has(handle)) {
          this.subscribed.delete(handle);
          void this.argus.unsubscribe(handle);
        }
      }
    });
  }

  ngOnInit(): void {
    void this.argus.refreshStatuses();
    void this.argus.refreshWindows();
  }

  ngOnDestroy(): void {
    for (const handle of this.subscribed) void this.argus.unsubscribe(handle);
    this.subscribed.clear();
  }

  protected statusClass(status: WindowStatus): string {
    return WindowStatus[status].toLowerCase();
  }

  protected statusLabel(status: WindowStatus): string {
    return STATUS_LABELS[status] ?? 'Unknown';
  }

  protected isLive(tile: WindowStatusUpdate): boolean {
    return tile.status === WindowStatus.Streaming;
  }

  protected placeholderFor(tile: WindowStatusUpdate): string {
    switch (tile.status) {
      case WindowStatus.Closed:
        return 'Not running - waiting for it to reopen';
      case WindowStatus.Minimised:
        return 'Minimised';
      case WindowStatus.Stalled:
        return 'Capture stalled';
      default:
        return 'Waiting for frames…';
    }
  }

  /** Attaches straight away - the tile appears at the top and the row leaves this list. */
  protected async watch(window: WindowListItem): Promise<void> {
    await this.argus.attach(window.handle);
  }

  /** Polite close. No confirmation: the app gets to put up its own save prompt. */
  protected async closeApp(window: AppRef): Promise<void> {
    this.runError.set(null);
    const result = await this.argus.closeWindow(window.handle);

    if (!result.closed) {
      this.runError.set(result.reason ?? `Could not close ${window.title}.`);
      return;
    }

    // No automatic re-read here. WM_CLOSE is only posted, so an app with unsaved work is still on
    // screen with a prompt - refreshing a second later would put the row straight back and read
    // as a bug. It returns on the next Refresh, which is honest: it did not close.
    await this.forget(window.handle);
  }

  protected askKill(window: AppRef): void {
    this.killTarget.set({
      handle: window.handle,
      title: window.title,
      processName: window.processName,
    });
  }

  protected cancelKill(): void {
    this.killTarget.set(null);
  }

  protected async confirmKill(window: AppRef): Promise<void> {
    this.killTarget.set(null);
    this.runError.set(null);

    const result = await this.argus.killWindow(window.handle);
    if (!result.closed) {
      this.runError.set(result.reason ?? `Could not kill ${window.title}.`);
      return;
    }

    await this.forget(window.handle);

    // The process is gone for certain, so a re-read can only agree.
    await this.argus.refreshWindows();
  }

  /**
   * Takes a closed or killed app off both lists at once.
   *
   * Detaching is not optional for a watched tile: a selection persists by process and title, so
   * without it the watchdog keeps the tile on the dashboard reading "Not running - waiting for it
   * to reopen", which is not what closing something is meant to look like. Dropping it from the
   * window list covers the other half, the row under Available applications.
   */
  private async forget(handle: number): Promise<void> {
    if (this.argus.statuses().has(handle)) {
      await this.argus.detach(handle);
    }

    this.argus.forgetWindow(handle);
  }

  protected async refreshWindows(): Promise<void> {
    await this.argus.refreshWindows();
  }

  // ------------------------------------------------------------- run and browse

  protected async run(event: Event): Promise<void> {
    event.preventDefault();

    const command = this.command().trim();
    if (!command || this.running()) return;

    this.running.set(true);
    this.runError.set(null);

    const result = await this.argus.runApplication(command);
    this.running.set(false);

    if (!result.started) {
      this.runError.set(result.reason ?? 'Could not run that.');
      return;
    }

    this.command.set('');

    // The new window does not exist the instant the process starts, so give it a moment before
    // refreshing - otherwise the available list below still will not show it.
    setTimeout(() => void this.argus.refreshWindows(), 1500);
  }

  protected async openBrowse(): Promise<void> {
    this.browsing.set(true);
    this.listing.set(null);
    await this.load(null);
  }

  protected closeBrowse(): void {
    this.browsing.set(false);
  }

  /** A folder descends; a file fills the box so you can see what you picked before running it. */
  protected async choose(entry: BrowseEntry): Promise<void> {
    if (entry.isDirectory) {
      await this.load(entry.path);
      return;
    }

    // Quoted because Program Files and friends contain spaces, and the server splits the command
    // into program and arguments on the first unquoted space.
    this.command.set(entry.path.includes(' ') ? `"${entry.path}"` : entry.path);
    this.runError.set(null);
    this.browsing.set(false);
  }

  protected async goUp(): Promise<void> {
    await this.load(this.listing()?.parent ?? null);
  }

  private async load(path: string | null): Promise<void> {
    this.listing.set(await this.argus.browse(path));
  }

  /** Stops watching. The app itself carries on running. */
  protected async detach(handle: number): Promise<void> {
    await this.argus.detach(handle);
  }
}
