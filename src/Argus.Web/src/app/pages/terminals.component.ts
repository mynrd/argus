import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import { ArgusService } from '../core/argus.service';
import { StrayTerminal, TerminalEntry } from '../core/models';

/** Matches the app's own dark palette rather than xterm's default black. */
const THEME = {
  background: '#080a0f',
  foreground: '#e6edf3',
  cursor: '#4c9aff',
  cursorAccent: '#080a0f',
  selectionBackground: 'rgba(76, 154, 255, 0.35)',
};

/** Lines kept per terminal in the browser. The host keeps its own 256 KB for replay. */
const SCROLLBACK = 5000;

/** Reconnect attempts after the socket drops before the page stops trying and says so. */
const MAX_RETRIES = 5;

const RETRY_BASE_MS = 1200;

/** One terminal on screen: its xterm, its socket, and the state neither of them keeps. */
interface Pane {
  terminalId: string;
  container: HTMLDivElement;
  term: Terminal;
  fit: FitAddon;
  socket?: WebSocket;
  running: boolean;
  statusText: string;
  statusIsError: boolean;
  retries: number;
  observer?: ResizeObserver;
  /** Set when the tab is closed, so a reconnect timer that is already queued gives up. */
  discarded: boolean;
}

/**
 * Real shells on the watched PC, in the browser.
 *
 * The terminals do not live in this page and they do not live in the server either - they live in
 * a separate host process, so closing this tab, restarting the server, or the server crashing
 * leaves every one of them running. Coming back reattaches and replays what is already on screen.
 * A terminal ends when it is killed here, when its shell exits, or when the machine restarts.
 *
 * The xterm instances are held in a plain Map rather than rendered by the template: switching tabs
 * moves a container element into the host div instead of rebuilding it, because rebuilding an
 * xterm loses its scrollback, its cursor and the focus you were typing into.
 */
@Component({
  selector: 'argus-terminals',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './terminals.component.html',
  styleUrl: './terminals.component.scss',
})
export class TerminalsComponent implements OnInit, AfterViewInit, OnDestroy {
  private readonly argus = inject(ArgusService);
  private readonly host = viewChild<ElementRef<HTMLDivElement>>('host');
  private readonly renameBox = viewChild<ElementRef<HTMLInputElement>>('renameBox');

  protected readonly terminals = signal<TerminalEntry[]>([]);
  protected readonly strays = signal<StrayTerminal[]>([]);
  protected readonly activeId = signal<string | null>(null);
  protected readonly loading = signal(false);
  protected readonly opening = signal(false);

  /** Fills the page with the terminal, for when the tab strip and the header are in the way. */
  protected readonly maximised = signal(false);

  /** Which tab is being renamed, and the text so far. */
  protected readonly renaming = signal<string | null>(null);

  /** The active pane's status line - "Reconnecting…", or why it stopped. */
  protected readonly status = signal<{ text: string; isError: boolean } | null>(null);

  private readonly panes = new Map<string, Pane>();
  private readonly retryTimers = new Map<string, ReturnType<typeof setTimeout>>();

  protected readonly active = computed(() =>
    this.terminals().find((t) => t.terminalId === this.activeId()),
  );

  constructor() {
    // The rename box only exists while a tab is being renamed, so it is focused when it appears
    // rather than by whatever opened it.
    effect(() => {
      const box = this.renameBox()?.nativeElement;
      if (box) {
        box.focus();
        box.select();
      }
    });
  }

  async ngOnInit(): Promise<void> {
    await this.refresh();

    // A first visit with nothing open should land on a usable terminal, not on an empty page with
    // a button. Every later visit adopts whatever is already running.
    if (this.terminals().length === 0) await this.open();
  }

  ngAfterViewInit(): void {
    // Covers the case where the terminal list came back before the view was ready.
    this.mount();
  }

