import {
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
import { ActivatedRoute, Router } from '@angular/router';
import { FrameViewComponent } from '../core/frame-view.component';
import { ArgusService } from '../core/argus.service';
import {
  InjectionMode,
  KeyEventDto,
  MouseAction,
  MouseButton,
  QUALITY_LABELS,
  QualityLevel,
  STATUS_LABELS,
  WindowStatus,
} from '../core/models';

const MIN_ZOOM = 0.25;
const MAX_ZOOM = 4;

/** Hold this long without moving and a touch becomes a right-click. */
const LONG_PRESS_MS = 500;

/** Movement past this many pixels turns a touch into a drag rather than a tap. */
const DRAG_THRESHOLD_PX = 10;

/** Floor on how often a drag reports a new position to the host. */
const MOVE_INTERVAL_MS = 30;

interface MouseGesture {
  pointerId: number;
  touch: boolean;
  startX: number;
  startY: number;
  button: MouseButton;
  /** Whether a button is currently held down on the host. */
  pressed: boolean;
  longPressFired: boolean;
  timer?: ReturnType<typeof setTimeout>;
}

/** Keys we never forward - they would fight the browser rather than reach the app. */
const SWALLOWED_CODES = new Set(['F5', 'F11', 'F12']);

@Component({
  selector: 'argus-viewer',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FrameViewComponent],
  templateUrl: './viewer.component.html',
  styleUrl: './viewer.component.scss',
})
export class ViewerComponent implements OnInit, OnDestroy {
  private readonly argus = inject(ArgusService);
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);

  private readonly keyboardSink = viewChild<ElementRef<HTMLInputElement>>('keyboardSink');
  private readonly frameView = viewChild(FrameViewComponent);
  private readonly viewerEl = viewChild<ElementRef<HTMLElement>>('viewer');

  protected readonly handle = signal(0);
  protected readonly zoom = signal(1);
  protected readonly panX = signal(0);
  protected readonly panY = signal(0);
  protected readonly quality = signal<QualityLevel>(defaultQuality());
  protected readonly sendKeys = signal(false);
  protected readonly lastKeyResult = signal<string | null>(null);
  protected readonly isFullscreen = signal(false);
  protected readonly mouseMode = signal(false);
  protected readonly fitted = signal(false);
  protected readonly menu = signal<'quality' | 'size' | null>(null);

  protected readonly qualities = [
    QualityLevel.Preview,
    QualityLevel.Low,
    QualityLevel.Medium,
    QualityLevel.High,
  ];
  protected readonly qualityLabels = QUALITY_LABELS;

  /** Sizes the host window can be snapped to. The value is what the stream ends up being. */
  protected readonly sizes = [
    { label: '1920 × 1080', value: '1920x1080' },
    { label: '1360 × 768', value: '1360x768' },
  ];

  protected readonly status = computed(() => this.argus.statuses().get(this.handle()));

  protected readonly statusLabel = computed(() => {
    const status = this.status();
    return status ? (STATUS_LABELS[status.status] ?? 'Unknown') : 'Not attached';
  });

  protected readonly statusClass = computed(() => {
    const status = this.status();
    return status ? WindowStatus[status.status].toLowerCase() : 'closed';
  });

  protected readonly title = computed(() => this.status()?.title ?? 'Live view');

  private lastPinchDistance = 0;
  private lastMidpoint = { x: 0, y: 0 };
  private readonly activePointers = new Map<number, PointerEvent>();

  private gesture: MouseGesture | null = null;
  private lastMoveSentAt = 0;

  constructor() {
    // Subscribing must be reactive, not a one-shot in ngOnInit. Opening a viewer URL directly
    // races the hub connection: the component initialises while ArgusService.start() is still
    // negotiating, the invoke is dropped on the floor, and the stream never starts. Re-running
    // whenever the connection or the chosen quality changes also covers reconnects, which issue
    // a fresh connection id and therefore need the subscription re-established.
    effect(() => {
      const handle = this.handle();
      const quality = this.quality();
      if (!handle || !this.argus.connected()) return;

      void this.argus.subscribe(handle, quality);
    });

    // A quality switch or a resize of the host window changes the frame's pixel size, which
    // changes what "fits". Re-fit only while the user has not zoomed away from it themselves.
    effect(() => {
      const size = this.frameView()?.frameSize();
      if (!size?.width || !this.fitted()) return;

      // After layout: the canvas element has not taken the new size yet at this point.
      requestAnimationFrame(() => {
        if (this.fitted()) this.fitToScreen();
      });
    });
  }

  async ngOnInit(): Promise<void> {
    const handle = Number(this.route.snapshot.paramMap.get('handle') ?? 0);
    if (!handle) {
      await this.router.navigate(['/dashboard']);
      return;
    }

    this.handle.set(handle);
    document.addEventListener('fullscreenchange', this.onFullscreenChange);
    window.addEventListener('resize', this.onViewportResize);
    await this.argus.refreshStatuses();
  }

  ngOnDestroy(): void {
    // Leaving the page mid-drag would otherwise strand a held mouse button on the host.
    this.abortMouseGesture();
    document.removeEventListener('fullscreenchange', this.onFullscreenChange);
    window.removeEventListener('resize', this.onViewportResize);
    const handle = this.handle();
    if (handle) void this.argus.unsubscribe(handle);
  }

  // ------------------------------------------------------------- full screen

  /**
   * Fullscreens the whole viewer section rather than the stage, so the bar stays on screen and
   * quality, zoom and Focus App remain reachable without leaving fullscreen.
   */
  protected async toggleFullscreen(): Promise<void> {
    const element = this.viewerEl()?.nativeElement;
    if (!element?.requestFullscreen) return;

    try {
      if (document.fullscreenElement) {
        await document.exitFullscreen();
      } else {
        await element.requestFullscreen();
      }
    } catch {
      // A rejected request (browser policy, or already exiting) leaves the flag as the
      // fullscreenchange event reports it - nothing else to do.
    }
  }

  /** Keeps the label honest when Esc or the browser's own chrome leaves fullscreen. */
  private readonly onFullscreenChange = (): void => {
    const full = document.fullscreenElement === this.viewerEl()?.nativeElement;
    this.isFullscreen.set(full);

    // Full screen starts fitted, as asked. The stage only takes its new size after the browser
    // relayouts, so measuring on the next frame rather than here.
    if (full) requestAnimationFrame(() => this.fitToScreen());
  };

  /** Rotating the tablet changes what fits; only re-fit if the view is still meant to be fitted. */
  private readonly onViewportResize = (): void => {
    if (this.fitted()) requestAnimationFrame(() => this.fitToScreen());
  };

  // ---------------------------------------------------------------- quality

  protected openMenu(which: 'quality' | 'size'): void {
    this.menu.set(which);
  }

  protected closeMenu(): void {
    this.menu.set(null);
  }

  /** The effect in the constructor re-subscribes; setting the signal is the whole action. */
  protected pickQuality(level: QualityLevel): void {
    this.quality.set(level);
    this.closeMenu();
  }

  /**
   * Resizes the window on the host. Nothing is marked as the current size on purpose - the app is
   * free to clamp the request in WM_GETMINMAXINFO, so a tick next to a preset could be a lie.
   */
  protected async pickSize(chosen: string): Promise<void> {
    this.closeMenu();

    const [width, height] = chosen.split('x').map(Number);
    const result = await this.argus.resizeWindow(this.handle(), width, height);

    this.lastKeyResult.set(result.resized ? null : (result.reason ?? 'Could not resize that window'));
  }

  // ---------------------------------------------------------- zoom and pan

  /** Zooming past 1 makes the image bigger than the stage, so panning becomes meaningful. */
  protected readonly canPan = computed(() => this.zoom() > 1);

  protected setZoom(value: number): void {
    this.fitted.set(false);
    this.zoom.set(clamp(value, MIN_ZOOM, MAX_ZOOM));
    this.clampPan();
  }

  protected onZoomInput(value: string): void {
    this.setZoom(Number(value));
  }

  protected resetZoom(): void {
    this.fitted.set(false);
    this.zoom.set(1);
    this.panX.set(0);
    this.panY.set(0);
  }

  /**
   * Scales the stream to the viewport of the device you are watching from - the tablet's screen in
   * full screen, not the host's monitor.
   *
   * One scale factor for both axes, the smaller of the two, so the picture is contained rather
   * than stretched: a 16:9 window on a 4:3 tablet gets bars, never a squashed aspect ratio.
   */
  protected fitToScreen(): void {
    const view = this.frameView();
    if (!view) return;

    const { containerW, containerH, contentW, contentH } = view.metrics();
    if (containerW <= 0 || containerH <= 0 || contentW <= 0 || contentH <= 0) return;

    this.zoom.set(clamp(Math.min(containerW / contentW, containerH / contentH), MIN_ZOOM, MAX_ZOOM));
    this.panX.set(0);
    this.panY.set(0);
    this.fitted.set(true);
  }

  /**
   * Zooms about a fixed point rather than the stage centre, so whatever is under the cursor or
   * between the fingers stays put. Without this, zooming in always dives at the middle of the
   * window and the edges are unreachable no matter how you drag afterwards.
   */
  private zoomAbout(clientX: number, clientY: number, nextZoom: number): void {
    const previous = this.zoom();
    const target = clamp(nextZoom, MIN_ZOOM, MAX_ZOOM);
    if (target === previous) return;

    this.fitted.set(false);

    const view = this.frameView();
    if (view) {
      const centre = view.centre();
      const dx = clientX - centre.x;
      const dy = clientY - centre.y;
      const scale = target / previous;

      // Keep the content point under (clientX, clientY) stationary across the zoom change.
      this.panX.set(dx - (dx - this.panX()) * scale);
      this.panY.set(dy - (dy - this.panY()) * scale);
    }

    this.zoom.set(target);
    this.clampPan();
  }

  private panBy(dx: number, dy: number): void {
    if (dx !== 0 || dy !== 0) this.fitted.set(false);
    this.panX.update((x) => x + dx);
    this.panY.update((y) => y + dy);
    this.clampPan();
  }

  /** Stops the image being dragged off into empty space, in either axis independently. */
  private clampPan(): void {
    const view = this.frameView();
    if (!view) return;

    const { containerW, containerH, contentW, contentH } = view.metrics();
    const zoom = this.zoom();

    const maxX = Math.max(0, (contentW * zoom - containerW) / 2);
    const maxY = Math.max(0, (contentH * zoom - containerH) / 2);

    this.panX.update((x) => clamp(x, -maxX, maxX));
    this.panY.update((y) => clamp(y, -maxY, maxY));
  }

  protected onWheel(event: WheelEvent): void {
    // Scroll-to-zoom is the expectation on a desktop; the page itself never scrolls here.
    event.preventDefault();
    const factor = event.deltaY < 0 ? 1.1 : 1 / 1.1;
    this.zoomAbout(event.clientX, event.clientY, this.zoom() * factor);
  }

  protected onPointerDown(event: PointerEvent): void {
    this.activePointers.set(event.pointerId, event);
    (event.target as Element).setPointerCapture?.(event.pointerId);

    if (this.activePointers.size === 2) {
      this.lastPinchDistance = this.pointerDistance();
      this.lastMidpoint = this.pointerMidpoint();
      // A second finger means pinch-zoom, not a drag on the host - undo anything already pressed.
      this.abortMouseGesture();
      return;
    }

    if (this.mouseMode()) this.beginMouseGesture(event);
  }

  protected onPointerMove(event: PointerEvent): void {
    const previous = this.activePointers.get(event.pointerId);
    if (!previous) return;
    this.activePointers.set(event.pointerId, event);

    if (this.activePointers.size >= 2) {
      event.preventDefault();

      // Pinch: scale by how much the finger separation changed, anchored at the midpoint...
      const distance = this.pointerDistance();
      if (this.lastPinchDistance > 0 && distance > 0) {
        const midpoint = this.pointerMidpoint();
        this.zoomAbout(midpoint.x, midpoint.y, this.zoom() * (distance / this.lastPinchDistance));

        // ...and treat movement of the midpoint itself as a two-finger drag.
        this.panBy(midpoint.x - this.lastMidpoint.x, midpoint.y - this.lastMidpoint.y);
        this.lastMidpoint = midpoint;
      }
      this.lastPinchDistance = distance;
      return;
    }

    if (this.mouseMode()) {
      event.preventDefault();
      this.moveMouseGesture(event);
      return;
    }

    // Single pointer drags the image. Clamping makes this a no-op at zoom 1, so there is no
    // need to special-case the un-zoomed state.
    if (this.canPan()) {
      event.preventDefault();
      this.panBy(event.clientX - previous.clientX, event.clientY - previous.clientY);
    }
  }

  protected onPointerUp(event: PointerEvent): void {
    this.endMouseGesture(event);
    this.activePointers.delete(event.pointerId);
    if (this.activePointers.size < 2) {
      this.lastPinchDistance = 0;
      // Lifting one finger of a pinch must not make the remaining one jump the image: the
      // surviving pointer's stored position is already current, so panning resumes smoothly.
    }
  }

  private pointerDistance(): number {
    const [a, b] = [...this.activePointers.values()];
    if (!a || !b) return 0;
    return Math.hypot(a.clientX - b.clientX, a.clientY - b.clientY);
  }

  private pointerMidpoint(): { x: number; y: number } {
    const [a, b] = [...this.activePointers.values()];
    if (!a || !b) return { x: 0, y: 0 };
    return { x: (a.clientX + b.clientX) / 2, y: (a.clientY + b.clientY) / 2 };
  }

  // ------------------------------------------------------------ mouse mode

  /**
   * Turning it on foregrounds the window, because a click is delivered by moving the real cursor
   * to those screen coordinates - if something else is stacked on top, that is what gets clicked.
   */
  protected async toggleMouseMode(): Promise<void> {
    const next = !this.mouseMode();
    this.mouseMode.set(next);
    this.abortMouseGesture();

    if (!next) return;

    this.lastKeyResult.set(null);
    const result = await this.argus.focusWindow(this.handle());
    if (!result.focused && result.reason) this.lastKeyResult.set(result.reason);
  }

  /** Right-click is sent from the pointer events; this only stops the browser's own menu. */
  protected onContextMenu(event: Event): void {
    if (this.mouseMode()) event.preventDefault();
  }

  private beginMouseGesture(event: PointerEvent): void {
    const view = this.frameView();
    if (!view || !view.contains(event.clientX, event.clientY)) return;

    event.preventDefault();

    const touch = event.pointerType !== 'mouse';
    this.gesture = {
      pointerId: event.pointerId,
      touch,
      startX: event.clientX,
      startY: event.clientY,
      button: buttonFor(event.button),
      pressed: false,
      longPressFired: false,
      timer: undefined,
    };

    if (!touch) {
      // A real mouse says which button it used up front, so press it immediately and let the
      // matching pointerup release it. Drag, select and drag-and-drop all fall out of that.
      this.gesture.pressed = true;
      void this.dispatchMouse(MouseAction.Down, this.gesture.button, event);
      return;
    }

    // A finger does not. Hold it still and it means right-click; move it and it means drag; let
    // go quickly and it means a plain tap. Nothing is sent until one of those is decided, because
    // pressing a button early would make the long-press a left-press-then-right-click.
    this.gesture.timer = setTimeout(() => {
      const gesture = this.gesture;
      if (!gesture || gesture.pressed) return;

      gesture.longPressFired = true;
      void this.dispatchMouseAt(MouseAction.Click, MouseButton.Right, gesture.startX, gesture.startY);
    }, LONG_PRESS_MS);
  }

  private moveMouseGesture(event: PointerEvent): void {
    const gesture = this.gesture;
    if (!gesture || gesture.pointerId !== event.pointerId) return;

    const travelled = Math.hypot(event.clientX - gesture.startX, event.clientY - gesture.startY);

    if (gesture.touch && !gesture.pressed && !gesture.longPressFired) {
      if (travelled < DRAG_THRESHOLD_PX) return;

      // Moved before the hold completed: this is a drag. Press at the point the finger started
      // from, not where it is now, or the first part of the drag is lost.
      this.clearLongPress(gesture);
      gesture.pressed = true;
      void this.dispatchMouseAt(MouseAction.Down, gesture.button, gesture.startX, gesture.startY);
    }

    if (!gesture.pressed) return;

    // Throttled: the hub call is a round trip, and a phone emits pointermove far faster than the
    // host needs to see intermediate positions.
    const now = performance.now();
    if (now - this.lastMoveSentAt < MOVE_INTERVAL_MS) return;
    this.lastMoveSentAt = now;

    void this.dispatchMouse(MouseAction.Move, gesture.button, event);
  }

  private endMouseGesture(event: PointerEvent): void {
    const gesture = this.gesture;
    if (!gesture || gesture.pointerId !== event.pointerId) return;

    this.clearLongPress(gesture);
    this.gesture = null;

    if (gesture.pressed) {
      void this.dispatchMouse(MouseAction.Up, gesture.button, event);
      return;
    }

    // Released before the hold completed and without moving: a tap, which is a left click.
    if (!gesture.longPressFired) {
      void this.dispatchMouse(MouseAction.Click, gesture.button, event);
    }
  }

  /** Releases anything held and forgets the gesture - used when a pinch or a toggle interrupts. */
  private abortMouseGesture(): void {
    const gesture = this.gesture;
    if (!gesture) return;

    this.clearLongPress(gesture);
    this.gesture = null;

    if (gesture.pressed) {
      void this.dispatchMouseAt(MouseAction.Up, gesture.button, gesture.startX, gesture.startY);
    }
  }

  private clearLongPress(gesture: MouseGesture): void {
    if (gesture.timer !== undefined) {
      clearTimeout(gesture.timer);
      gesture.timer = undefined;
    }
  }

  private dispatchMouse(action: MouseAction, button: MouseButton, event: PointerEvent): Promise<void> {
    return this.dispatchMouseAt(action, button, event.clientX, event.clientY);
  }

  private async dispatchMouseAt(
    action: MouseAction,
    button: MouseButton,
    clientX: number,
    clientY: number,
  ): Promise<void> {
    const view = this.frameView();
    if (!view) return;

    const point = view.normalise(clientX, clientY);
    const result = await this.argus.sendMouse({
      windowId: String(this.handle()),
      action,
      button,
      x: point.x,
      y: point.y,
    });

    if (!result.delivered && result.reason) {
      this.lastKeyResult.set(result.reason);
    } else if (result.delivered) {
      this.lastKeyResult.set(null);
    }
  }

  // ------------------------------------------------------------------ input

  /**
   * Brings the target window to the foreground on the host desktop and arms typing here, so the
   * next keystroke lands in the app rather than nowhere.
   */
  protected async focusApp(): Promise<void> {
    this.sendKeys.set(true);
    this.lastKeyResult.set(null);

    const result = await this.argus.focusWindow(this.handle());
    if (!result.focused && result.reason) this.lastKeyResult.set(result.reason);

    // Focusing a real input is what brings up the soft keyboard on a phone. Done last so the
    // browser keeps this input focused regardless of what the foregrounding did.
    this.keyboardSink()?.nativeElement.focus();
  }

  protected onKeyDown(event: KeyboardEvent): void {
    if (!this.sendKeys()) return;
    if (SWALLOWED_CODES.has(event.code)) return;

    event.preventDefault();
    void this.dispatch(event, 0);
  }

  protected onKeyUp(event: KeyboardEvent): void {
    if (!this.sendKeys()) return;
    if (SWALLOWED_CODES.has(event.code)) return;

    event.preventDefault();
    void this.dispatch(event, 1);
  }

  /**
   * Soft keyboards on phones frequently report code/key as "Unidentified" on keydown, so the text
   * they insert is picked up here instead and replayed as character events.
   */
  protected onBeforeInput(event: Event): void {
    if (!this.sendKeys()) return;

    const input = event as InputEvent;
    if (!input.data) return;

    event.preventDefault();
    for (const character of input.data) {
      void this.dispatchCharacter(character);
    }
  }

  private async dispatch(event: KeyboardEvent, type: 0 | 1): Promise<void> {
    const payload: KeyEventDto = {
      windowId: String(this.handle()),
      type,
      code: event.code,
      key: event.key,
      ctrl: event.ctrlKey,
      shift: event.shiftKey,
      alt: event.altKey,
      meta: event.metaKey,
    };
    await this.send(payload);
  }

  private async dispatchCharacter(character: string): Promise<void> {
    const payload: KeyEventDto = {
      windowId: String(this.handle()),
      type: 0,
      code: codeForCharacter(character),
      key: character,
      ctrl: false,
      shift: false,
      alt: false,
      meta: false,
    };
    await this.send(payload);
  }

  private async send(payload: KeyEventDto): Promise<void> {
    // Always Focus mode: the Focus App button has already foregrounded the window, and SendInput
    // is the only backend that reaches every app once it is there.
    const result = await this.argus.sendKey(payload, InjectionMode.Focus);
    if (!result.delivered && result.reason) {
      this.lastKeyResult.set(result.reason);
    } else if (result.delivered) {
      this.lastKeyResult.set(null);
    }
  }

  protected clearKeyboardSink(): void {
    const sink = this.keyboardSink()?.nativeElement;
    if (sink) sink.value = '';
  }

  protected async back(): Promise<void> {
    await this.router.navigate(['/dashboard']);
  }
}

