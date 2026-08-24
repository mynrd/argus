import { Injectable, signal, computed } from '@angular/core';
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from '@microsoft/signalr';
import {
  BrowseListing,
  CloseResult,
  Frame,
  InjectionMode,
  KeyEventDto,
  MouseEventDto,
  OpenWithApp,
  QualityLevel,
  ReleaseKeysResult,
  SendKeyResult,
  WindowListItem,
  WindowStatus,
  WindowStatusUpdate,
} from './models';

const FRAME_HEADER_SIZE = 16;

/** Missed heartbeats before the agent is treated as offline. The server beats every 2s. */
const HEARTBEAT_TIMEOUT_MS = 7000;

type FrameHandler = (frame: Frame) => void;

/**
 * Owns both connections to the server:
 *   - SignalR for control (window list, attach, subscribe, status, keystrokes)
 *   - a raw WebSocket for binary frames
 *
 * They are separate on purpose. Frames never queue behind control messages, so a keystroke is not
 * stuck behind a backlog of JPEGs, and frames avoid the ~33% base64 overhead SignalR's JSON
 * protocol would add to every byte of every tile.
 */
@Injectable({ providedIn: 'root' })
export class ArgusService {
  private hub?: HubConnection;
  private socket?: WebSocket;
  private clientId = '';
  /** Connection id the current frame socket was opened for, so we never open a duplicate. */
  private socketClientId = '';
  private readonly frameHandlers = new Map<number, Set<FrameHandler>>();
  private heartbeatTimer?: ReturnType<typeof setInterval>;
  private reconnectTimer?: ReturnType<typeof setTimeout>;
  private disposed = false;

  readonly connected = signal(false);
  readonly framesSocketOpen = signal(false);
  readonly lastHeartbeat = signal(0);
  readonly agentOnline = signal(false);
  readonly windows = signal<WindowListItem[]>([]);
  readonly statuses = signal<ReadonlyMap<number, WindowStatusUpdate>>(new Map());
  readonly lastError = signal<string | null>(null);

  /**
   * Bumped once per completed Release keys pass.
   *
   * A counter rather than a boolean because the interesting thing is the event, not a state: a
   * viewer watches it to drop its own idea of what is held, and pressing the button twice has to
   * fire twice.
   */
  readonly keysReleased = signal(0);

  readonly attached = computed(() => [...this.statuses().values()]);

  readonly liveCount = computed(
    () => this.attached().filter((s) => s.status === WindowStatus.Streaming).length,
  );

  /** Base origin of the server. Same origin in production; the dev server proxies instead. */
  private get origin(): string {
    return window.location.origin;
  }

  async start(): Promise<void> {
    if (this.hub) return;
    this.disposed = false;

    this.hub = new HubConnectionBuilder()
      .withUrl(`${this.origin}/hubs/argus`)
      .withAutomaticReconnect([0, 1000, 2000, 5000, 10000])
      .configureLogging(LogLevel.Warning)
      .build();

    this.hub.on('Hello', (payload: { clientId: string; statuses: WindowStatusUpdate[] }) => {
      this.clientId = payload.clientId;
      this.applyStatuses(payload.statuses ?? []);
      this.openFrameSocket();
    });

    this.hub.on('WindowStatus', (update: WindowStatusUpdate) => this.applyStatus(update));

    this.hub.on('Heartbeat', () => {
      this.lastHeartbeat.set(Date.now());
      this.agentOnline.set(true);
    });

    this.hub.onreconnected(() => {
      this.connected.set(true);
      this.lastError.set(null);
      // The connection id changes on reconnect, so the frame socket must follow it.
      void this.refreshAfterReconnect();
    });

    this.hub.onreconnecting(() => {
      this.connected.set(false);
      this.agentOnline.set(false);
    });

    this.hub.onclose(() => {
      this.connected.set(false);
      this.agentOnline.set(false);
      this.scheduleRestart();
    });

    await this.connect();
    this.startHeartbeatWatch();
  }