  ngOnDestroy(): void {
    // Only the browser's half is torn down. The shells keep running in the host process, which is
    // the entire point of them living there.
    for (const timer of this.retryTimers.values()) clearTimeout(timer);
    this.retryTimers.clear();
    for (const pane of this.panes.values()) this.discard(pane);
    this.panes.clear();
  }

  /** Re-reads the host's list and adopts anything this page does not have a pane for yet. */
  protected async refresh(): Promise<void> {
    this.loading.set(true);
    try {
      const listing = await this.argus.listTerminals();
      this.terminals.set(listing.terminals);
      this.strays.set(listing.strays);

      // A terminal killed from another device, or one whose shell exited and was closed there.
      for (const [id, pane] of this.panes) {
        if (!listing.terminals.some((t) => t.terminalId === id)) {
          this.discard(pane);
          this.panes.delete(id);
        }
      }

      for (const terminal of listing.terminals) {
        if (!this.panes.has(terminal.terminalId)) this.adopt(terminal);
      }

      const current = this.activeId();
      if (!current || !listing.terminals.some((t) => t.terminalId === current)) {
        // A running terminal wins the active tab; only fall back to an exited one when nothing on
        // the list is still alive.
        const preferred = listing.terminals.find((t) => t.running) ?? listing.terminals[0];
        this.select(preferred?.terminalId ?? null);
      }
    } finally {
      this.loading.set(false);
    }
  }

  /** Opens another terminal, sized to whatever the active one is already drawn at. */
  protected async open(): Promise<void> {
    if (this.opening()) return;
    this.opening.set(true);

    try {
      const reference = this.activePane();
      const opened = await this.argus.openTerminal(
        reference?.term.cols ?? 100,
        reference?.term.rows ?? 30,
      );
      if (!opened) return;

      this.terminals.update((list) => [...list, opened]);
      this.adopt(opened);
      this.select(opened.terminalId);
    } finally {
      this.opening.set(false);
    }
  }

  protected select(terminalId: string | null): void {
    this.activeId.set(terminalId);
    this.mount();
  }

  /** Closes a tab and ends its shell. Both, always - a tab is the only handle on a terminal. */
  protected async close(terminal: TerminalEntry, event: Event): Promise<void> {
    event.stopPropagation();

    const pane = this.panes.get(terminal.terminalId);
    if (pane) {
      this.discard(pane);
      this.panes.delete(terminal.terminalId);
    }

    const remaining = this.terminals().filter((t) => t.terminalId !== terminal.terminalId);
    this.terminals.set(remaining);
    if (this.activeId() === terminal.terminalId) {
      this.select(remaining[remaining.length - 1]?.terminalId ?? null);
    }

    await this.argus.killTerminal(terminal.terminalId);
  }

  /** Kills the active shell and opens a fresh one in its place. */
  protected async restart(): Promise<void> {
    const current = this.active();
    if (!current) {
      await this.open();
      return;
    }

    // The replacement opens first so the strip never flickers empty.
    await this.open();
    await this.close(current, new Event('restart'));
  }

  protected async killStray(stray: StrayTerminal): Promise<void> {
    await this.argus.killStrayTerminal(stray);
    await this.refresh();
  }

  protected toggleMaximised(): void {
    this.maximised.update((on) => !on);
    // The pane changed size; refit once the layout has settled.
    requestAnimationFrame(() => this.fitActive());
  }

  // ---------------------------------------------------------------- renaming

  protected startRename(terminal: TerminalEntry, event: Event): void {
    event.stopPropagation();
    this.renaming.set(terminal.terminalId);
  }

  protected async commitRename(terminal: TerminalEntry, value: string): Promise<void> {
    if (this.renaming() !== terminal.terminalId) return;
    this.renaming.set(null);

    const name = value.trim();
    if (name === (terminal.name ?? '')) return;

    // Updated locally first: the label is cosmetic, and waiting on a round trip to redraw a tab
    // name reads as the rename not having worked.
    this.terminals.update((list) =>
      list.map((t) => (t.terminalId === terminal.terminalId ? { ...t, name: name || null } : t)),
    );
    await this.argus.renameTerminal(terminal.terminalId, name);
  }