/** PointerEvent.button: 0 left, 1 middle, 2 right. */
function buttonFor(button: number): MouseButton {
  if (button === 2) return MouseButton.Right;
  if (button === 1) return MouseButton.Middle;
  return MouseButton.Left;
}

function clamp(value: number, min: number, max: number): number {
  return Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : min;
}

/** Phones and slow links start lower; a viewer can always raise it. */
function defaultQuality(): QualityLevel {
  const coarse = window.matchMedia?.('(pointer: coarse)').matches ?? false;
  const narrow = window.innerWidth < 700;
  return coarse || narrow ? QualityLevel.Low : QualityLevel.Medium;
}

function codeForCharacter(character: string): string {
  if (character >= 'a' && character <= 'z') return `Key${character.toUpperCase()}`;
  if (character >= 'A' && character <= 'Z') return `Key${character}`;
  if (character >= '0' && character <= '9') return `Digit${character}`;

  const punctuation: Record<string, string> = {
    ' ': 'Space',
    '-': 'Minus',
    '=': 'Equal',
    '.': 'Period',
    ',': 'Comma',
    '/': 'Slash',
    '\\': 'Backslash',
    ';': 'Semicolon',
    "'": 'Quote',
    '[': 'BracketLeft',
    ']': 'BracketRight',
    '`': 'Backquote',
  };
  return punctuation[character] ?? 'Unidentified';
}
