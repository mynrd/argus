import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  OnInit,
  computed,
  effect,
  inject,
  signal,
} from '@angular/core';
import { ArgusService } from '../core/argus.service';
import { PortAddress, PortEntry, PortIdentity } from '../core/models';

/**
 * Every TCP port the watched PC is listening on, and a one-tap link to each one.
 *
 * The point is the address list under each port: the same service is a different URL depending on
 * whether you are on the tailnet, on the LAN, or sitting at the machine, and only the host knows
 * which of those a given socket actually answers on.
 */
@Component({
  selector: 'argus-ports',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet],
  templateUrl: './ports.component.html',
  styleUrl: './ports.component.scss',
})
export class PortsComponent implements OnInit {
  private readonly argus = inject(ArgusService);

  protected readonly ports = signal<PortEntry[]>([]);
  protected readonly loading = signal(false);

  /** Which ports are open. A set rather than one id, so several can be compared side by side. */
  protected readonly expanded = signal<ReadonlySet<number>>(new Set());

  /** What each port answered when asked for its home page. */
  protected readonly identities = signal<ReadonlyMap<number, PortIdentity>>(new Map());
  protected readonly probing = signal<ReadonlySet<number>>(new Set());

  /**
   * Ports a probe has already been started for. A plain set rather than a signal: the effect that
   * fires the probes must not re-run every time one of them lands.
   */
  private readonly probed = new Set<number>();

  /** Windows plumbing is hidden by default - it is most of the list and none of the interest. */
  protected readonly showSystem = signal(false);

  /** Ports struck off by hand. Off by default, which is the whole point of striking them off. */
  protected readonly showHidden = signal(false);

  protected readonly query = signal('');

  /**
   * The rows the two filters allow, before the search box has its say.
   *
   * Kept separate from what is drawn because this is also what gets probed: searching by title
   * only works if the titles were fetched, and they cannot be fetched for rows the search has
   * already hidden. A struck-off port is not probed until you ask to see it - the point of hiding
   * it was to stop spending an HTTP request on it every refresh.
   */
  private readonly allowed = computed(() =>
    this.ports().filter(
      (p) =>
        p.isFavourite || ((this.showSystem() || !p.isSystem) && (this.showHidden() || !p.isHidden)),
    ),
  );

  /**
   * What the search box looks through. Typing a query pulls the system and hidden ports back in:
   * searching "445" and being told nothing matches reads as "that port is closed", which is a lie.
   *
   * They are still not probed, so a filtered-out port is findable by number and process but not by
   * title - Windows services are not worth an HTTP request each on the off chance.
   */
  private readonly searchable = computed(() =>
    this.query().trim() ? this.ports() : this.allowed(),
  );

  /** Pinned ports, in their own list at the top. The system filter does not apply: you asked. */
  protected readonly favourites = computed(() =>
    this.searchable().filter((p) => p.isFavourite && this.matches(p)),
  );

  /** Everything else. A pinned port lives in one list only, never both. */
  protected readonly rest = computed(() =>
    this.searchable().filter((p) => !p.isFavourite && this.matches(p)),
  );

  // Each count is what its own button would reveal, so the two never claim the same row: a system
  // port that was also struck off is only ever offered by the hidden button.
  protected readonly systemCount = computed(
    () => this.ports().filter((p) => p.isSystem && !p.isFavourite && !p.isHidden).length,
  );

  protected readonly hiddenCount = computed(() => this.ports().filter((p) => p.isHidden).length);

  constructor() {
    // Every row on screen gets identified as soon as it is on screen. The page title is the label
    // that actually tells you what a port is - "node" does not - so waiting for a tap to fetch it
    // meant the useful half of the row only appeared after you had already guessed right.
    //
    // The cost is one HTTP request per visible port, including the ones that are not web servers
    // at all. It re-runs when the system filter is flipped, so those are only touched if you ask.
    effect(() => {
      for (const port of this.allowed()) {
        if (port.isListening) void this.probe(port.port);
      }
    });
  }

  ngOnInit(): void {
    // No wait on the hub: the whole page is plain HTTP now, so it loads whether or not a stream
    // is up.
    void this.refresh();
  }

  protected async refresh(): Promise<void> {
    // A port that went away should not keep its old title if the number is reused by something
    // else. Cleared before the fetch so the effect re-probes whatever comes back; the host caches
    // its answers for 30s, so a refresh does not mean 30 fresh HTTP requests.
    this.probed.clear();
    this.identities.set(new Map());
    this.probing.set(new Set());

    this.loading.set(true);
    try {
      this.ports.set(await this.argus.listPorts());
    } finally {
      this.loading.set(false);
    }
  }

  /**
   * Pins or unpins a port. The host owns the list, so the answer replaces what is on screen rather
   * than being patched in locally - unpinning a port that is not listening removes its row
   * entirely, and pinning one adds a row that was never in the scan.
   */
  protected async toggleFavourite(entry: PortEntry, event: Event): Promise<void> {
    event.stopPropagation();
    this.ports.set(await this.argus.setFavouritePort(entry.port, !entry.isFavourite));
  }

  /**
   * Strikes a port off the list, or brings it back. Hiding a pinned port unpins it on the host, so
   * again the answer replaces the list rather than being patched in.
   */
  protected async toggleHidden(entry: PortEntry, event: Event): Promise<void> {
    event.stopPropagation();
    this.ports.set(await this.argus.setHiddenPort(entry.port, !entry.isHidden));
  }

  /**
   * Whether a row survives the search box. Matches the port number, the process, and the title -
   * "5081" and "work hub" should both find the same row, since which one you remember varies.
   */
  private matches(entry: PortEntry): boolean {
    const needle = this.query().trim().toLowerCase();
    if (!needle) return true;

    return (
      String(entry.port).includes(needle) ||
      entry.process.toLowerCase().includes(needle) ||
      (this.identity(entry.port)?.title?.toLowerCase().includes(needle) ?? false)
    );
  }

  protected isExpanded(port: number): boolean {
    return this.expanded().has(port);
  }

  protected toggle(port: PortEntry): void {
    const next = new Set(this.expanded());

    if (next.has(port.port)) next.delete(port.port);
    else next.add(port.port);

    this.expanded.set(next);
  }

  protected identity(port: number): PortIdentity | undefined {
    return this.identities().get(port);
  }

  protected isProbing(port: number): boolean {
    return this.probing().has(port);
  }

  /** The link for one address. Falls back to http until the probe says which scheme answered. */
  protected url(port: PortEntry, address: PortAddress): string {
    const scheme = this.identity(port.port)?.scheme ?? 'http';
    return `${scheme}://${address.ip}:${port.port}/`;
  }

  protected open(url: string): void {
    window.open(url, '_blank', 'noopener');
  }

  private async probe(port: number): Promise<void> {
    if (this.probed.has(port)) return;
    this.probed.add(port);

    this.probing.update((set) => new Set(set).add(port));
    try {
      const identity = await this.argus.probePort(port);
      this.identities.update((map) => new Map(map).set(port, identity));
    } finally {
      this.probing.update((set) => {
        const next = new Set(set);
        next.delete(port);
        return next;
      });
    }
  }
}