  /**
   * Drops both connections and forgets everything they told us.
   *
   * Locking has to do this rather than only hiding the UI: a live hub is a live keyboard and mouse
   * on the host, and the frame socket keeps the desktop on screen behind whatever is drawn on top.
   * The server refuses both without a session anyway - this stops the client retrying forever.
   */
  stop(): void {
    if (!this.hub && !this.socket) return;
    this.disposed = true;

    if (this.reconnectTimer) {
      clearTimeout(this.reconnectTimer);
      this.reconnectTimer = undefined;
    }

    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer);
      this.heartbeatTimer = undefined;
    }

    this.socket?.close();
    this.socket = undefined;
    this.socketClientId = '';

    const hub = this.hub;
    this.hub = undefined;
    void hub?.stop();

    this.clientId = '';
    this.connected.set(false);
    this.framesSocketOpen.set(false);
    this.agentOnline.set(false);
    this.lastHeartbeat.set(0);
    this.windows.set([]);
    this.statuses.set(new Map());
    this.lastError.set(null);
  }

  private async connect(): Promise<void> {
    if (!this.hub || this.disposed) return;
    try {
      await this.hub.start();
      this.clientId = this.hub.connectionId ?? '';
      this.connected.set(true);
      this.lastError.set(null);
      this.openFrameSocket();
      await this.refreshWindows();
    } catch (error) {
      this.connected.set(false);
      this.lastError.set(`Cannot reach the Argus agent: ${String(error)}`);
      this.scheduleRestart();
    }
  }

  private scheduleRestart(): void {
    if (this.disposed || this.reconnectTimer) return;
    this.reconnectTimer = setTimeout(() => {
      this.reconnectTimer = undefined;
      if (this.hub?.state === HubConnectionState.Disconnected) void this.connect();
    }, 2000);
  }

  private async refreshAfterReconnect(): Promise<void> {
    this.clientId = this.hub?.connectionId ?? '';
    this.openFrameSocket();
    await this.refreshWindows();
    await this.refreshStatuses();
  }

  private startHeartbeatWatch(): void {
    this.heartbeatTimer ??= setInterval(() => {
      const last = this.lastHeartbeat();
      if (last > 0 && Date.now() - last > HEARTBEAT_TIMEOUT_MS) this.agentOnline.set(false);
    }, 2000);
  }

  // ------------------------------------------------------------------ frames

  private openFrameSocket(): void {
    if (!this.clientId) return;

    // Both the Hello handler and connect() reach here on every connect, and again on every
    // reconnect. Opening a second socket for a connection id that already has a live one makes
    // the server see two sockets for one viewer, and the loser's cleanup tears down the winner's
    // subscriptions - a viewer that looks connected but never receives another frame.
    if (this.socketClientId === this.clientId && this.socket && this.socket.readyState <= WebSocket.OPEN) {
      return;
    }

    if (this.socket && this.socket.readyState <= WebSocket.OPEN) {
      this.socket.close();
    }

    const scheme = window.location.protocol === 'https:' ? 'wss' : 'ws';
    const url = `${scheme}://${window.location.host}/ws/frames?clientId=${encodeURIComponent(this.clientId)}`;

    const socket = new WebSocket(url);
    socket.binaryType = 'arraybuffer';
    socket.onopen = () => this.framesSocketOpen.set(true);
    socket.onclose = () => {
      if (this.socket === socket) {
        this.framesSocketOpen.set(false);
        this.socketClientId = '';
      }
    };
    socket.onerror = () => this.framesSocketOpen.set(false);
    socket.onmessage = (event) => this.dispatchFrame(event.data as ArrayBuffer);

    this.socket = socket;
    this.socketClientId = this.clientId;
  }

  private dispatchFrame(buffer: ArrayBuffer): void {
    if (!buffer || buffer.byteLength < FRAME_HEADER_SIZE) return;

    const view = new DataView(buffer);
    // HWNDs comfortably fit a double; Number() keeps the rest of the app off BigInt.
    const handle = Number(view.getBigInt64(0, true));
    const handlers = this.frameHandlers.get(handle);
    if (!handlers?.size) return;

    const frame: Frame = {
      handle,
      sequence: view.getInt32(8, true),
      width: view.getUint16(12, true),
      height: view.getUint16(14, true),
      bytes: new Uint8Array(buffer, FRAME_HEADER_SIZE),
    };

    for (const handler of handlers) handler(frame);
  }

  /** Registers a frame handler for one window. Returns the unsubscribe function. */
  onFrame(handle: number, handler: FrameHandler): () => void {
    let handlers = this.frameHandlers.get(handle);
    if (!handlers) {
      handlers = new Set();
      this.frameHandlers.set(handle, handlers);
    }
    handlers.add(handler);

    return () => {
      const current = this.frameHandlers.get(handle);
      if (!current) return;
      current.delete(handler);
      if (current.size === 0) this.frameHandlers.delete(handle);
    };
  }

  // ----------------------------------------------------------------- control

  async refreshWindows(): Promise<void> {
    const list = await this.invoke<WindowListItem[]>('ListWindows');
    if (list) this.windows.set(list);
  }

  async refreshStatuses(): Promise<void> {
    const statuses = await this.invoke<WindowStatusUpdate[]>('ListStatuses');
    if (statuses) this.applyStatuses(statuses);
  }

  async attach(handle: number): Promise<void> {
    const status = await this.invoke<WindowStatusUpdate | null>('Attach', handle);
    if (status) this.applyStatus(status);
    await this.refreshWindows();
  }

  async detach(handle: number): Promise<void> {
    await this.invoke('Detach', handle);
    const next = new Map(this.statuses());
    next.delete(handle);
    this.statuses.set(next);
    await this.refreshWindows();
  }

  async subscribe(handle: number, quality: QualityLevel): Promise<boolean> {
    return (await this.invoke<boolean>('Subscribe', handle, quality)) ?? false;
  }

  async unsubscribe(handle: number): Promise<void> {
    await this.invoke('Unsubscribe', handle);
  }

  async sendKey(event: KeyEventDto, mode: InjectionMode): Promise<SendKeyResult> {
    const result = await this.invoke<SendKeyResult>('SendKey', event, mode);
    return result ?? { delivered: false, reason: 'Not connected' };
  }

  /**
   * Types a whole block of text into the window, optionally pressing Enter after it.
   *
   * One call for the lot rather than a SendKey per character: a phone typing a command line over
   * a tailnet would otherwise pay a round trip per keystroke, and anything that grabbed the
   * host's foreground half way through would split the text between two windows.
   */
  async sendText(handle: number, text: string, submit: boolean): Promise<SendKeyResult> {
    const result = await this.invoke<SendKeyResult>('SendText', String(handle), text, submit);
    return result ?? { delivered: false, reason: 'Not connected' };
  }

  /** Resizes the host window. Width and height are the visible size, not the outer window rect. */
  async resizeWindow(
    handle: number,
    width: number,
    height: number,
  ): Promise<{ resized: boolean; reason?: string | null }> {
    return (
      (await this.invoke<{ resized: boolean; reason?: string | null }>(
        'ResizeWindow',
        handle,
        width,
        height,
      )) ?? { resized: false, reason: 'Not connected' }
    );
  }

  async sendMouse(event: MouseEventDto): Promise<SendKeyResult> {
    const result = await this.invoke<SendKeyResult>('SendMouse', event);
    return result ?? { delivered: false, reason: 'Not connected' };
  }

  /** Runs a command on the host, as the Windows Run dialog would. */
  async runApplication(command: string): Promise<{ started: boolean; reason?: string | null }> {
    return (
      (await this.invoke<{ started: boolean; reason?: string | null }>(
        'RunApplication',
        command,
      )) ?? { started: false, reason: 'Not connected' }
    );
  }

  /** One directory of the host filesystem. Pass null for the roots. */
  async browse(path: string | null): Promise<BrowseListing> {
    return (
      (await this.invoke<BrowseListing>('Browse', path)) ?? {
        path: '',
        label: 'This PC',
        parent: null,
        entries: [],
        error: 'Not connected',
      }
    );
  }

  /**
   * Drops a window from the local list without waiting for the server to be re-enumerated.
   *
   * A closed or killed app should leave the UI the moment it is gone, and ListWindows is pulled,
   * not pushed - polling for its absence would leave a dead row on screen for a second or two.
   */
  forgetWindow(handle: number): void {
    this.windows.update((list) => list.filter((w) => w.handle !== handle));
  }

  /** Asks the app to close. It may put up a save prompt on the host desktop. */
  async closeWindow(handle: number): Promise<CloseResult> {
    return (
      (await this.invoke<CloseResult>('CloseWindow', handle)) ?? {
        closed: false,
        reason: 'Not connected',
      }
    );
  }

  /** Terminates the app. Unsaved work in it is lost. */
  async killWindow(handle: number): Promise<CloseResult> {
    return (
      (await this.invoke<CloseResult>('KillWindow', handle)) ?? {
        closed: false,
        reason: 'Not connected',
      }
    );
  }

  /** Like browse(), but lists every file rather than only the launchable ones. */
  async explore(path: string | null): Promise<BrowseListing> {
    return (
      (await this.invoke<BrowseListing>('Explore', path)) ?? {
        path: '',
        label: 'This PC',
        parent: null,
        entries: [],
        error: 'Not connected',
      }
    );
  }

  async openWithApps(): Promise<OpenWithApp[]> {
    return (await this.invoke<OpenWithApp[]>('OpenWithApps')) ?? [];
  }

  /** Opens a host file or folder. Pass null for the file's default association. */
  async openWith(
    path: string,
    app: string | null,
  ): Promise<{ started: boolean; reason?: string | null }> {
    return (
      (await this.invoke<{ started: boolean; reason?: string | null }>('OpenWith', path, app)) ?? {
        started: false,
        reason: 'Not connected',
      }
    );
  }

  /**
   * Lifts every modifier off the host keyboard, plus anything else found physically down.
   *
   * No handle: a key-up clears the global key state whatever window is in front, so this works
   * from any page and even with nothing attached.
   */
  async releaseKeys(): Promise<ReleaseKeysResult> {
    const result = await this.invoke<ReleaseKeysResult>('ReleaseKeys');
    if (!result) return { released: [], reason: 'Not connected' };

    this.keysReleased.update((n) => n + 1);
    return result;
  }

  /** Brings the window to the foreground on the host desktop. */
  async focusWindow(handle: number): Promise<{ focused: boolean; reason?: string | null }> {
    return (
      (await this.invoke<{ focused: boolean; reason?: string | null }>('FocusWindow', handle)) ?? {
        focused: false,
        reason: 'Not connected',
      }
    );
  }

  private async invoke<T>(method: string, ...args: unknown[]): Promise<T | undefined> {
    if (!this.hub || this.hub.state !== HubConnectionState.Connected) return undefined;
    try {
      return await this.hub.invoke<T>(method, ...args);
    } catch (error) {
      this.lastError.set(`${method} failed: ${String(error)}`);
      return undefined;
    }
  }

  private applyStatus(update: WindowStatusUpdate): void {
    const next = new Map(this.statuses());
    next.set(update.handle, update);
    this.statuses.set(next);
  }

  private applyStatuses(updates: WindowStatusUpdate[]): void {
    this.statuses.set(new Map(updates.map((s) => [s.handle, s])));
  }

  statusFor(handle: number): WindowStatusUpdate | undefined {
    return this.statuses().get(handle);
  }
}