  protected cancelRename(): void {
    this.renaming.set(null);
  }

  /** The label for a tab: what it was renamed to, else its position in the strip. */
  protected label(terminal: TerminalEntry): string {
    if (terminal.name) return terminal.name;

    const index = this.terminals().findIndex((t) => t.terminalId === terminal.terminalId);
    return `Terminal ${index + 1}`;
  }

  protected started(at: number | null): string {
    if (!at) return '';

    const seconds = Math.max(0, Math.round((Date.now() - at) / 1000));
    if (seconds < 60) return `${seconds}s ago`;
    if (seconds < 3600) return `${Math.round(seconds / 60)}m ago`;
    return `${Math.round(seconds / 3600)}h ago`;
  }

  // ---------------------------------------------------------------- panes

  private activePane(): Pane | undefined {
    const id = this.activeId();
    return id ? this.panes.get(id) : undefined;
  }

  /** Builds the xterm for one terminal and connects it. Idempotent per terminal id. */
  private adopt(terminal: TerminalEntry): void {
    if (this.panes.has(terminal.terminalId)) return;

    const container = document.createElement('div');
    container.className = 'screen';

    const term = new Terminal({
      cursorBlink: true,
      fontSize: 13,
      fontFamily: 'ui-monospace, "Cascadia Mono", Consolas, "SF Mono", Menlo, monospace',
      theme: THEME,
      scrollback: SCROLLBACK,
      // A terminal nobody has attached to yet still has a size on the host; the fit below
      // corrects it as soon as the container is on screen.
      cols: terminal.cols,
      rows: terminal.rows,
    });

    const fit = new FitAddon();
    term.loadAddon(fit);
    term.open(container);

    const pane: Pane = {
      terminalId: terminal.terminalId,
      container,
      term,
      fit,
      running: terminal.running,
      statusText: '',
      statusIsError: false,
      retries: 0,
      discarded: false,
    };

    term.onData((data) => this.sendInput(pane, data));
    term.onResize(({ cols, rows }) => this.sendResize(pane, cols, rows));

    // The container is sized by CSS, and the flex layout settles after this runs.
    let pending: ReturnType<typeof setTimeout> | undefined;
    pane.observer = new ResizeObserver(() => {
      clearTimeout(pending);
      pending = setTimeout(() => {
        if (pane.container.isConnected) this.safeFit(pane);
      }, 80);
    });
    pane.observer.observe(container);

    this.panes.set(terminal.terminalId, pane);
    this.connect(pane);
  }

  /**
   * Puts the active pane's container in the host div, replacing whatever was there.
   *
   * The host may not exist yet - the first mount is driven by the load in ngOnInit, and nothing
   * guarantees the view is up by then. Mounting is repeated in ngAfterViewInit for that case.
   */
  private mount(): void {
    const element = this.host()?.nativeElement;
    if (!element) return;

    element.replaceChildren();

    const pane = this.activePane();
    if (!pane) {
      this.status.set(null);
      return;
    }

    element.append(pane.container);
    this.status.set(pane.statusText ? { text: pane.statusText, isError: pane.statusIsError } : null);

    requestAnimationFrame(() => {
      this.safeFit(pane);
      pane.term.focus();
    });
  }

  private fitActive(): void {
    const pane = this.activePane();
    if (pane) this.safeFit(pane);
  }

  /** Fitting a container with no layout yet throws inside xterm; a terminal is not worth that. */
  private safeFit(pane: Pane): void {
    try {
      if (pane.container.isConnected) pane.fit.fit();
    } catch {
      // Zero-sized container - the next resize will land.
    }
  }

