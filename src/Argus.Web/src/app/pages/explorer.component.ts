import { ChangeDetectionStrategy, Component, OnInit, computed, inject, signal } from '@angular/core';
import { ArgusService } from '../core/argus.service';
import { BrowseEntry, BrowseListing, OpenWithApp } from '../core/models';

/** The entry the app picker is currently open for. */
interface PickerTarget {
  entry: BrowseEntry;
}

/**
 * Browses the host's filesystem and hands a file or folder to an app on that machine.
 *
 * Everything here happens on the watched PC, not on the device holding the browser - this is the
 * only page where that distinction matters constantly, so paths are shown in full.
 */
@Component({
  selector: 'argus-explorer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './explorer.component.html',
  styleUrl: './explorer.component.scss',
})
export class ExplorerComponent implements OnInit {
  private readonly argus = inject(ArgusService);

  protected readonly listing = signal<BrowseListing | null>(null);
  protected readonly roots = signal<BrowseEntry[]>([]);
  protected readonly apps = signal<OpenWithApp[]>([]);
  protected readonly picker = signal<PickerTarget | null>(null);
  protected readonly error = signal<string | null>(null);
  protected readonly loading = signal(false);

  protected readonly entries = computed(() => this.listing()?.entries ?? []);
  protected readonly atRoot = computed(() => !this.listing()?.path);

  /** Only the apps that make sense for whatever the picker was opened on. */
  protected readonly pickerApps = computed(() => {
    const target = this.picker();
    if (!target) return [];

    return this.apps().filter((app) =>
      target.entry.isDirectory ? app.handlesFolders : app.handlesFiles,
    );
  });

  async ngOnInit(): Promise<void> {
    const [rootListing, apps] = await Promise.all([
      this.argus.explore(null),
      this.argus.openWithApps(),
    ]);

    this.roots.set(rootListing.entries);
    this.apps.set(apps);
    this.listing.set(rootListing);
  }

  // ------------------------------------------------------------- navigation

  protected async open(entry: BrowseEntry): Promise<void> {
    if (entry.isDirectory) await this.load(entry.path);
  }

  protected async goUp(): Promise<void> {
    await this.load(this.listing()?.parent ?? null);
  }

  protected async goRoot(): Promise<void> {
    await this.load(null);
  }

  protected async refresh(): Promise<void> {
    await this.load(this.listing()?.path || null);
  }

  private async load(path: string | null): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      this.listing.set(await this.argus.explore(path));
    } finally {
      this.loading.set(false);
    }
  }

  // -------------------------------------------------------------- open with

  protected openPicker(entry: BrowseEntry, event: Event): void {
    event.stopPropagation();
    this.picker.set({ entry });
  }

  protected closePicker(): void {
    this.picker.set(null);
  }

  /** The plain Open button on a file: whatever Windows has associated with it. */
  protected async openDefault(entry: BrowseEntry, event: Event): Promise<void> {
    event.stopPropagation();
    await this.launch(entry, null);
  }

  protected async pickApp(app: OpenWithApp): Promise<void> {
    const target = this.picker();
    this.closePicker();
    if (target) await this.launch(target.entry, app.key);
  }

  private async launch(entry: BrowseEntry, app: string | null): Promise<void> {
    this.error.set(null);
    const result = await this.argus.openWith(entry.path, app);
    if (!result.started) this.error.set(result.reason ?? `Could not open ${entry.name}.`);
  }

  protected extension(entry: BrowseEntry): string {
    if (entry.isDirectory) return '';
    const dot = entry.name.lastIndexOf('.');
    return dot > 0 ? entry.name.slice(dot + 1).toUpperCase() : '';
  }
}