  private setStatus(pane: Pane, text: string, isError = false): void {
    pane.statusText = text;
    pane.statusIsError = isError;
    if (this.activeId() === pane.terminalId) {
      this.status.set(text ? { text, isError } : null);
    }
  }

  // ---------------------------------------------------------------- socket

  private connect(pane: Pane): void {
    if (pane.discarded) return;

    const socket = new WebSocket(this.argus.terminalSocketUrl(pane.terminalId));
    pane.socket = socket;

    socket.onopen = () => {
      pane.retries = 0;
      this.setStatus(pane, '');
      // The host does not know what size this browser draws it at until it is told.
      this.sendResize(pane, pane.term.cols, pane.term.rows);
    };

    socket.onmessage = (event) => this.receive(pane, event.data);

    socket.onclose = () => {
      if (pane.discarded || !pane.running) return;
      this.retry(pane);
    };

    // onerror is always followed by onclose, so the reconnect is left to that one path.
    socket.onerror = () => {};
  }

  private receive(pane: Pane, raw: unknown): void {
    if (typeof raw !== 'string') return;

    let frame: { t?: string; d?: string; c?: number | null; m?: string };
    try {
      frame = JSON.parse(raw);
    } catch {
      return;
    }

    switch (frame.t) {
      case 'b':
        // The replay is the screen as the host has it. Reset first, or reattaching after a
        // reconnect paints the buffer on top of what is already drawn.
        pane.term.reset();
        pane.term.write(frame.d ?? '');
        return;

      case 'd':
        pane.term.write(frame.d ?? '');
        return;

      case 'x': {
        pane.running = false;
        const code = frame.c ?? null;
        pane.term.write(
          `\r\n\x1b[90m[shell exited${code === null ? '' : ` with code ${code}`} - Restart starts a new one]\x1b[0m\r\n`,
        );
        this.setStatus(pane, '');
        this.terminals.update((list) =>
          list.map((t) =>
            t.terminalId === pane.terminalId ? { ...t, running: false, exitCode: code } : t,
          ),
        );
        return;
      }

      case 'e':
        this.setStatus(pane, frame.m ?? 'This terminal could not be attached.', true);
        return;
    }
  }

  /**
   * A dropped connection while the shell is alive - a sleeping phone, a network blip - reattaches
   * quietly, and the host replays its buffer so nothing is lost. Repeated failure stops with
   * something to read rather than hammering the server.
   */
  private retry(pane: Pane): void {
    pane.retries += 1;
    if (pane.retries > MAX_RETRIES) {
      this.setStatus(pane, 'Lost the connection to this terminal. Refresh to reattach.', true);
      return;
    }

    this.setStatus(pane, 'Reconnecting…');
    const timer = setTimeout(() => {
      this.retryTimers.delete(pane.terminalId);
      this.connect(pane);
    }, RETRY_BASE_MS * pane.retries);

    this.retryTimers.set(pane.terminalId, timer);
  }

  private sendInput(pane: Pane, data: string): void {
    this.send(pane, { t: 'i', d: data });
  }

  private sendResize(pane: Pane, cols: number, rows: number): void {
    this.send(pane, { t: 'r', c: cols, r: rows });
  }

  private send(pane: Pane, frame: object): void {
    if (pane.socket?.readyState !== WebSocket.OPEN) return;
    pane.socket.send(JSON.stringify(frame));
  }

  /** Tears down the browser's half of a terminal. Says nothing to the host about the shell. */
  private discard(pane: Pane): void {
    pane.discarded = true;

    const timer = this.retryTimers.get(pane.terminalId);
    if (timer) {
      clearTimeout(timer);
      this.retryTimers.delete(pane.terminalId);
    }

    pane.observer?.disconnect();
    if (pane.socket) {
      // Cleared first: closing deliberately must not look like a drop worth reconnecting over.
      pane.socket.onclose = null;
      pane.socket.close();
    }
    pane.term.dispose();
    pane.container.remove();
  }
}
