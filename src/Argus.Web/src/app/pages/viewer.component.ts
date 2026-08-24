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
import { SessionService } from '../core/session.service';
import { SettingsService } from '../core/settings.service';
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
  /** Moving this pointer pans the picture instead of reaching the host. */
  panning: boolean;
  startX: number;
  startY: number;
  button: MouseButton;
  /** Whether a button is currently held down on the host. */
  pressed: boolean;
  longPressFired: boolean;
  /** Travelled past the drag threshold, so releasing is no longer a tap. */
  moved: boolean;
  timer?: ReturnType<typeof setTimeout>;
}

/**
 * Whether this device has a touch screen at all - true on a phone, a tablet, and a touch laptop.
 *
 * Only decides whether the touch-only controls are worth toolbar space. What a given gesture does
 * is decided per event from PointerEvent.pointerType, so a hybrid laptop behaves like a mouse when
 * you use the mouse and like a tablet when you use the screen.
 */
const TOUCH_DEVICE = window.matchMedia?.('(any-pointer: coarse)').matches ?? false;

/** Keys we never forward - they would fight the browser rather than reach the app. */
const SWALLOWED_CODES = new Set(['F5', 'F11', 'F12']);

/** A key on the floating pad: what it says, and the KeyboardEvent.code it sends. */
interface PadKey {
  label: string;
  code: string;
  /** Set for the keys that should be visually wider than one unit. */
  wide?: boolean;
}

/** How far the pad may be dragged in each direction before it would leave the stage. */
interface PadBounds {
  minX: number;
  maxX: number;
  minY: number;
  maxY: number;
}

type ModifierName = 'ctrl' | 'alt' | 'shift' | 'meta';

/**
 * Modifiers are sent as ordinary key presses around the key they modify - the ctrl/shift/alt flags
 * on KeyEventDto only decide whether an event counts as printable, they do not press anything.
 */
const MODIFIER_CODES: Record<ModifierName, string> = {
  ctrl: 'ControlLeft',
  alt: 'AltLeft',
  shift: 'ShiftLeft',
  meta: 'MetaLeft',
};

/** 0 off, 1 latched (released after the next key), 2 locked (held until tapped off). */
type ModifierState = 0 | 1 | 2;

/** How long a pad key is held before it starts repeating, and how fast it repeats after that. */
const KEY_REPEAT_DELAY_MS = 400;
const KEY_REPEAT_MS = 90;

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
  private readonly settings = inject(SettingsService);
  private readonly session = inject(SessionService);

  private readonly keyboardSink = viewChild<ElementRef<HTMLInputElement>>('keyboardSink');
  private readonly frameView = viewChild(FrameViewComponent);
  private readonly viewerEl = viewChild<ElementRef<HTMLElement>>('viewer');
  private readonly stageEl = viewChild<ElementRef<HTMLElement>>('stage');
  private readonly keyPad = viewChild<ElementRef<HTMLElement>>('keyPad');
  private readonly sendTextBox = viewChild<ElementRef<HTMLTextAreaElement>>('sendTextBox');

  protected readonly handle = signal(0);
  protected readonly zoom = signal(1);
  protected readonly panX = signal(0);
  protected readonly panY = signal(0);
  protected readonly quality = signal<QualityLevel>(QualityLevel.High);
  protected readonly sendKeys = signal(false);
  protected readonly lastKeyResult = signal<string | null>(null);
  protected readonly isFullscreen = signal(false);
  protected readonly fitted = signal(false);
  protected readonly menu = signal<'quality' | 'size' | null>(null);

  /** The Send Text panel: its own overlay rather than a menu, because it holds an editable field. */
  protected readonly sendTextOpen = signal(false);
  protected readonly textToSend = signal('');
  protected readonly sending = signal(false);

  /** Remembered per device, like the other viewer toggles - see SettingsService. */
  protected readonly sendTextEnter = this.settings.sendTextEnter;

  protected readonly canSendText = computed(() => this.textToSend().length > 0 && !this.sending());

  protected readonly touchDevice = TOUCH_DEVICE;

  /**
   * What a one-finger drag means. Clicking never depends on it: a tap is a left click and a hold
   * is a right click either way. A finger just cannot mean both "move the picture" and "drag
   * something on the host", so dragging something over there is the one thing that needs saying
   * out loud. A mouse has buttons and a Shift key, so it never needs this.
   */
  protected readonly dragToHost = signal(false);

  /** Both floating overlays are remembered per device - see SettingsService. */
  protected readonly showScrollButtons = this.settings.showScrollButtons;
  protected readonly showKeyPad = this.settings.showKeyPad;

  /**
   * Whether this device's own keyboard types into the app while the pad is open.
   *
   * Off, the pad is only its buttons: the physical keyboard in front of you stays the browser's,
   * and text goes to the app through Send Text instead. That distinction only matters on a machine
   * that has a real keyboard - on a phone the pad and the soft keyboard arrive together - which is
   * why it is a switch rather than a rule. Remembered per device, like the pad itself.
   */
  protected readonly allowTyping = this.settings.allowTyping;

  /** The F row doubles the pad's height, so it is opened only when wanted. */
  protected readonly showFunctionKeys = signal(false);

  /** Collapsed to its handle: still on screen and still draggable, but out of the picture's way. */
  protected readonly padMinimized = signal(false);

  /**
   * How far the pad has been dragged from where the stylesheet puts it. Deliberately not persisted:
   * a new viewer starts with the pad back at the bottom centre.
   */
  protected readonly padOffset = signal({ x: 0, y: 0 });

  protected readonly padTransform = computed(() => {
    const { x, y } = this.padOffset();
    // The -50% is the stylesheet's own centring; the drag rides on top of it.
    return `translate(calc(-50% + ${x}px), ${y}px)`;
  });

  protected readonly modifiers = signal<Record<ModifierName, ModifierState>>({
    ctrl: 0,
    alt: 0,
    shift: 0,
    meta: 0,
  });

  protected readonly modifierKeys: { name: ModifierName; label: string }[] = [
    { name: 'ctrl', label: 'Ctrl' },
    { name: 'alt', label: 'Alt' },
    { name: 'shift', label: 'Shift' },
    { name: 'meta', label: 'Win' },
  ];

  /** The keys a tablet's own keyboard cannot produce, grouped the way a keyboard groups them. */
  protected readonly editKeys: PadKey[] = [
    { label: 'Esc', code: 'Escape' },
    { label: 'Tab', code: 'Tab' },
    { label: 'Enter', code: 'Enter', wide: true },
    { label: 'Bksp', code: 'Backspace' },
    { label: 'Del', code: 'Delete' },
    { label: 'Ins', code: 'Insert' },
  ];

  protected readonly navKeys: PadKey[] = [
    { label: 'Home', code: 'Home' },
    { label: 'End', code: 'End' },
    { label: 'PgUp', code: 'PageUp' },
    { label: 'PgDn', code: 'PageDown' },
    { label: 'Menu', code: 'ContextMenu' },
    { label: 'PrtSc', code: 'PrintScreen' },
  ];

  protected readonly arrowKeys: PadKey[] = [
    { label: '←', code: 'ArrowLeft' },
    { label: '↑', code: 'ArrowUp' },
    { label: '↓', code: 'ArrowDown' },
    { label: '→', code: 'ArrowRight' },
  ];

  /**
   * Two rows of six rather than one row of twelve. Twelve keys wide is wider than the rest of the
   * pad, so it wraps on its own - and where it wraps depends on the screen, which leaves a single
   * orphaned F12 on some of them. Splitting it deliberately also matches how a keyboard groups them.
   */
  protected readonly functionRows: PadKey[][] = [
    Array.from({ length: 6 }, (_, i) => ({ label: `F${i + 1}`, code: `F${i + 1}` })),
    Array.from({ length: 6 }, (_, i) => ({ label: `F${i + 7}`, code: `F${i + 7}` })),
  ];

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
    // Phone-shaped, so a desktop app that has a responsive layout shows its narrow one and the
    // whole window fits a tablet screen without zooming out.
    { label: 'Mobile · 412 × 915', value: '412x915' },
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

  private pendingScroll: { notches: number; clientX: number; clientY: number } | null = null;
  private scrollFrame = 0;
  private scrollRepeat?: ReturnType<typeof setInterval>;

  /** The pad key currently held down, so it can be released even if the pointerup goes astray. */
  private heldKey: { code: string; modifiers: ModifierName[] } | null = null;
  private keyRepeat?: ReturnType<typeof setTimeout>;

  /** The in-progress pad drag. Bounds are measured once at grab time, not on every move. */
  private padDrag: {
    pointerId: number;
    startX: number;
    startY: number;
    originX: number;
    originY: number;
    bounds: PadBounds;
  } | null = null;

  /** Modifiers physically down on the host right now, because they were locked on. */
  private readonly heldModifiers = new Set<ModifierName>();

  /**
   * Key events have to reach the host in the order they were made. Each one is a hub round trip,
   * so a fast tap could otherwise have its key-up overtake its key-down and strand the key down.
   */
  private padChain: Promise<void> = Promise.resolve();

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

    // The lock screen is drawn over this page rather than routed to, so the viewer keeps running
    // behind it with its document key listeners attached. Armed typing there cancels every key on
    // the password box and forwards it to the host instead - disarm the moment the session locks.
    effect(() => {
      if (!this.session.locked()) return;

      this.releasePadKey();
      this.clearModifiers();
      this.sendKeys.set(false);
      this.keyboardSink()?.nativeElement.blur();
    });

    // Release keys clears the host side globally, from any page. The pad has no way of knowing
    // that happened, so without this the row still shows Ctrl locked and the next pad key presses
    // it straight back down - the button would look like it did nothing.
    effect(() => {
      this.argus.keysReleased();
      this.forgetHeldInput();
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
    // Capture, and on the document rather than on the sink input - see onKeyDown.
    document.addEventListener('keydown', this.onKeyDown, true);
    document.addEventListener('keyup', this.onKeyUp, true);
    document.addEventListener('visibilitychange', this.onVisibilityChange);
    window.addEventListener('pagehide', this.onPageHide);
    await this.argus.refreshStatuses();

    // The pad is remembered per device, so a viewer can open with it already showing and the icon
    // already lit. Arm typing to match, rather than leaving it dead until the pad is touched.
    // Next frame, because the sink input is not in the DOM yet during ngOnInit.
    if (this.showKeyPad() && this.allowTyping()) requestAnimationFrame(() => void this.focusApp());
  }

  ngOnDestroy(): void {
    // Leaving the page mid-drag would otherwise strand a held mouse button on the host, and a pad
    // key or a locked modifier still down is worse than a stuck click: every subsequent keystroke
    // on the machine becomes a shortcut.
    this.releaseEverything();
    document.removeEventListener('fullscreenchange', this.onFullscreenChange);
    window.removeEventListener('resize', this.onViewportResize);
    document.removeEventListener('keydown', this.onKeyDown, true);
    document.removeEventListener('keyup', this.onKeyUp, true);
    document.removeEventListener('visibilitychange', this.onVisibilityChange);
    window.removeEventListener('pagehide', this.onPageHide);
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
  /**
   * Backgrounding the tab releases everything this viewer is holding.
   *
   * ngOnDestroy already covers navigating away, and the server releases what a viewer left down
   * when its hub connection drops - but switching to another tab or app on the phone does neither.
   * The locked Ctrl just sits there on the host until you come back.
   *
   * visibilitychange rather than window blur, deliberately. Focus App foregrounds the target window
   * on the host, and when the browser is on that same machine that blurs the browser - so a blur
   * handler would clear the pad's locked modifiers on every Focus App, which is the one thing they
   * exist to survive. document.hidden only turns true when the tab genuinely goes away.
   */
  private readonly onVisibilityChange = (): void => {
    if (document.hidden) this.releaseEverything();
  };

  /** Same for bfcache and real unloads, where visibilitychange is not guaranteed to arrive. */
  private readonly onPageHide = (): void => {
    this.releaseEverything();
  };

  private releaseEverything(): void {
    this.abortMouseGesture();
    this.cancelPendingScroll();
    this.stopScrolling();
    this.releasePadKey();
    this.clearModifiers();
  }

  private readonly onViewportResize = (): void => {
    if (this.fitted()) requestAnimationFrame(() => this.fitToScreen());
    requestAnimationFrame(() => this.clampPadOffset());
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

    this.lastKeyResult.set(
      result.resized ? null : (result.reason ?? 'Could not resize that window'),
    );
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

    this.zoom.set(
      clamp(Math.min(containerW / contentW, containerH / contentH), MIN_ZOOM, MAX_ZOOM),
    );
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
    // The page itself never scrolls here, whichever way this goes.
    event.preventDefault();

    // The wheel belongs to the app being driven, exactly as it would if you were sitting at the
    // machine. Ctrl+wheel is the viewer's own zoom - the same key a browser zooms with, and what
    // a trackpad pinch already sends.
    if (!event.ctrlKey) {
      this.queueScroll(event);
      return;
    }

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

    this.beginMouseGesture(event);
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

    const gesture = this.gesture;

    if (gesture && !gesture.panning) {
      event.preventDefault();
      this.moveMouseGesture(event);
      return;
    }

    if (gesture && !gesture.moved) {
      const travelled = Math.hypot(event.clientX - gesture.startX, event.clientY - gesture.startY);
      if (travelled >= DRAG_THRESHOLD_PX) {
        // Past the threshold this is a drag, not a tap - so it must not click on release, and the
        // pending hold must not fire a right-click into the middle of it.
        gesture.moved = true;
        this.clearLongPress(gesture);
      }
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

  // --------------------------------------------------------- driving the host

  /** Only changes what a one-finger drag means - see dragToHost. */
  protected toggleDragToHost(): void {
    this.dragToHost.update((on) => !on);
    this.abortMouseGesture();
  }

  /**
   * Right-click is sent from the pointer events, so the browser's own menu would be a second menu
   * on top of the app's. The host window is raised by the server before every mouse event
   * (MouseInjector.Send), so nothing here has to foreground it first.
   */
  protected onContextMenu(event: Event): void {
    event.preventDefault();
  }

  private beginMouseGesture(event: PointerEvent): void {
    const view = this.frameView();
    if (!view || !view.contains(event.clientX, event.clientY)) return;

    event.preventDefault();

    const touch = event.pointerType !== 'mouse';
    this.gesture = {
      pointerId: event.pointerId,
      touch,
      // Shift is the mouse's pan modifier, because every plain button belongs to the host now.
      panning: touch ? !this.dragToHost() : event.shiftKey,
      startX: event.clientX,
      startY: event.clientY,
      button: buttonFor(event.button),
      pressed: false,
      longPressFired: false,
      moved: false,
      timer: undefined,
    };

    if (!touch) {
      // Shift+drag pans the picture and nothing else - no button is pressed over there at all.
      if (this.gesture.panning) return;

      // A real mouse says which button it used up front, so press it immediately and let the
      // matching pointerup release it. Drag, select and drag-and-drop all fall out of that.
      this.gesture.pressed = true;
      void this.dispatchMouse(MouseAction.Down, this.gesture.button, event);
      return;
    }

    // A finger does not. Hold it still and it means right-click; let go quickly and it means a
    // plain tap; move it and it means pan, or drag on the host if that is switched on. Nothing is
    // sent until one of those is decided, because pressing a button early would make the
    // long-press a left-press-then-right-click.
    this.gesture.timer = setTimeout(() => {
      const gesture = this.gesture;
      if (!gesture || gesture.pressed) return;

      gesture.longPressFired = true;
      void this.dispatchMouseAt(
        MouseAction.Click,
        MouseButton.Right,
        gesture.startX,
        gesture.startY,
      );
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

    // A mouse either pressed on the way down or was panning, so there is no tap to work out.
    if (!gesture.touch) return;

    // Released before the hold completed and without moving: a tap, which is a left click. A
    // finger that panned the picture is not a tap, however briefly it was down.
    if (!gesture.longPressFired && !gesture.moved) {
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

  /**
   * One flick of a wheel or trackpad fires dozens of events, and every dispatch costs a hub round
   * trip plus a SetForegroundWindow and SetCursorPos on the host. Accumulate here and send at most
   * one event per frame instead.
   */
  private queueScroll(event: WheelEvent): void {
    // Same rule as a click: the letterboxing around the frame is not part of the app.
    const view = this.frameView();
    if (!view || !view.contains(event.clientX, event.clientY)) return;

    this.pendingScroll = {
      notches: (this.pendingScroll?.notches ?? 0) + wheelNotches(event),
      clientX: event.clientX,
      clientY: event.clientY,
    };

    if (this.scrollFrame) return;
    this.scrollFrame = requestAnimationFrame(() => {
      this.scrollFrame = 0;
      this.flushScroll();
    });
  }

  private flushScroll(): void {
    const pending = this.pendingScroll;
    if (!pending) return;

    // Whole notches only, carrying the remainder: a slow trackpad drags in fractions that would
    // otherwise round away to nothing every frame and never scroll at all.
    const notches = Math.trunc(pending.notches);
    if (notches === 0) return;

    pending.notches -= notches;
    void this.dispatchMouseAt(
      MouseAction.Scroll,
      MouseButton.Left,
      pending.clientX,
      pending.clientY,
      notches * WHEEL_DELTA,
    );
  }

  // --------------------------------------------------------- scroll buttons

  /**
   * Scrolls the host from a button instead of the wheel.
   *
   * Held down it repeats, because one notch per tap is not a way to get down a long page. The
   * first notch is sent immediately so a quick tap still does something.
   */
  protected startScrolling(direction: 1 | -1): void {
    this.stopScrolling();
    this.scrollHost(direction);
    this.scrollRepeat = setInterval(() => this.scrollHost(direction), SCROLL_REPEAT_MS);
  }

  protected stopScrolling(): void {
    if (this.scrollRepeat === undefined) return;

    clearInterval(this.scrollRepeat);
    this.scrollRepeat = undefined;
  }

  /**
   * Sent at the middle of the frame: a button press has no cursor position of its own, and the
   * middle is inside whatever the window's main scrollable area is.
   */
  private scrollHost(direction: 1 | -1): void {
    void this.dispatchMouseNormalised(
      MouseAction.Scroll,
      MouseButton.Left,
      0.5,
      0.5,
      direction * BUTTON_SCROLL_NOTCHES * WHEEL_DELTA,
    );
  }

  private cancelPendingScroll(): void {
    if (this.scrollFrame) cancelAnimationFrame(this.scrollFrame);
    this.scrollFrame = 0;
    this.pendingScroll = null;
  }

  private dispatchMouse(
    action: MouseAction,
    button: MouseButton,
    event: PointerEvent,
  ): Promise<void> {
    return this.dispatchMouseAt(action, button, event.clientX, event.clientY);
  }

  private async dispatchMouseAt(
    action: MouseAction,
    button: MouseButton,
    clientX: number,
    clientY: number,
    delta = 0,
  ): Promise<void> {
    const view = this.frameView();
    if (!view) return;

    const point = view.normalise(clientX, clientY);
    await this.dispatchMouseNormalised(action, button, point.x, point.y, delta);
  }

  /** Takes 0..1 fractions of the frame, for the callers that have no screen point to start from. */
  private async dispatchMouseNormalised(
    action: MouseAction,
    button: MouseButton,
    x: number,
    y: number,
    delta = 0,
  ): Promise<void> {
    const result = await this.argus.sendMouse({
      windowId: String(this.handle()),
      action,
      button,
      x,
      y,
      delta,
    });

    if (!result.delivered && result.reason) {
      this.lastKeyResult.set(result.reason);
    } else if (result.delivered) {
      this.lastKeyResult.set(null);
    }
  }

  // -------------------------------------------------------------- key pad

  /**
   * Both overlays sit on top of the stream, so they are toggles rather than always-on: on a phone
   * in portrait they would cover a third of the picture.
   */
  protected toggleScrollButtons(): void {
    this.showScrollButtons.update((on) => !on);
    if (!this.showScrollButtons()) this.stopScrolling();
  }

  /**
   * With Allow Type on, the pad is the switch for typing as well as a row of extra keys: showing it
   * foregrounds the app on the host and arms the keyboard, hiding it puts everything back down.
   * With Allow Type off it is only the buttons, so showing it arms nothing.
   */
  protected toggleKeyPad(): void {
    this.showKeyPad.update((on) => !on);

    if (this.showKeyPad()) {
      if (this.allowTyping()) void this.focusApp();
      return;
    }

    // Hiding the pad takes its buttons out of the DOM, so anything still held would never get its
    // key-up. Release before it disappears.
    this.releasePadKey();
    this.clearModifiers();

    // And with no pad there is nothing to type into, so the sink lets go - which is also what
    // takes the browser's own autofill bar off the bottom of a tablet screen.
    this.sendKeys.set(false);
    this.keyboardSink()?.nativeElement.blur();
  }

  /** Collapses the pad to its top row, or opens it back up. Either way the app keeps the keyboard. */
  protected toggleMinimize(): void {
    this.padMinimized.update((on) => !on);

    // Same rule as hiding: a collapsed pad has no modifier keys to tap off, and a locked one is
    // physically down on the host until something releases it.
    if (this.padMinimized()) {
      this.releasePadKey();
      this.clearModifiers();
    }

    // A shorter pad can sit where a taller one could not, and the reverse.
    requestAnimationFrame(() => this.clampPadOffset());
    if (this.allowTyping()) void this.focusApp();
  }

  /**
   * Allow Type: whether the keyboard in front of you belongs to the app or to the browser.
   *
   * Turning it off is the point of the switch - a real keyboard next to a live viewer types into
   * whatever is on the host by accident, and there is now a Send Text box for the times you mean
   * it. Turning it back on arms typing immediately rather than waiting for the pad to be touched,
   * because the tap that turned it on is the request.
   */
  protected toggleAllowTyping(): void {
    this.allowTyping.update((on) => !on);

    if (this.allowTyping()) {
      void this.focusApp();
      return;
    }

    // Disarm now, and let the sink go so a phone's soft keyboard and its autofill bar drop away.
    // Locked modifiers stay: they belong to the pad's own keys, which still work.
    this.sendKeys.set(false);
    this.lastKeyResult.set(null);
    this.keyboardSink()?.nativeElement.blur();
  }

  protected toggleFunctionKeys(): void {
    this.showFunctionKeys.update((on) => !on);
    requestAnimationFrame(() => this.clampPadOffset());
  }

  /**
   * The pad must never take focus off the sink input: a blurred sink is a soft keyboard that drops
   * away and physical keys that go nowhere, which is why every key on the pad is driven from
   * pointerdown and why the default action - focusing the button that was tapped - is cancelled
   * here for the whole pad.
   */
  protected keepTyping(event: Event): void {
    event.preventDefault();

    // With Allow Type off there is no keyboard to keep: cancelling the default action above is the
    // whole job, so a tapped button does not sit there focused and swallow the next Enter.
    if (!this.allowTyping()) return;

    // Touching the pad at all is a request for the app: with the pad open on a viewer that has not
    // armed typing yet, this is what arms it. focusApp sets the flag first, so it runs once.
    if (!this.sendKeys()) {
      void this.focusApp();
      return;
    }

    this.restoreTyping();
  }

  /**
   * The repair for a tap that got focus anyway. Runs on click, which on a touch screen arrives
   * after the browser has moved focus, so it is the only place that can put it back.
   */
  protected restoreTyping(): void {
    if (!this.sendKeys()) return;

    const sink = this.keyboardSink()?.nativeElement;
    if (sink && document.activeElement !== sink) sink.focus({ preventScroll: true });
  }

  protected startPadDrag(event: PointerEvent): void {
    const bounds = this.padBounds(this.padOffset());
    if (!bounds) return;

    event.preventDefault();
    // Capture so the pad keeps following the finger even once it is off the grip.
    (event.currentTarget as Element).setPointerCapture?.(event.pointerId);

    const offset = this.padOffset();
    this.padDrag = {
      pointerId: event.pointerId,
      startX: event.clientX,
      startY: event.clientY,
      originX: offset.x,
      originY: offset.y,
      bounds,
    };
  }

  protected movePadDrag(event: PointerEvent): void {
    const drag = this.padDrag;
    if (!drag || event.pointerId !== drag.pointerId) return;

    event.preventDefault();
    const { bounds } = drag;
    this.padOffset.set({
      x: clamp(drag.originX + event.clientX - drag.startX, bounds.minX, bounds.maxX),
      y: clamp(drag.originY + event.clientY - drag.startY, bounds.minY, bounds.maxY),
    });
  }

  protected endPadDrag(event: PointerEvent): void {
    if (this.padDrag?.pointerId !== event.pointerId) return;
    this.padDrag = null;
  }

  /**
   * Measured rather than derived from the stylesheet, so the pad stays inside the stage whatever
   * the media queries did to its size and wherever the CSS anchors it.
   */
  private padBounds(offset: { x: number; y: number }): PadBounds | null {
    const pad = this.keyPad()?.nativeElement;
    const stage = this.stageEl()?.nativeElement;
    if (!pad || !stage) return null;

    const padRect = pad.getBoundingClientRect();
    const stageRect = stage.getBoundingClientRect();

    // Where the pad would sit with no drag applied at all.
    const restLeft = padRect.left - offset.x;
    const restTop = padRect.top - offset.y;

    const minX = stageRect.left - restLeft;
    const minY = stageRect.top - restTop;

    return {
      minX,
      minY,
      // A pad bigger than the stage pins to the top left rather than inverting the range.
      maxX: Math.max(minX, stageRect.right - padRect.width - restLeft),
      maxY: Math.max(minY, stageRect.bottom - padRect.height - restTop),
    };
  }

  /** A rotation or a fullscreen change can leave a dragged pad hanging off the stage. */
  private clampPadOffset(): void {
    const offset = this.padOffset();
    const bounds = this.padBounds(offset);
    if (!bounds) return;

    const x = clamp(offset.x, bounds.minX, bounds.maxX);
    const y = clamp(offset.y, bounds.minY, bounds.maxY);
    if (x !== offset.x || y !== offset.y) this.padOffset.set({ x, y });
  }

  /**
   * Modifiers cycle off - latched - locked.
   *
   * Latched applies to the next key press only, which is what Ctrl+C wants. Locked holds the key
   * down on the host until it is tapped off, which is what Alt+Tab+Tab wants - releasing Alt
   * between the two Tabs would switch back and forth between the same pair of windows instead of
   * walking down the list.
   */
  protected cycleModifier(name: ModifierName): void {
    const current = this.modifiers()[name];
    const next: ModifierState = current === 0 ? 1 : current === 1 ? 2 : 0;

    this.modifiers.update((state) => ({ ...state, [name]: next }));

    if (next === 2) {
      this.heldModifiers.add(name);
      this.queueKey(MODIFIER_CODES[name], 0);
    } else if (current === 2) {
      this.heldModifiers.delete(name);
      this.queueKey(MODIFIER_CODES[name], 1);
    }
  }

  protected modifierState(name: ModifierName): ModifierState {
    return this.modifiers()[name];
  }

  /**
   * Press is separate from release so holding a pad key repeats, the way holding a real key does -
   * one Backspace per tap is not a way to clear a line of text.
   */
  protected pressPadKey(key: PadKey, event: PointerEvent): void {
    event.preventDefault();
    // Capture so the key-up still arrives if the finger slides off the button while held.
    (event.target as Element).setPointerCapture?.(event.pointerId);

    this.releasePadKey();

    // Locked modifiers are already down on the host; only the latched ones are wrapped around
    // this press, and they come back up with it.
    const state = this.modifiers();
    const latched = this.modifierKeys.map((m) => m.name).filter((name) => state[name] === 1);

    this.heldKey = { code: key.code, modifiers: latched };

    for (const name of latched) this.queueKey(MODIFIER_CODES[name], 0);
    this.queueKey(key.code, 0);

    this.keyRepeat = setTimeout(() => {
      // Auto-repeat is just more key-downs with no key-up between them.
      this.keyRepeat = setInterval(() => this.queueKey(key.code, 0), KEY_REPEAT_MS);
    }, KEY_REPEAT_DELAY_MS);
  }

  protected releasePadKey(): void {
    this.stopKeyRepeat();

    const held = this.heldKey;
    if (!held) return;
    this.heldKey = null;

    this.queueKey(held.code, 1);
    for (const name of [...held.modifiers].reverse()) this.queueKey(MODIFIER_CODES[name], 1);

    if (held.modifiers.length > 0) {
      this.modifiers.update((state) => {
        const next = { ...state };
        for (const name of held.modifiers) next[name] = 0;
        return next;
      });
    }
  }

  private stopKeyRepeat(): void {
    if (this.keyRepeat === undefined) return;

    // The same handle is a timeout before the delay elapses and an interval after it; clearing
    // both is how you cancel it without tracking which one it currently is.
    clearTimeout(this.keyRepeat);
    clearInterval(this.keyRepeat as ReturnType<typeof setInterval>);
    this.keyRepeat = undefined;
  }

  /**
   * Drops this viewer's idea of what is held, sending nothing.
   *
   * The counterpart to clearModifiers: there the host still has the keys down and needs the
   * key-ups; here Release keys has already lifted them, and queueing more would be a second set of
   * key-ups aimed at a keyboard that is already clear.
   */
  private forgetHeldInput(): void {
    this.stopKeyRepeat();
    this.heldKey = null;
    this.heldModifiers.clear();
    this.modifiers.set({ ctrl: 0, alt: 0, shift: 0, meta: 0 });

    // Same for a drag: the host button is already up, so forget the gesture rather than sending
    // an Up that would land as a stray click wherever the cursor now is.
    if (this.gesture) {
      this.clearLongPress(this.gesture);
      this.gesture = null;
    }
  }

  /** Lifts any locked modifier off the host and blanks the row. */
  private clearModifiers(): void {
    for (const name of this.heldModifiers) this.queueKey(MODIFIER_CODES[name], 1);
    this.heldModifiers.clear();
    this.modifiers.set({ ctrl: 0, alt: 0, shift: 0, meta: 0 });
  }

  /**
   * Key is set to the code rather than a character on purpose: the server treats a one-character
   * Key as printable text and would type "Enter" nothing / the wrong glyph instead of mapping the
   * virtual key. Anything longer falls through to the code-driven VK mapping.
   */
  private queueKey(code: string, type: 0 | 1): void {
    this.padChain = this.padChain
      .then(() =>
        this.send({
          windowId: String(this.handle()),
          type,
          code,
          key: code,
          ctrl: false,
          shift: false,
          alt: false,
          meta: false,
        }),
      )
      .catch(() => {
        // A failed send is already reported through lastKeyResult; the queue must not die with it.
      });
  }

  // -------------------------------------------------------------- send text

  /**
   * Types a block of text into the app in one go, rather than a key at a time.
   *
   * Typing live from a phone means a hub round trip per character and a soft keyboard covering the
   * picture while you do it, and a URL or a command line is exactly the kind of thing that is
   * easier to compose in a box, check, and then hand over. Hit Enter is what turns that into
   * "run it" for a terminal or "go" for an address bar.
   */
  protected openSendText(): void {
    this.sendTextOpen.set(true);
    this.lastKeyResult.set(null);

    // Nothing held may survive the panel: keys typed into the box do not reach the host, so a
    // locked Ctrl would still be down over there when the text arrives.
    this.releasePadKey();
    this.clearModifiers();

    // The box takes the keyboard off the sink, so the soft keyboard on a phone types here rather
    // than into the app. Next frame, because it is not in the DOM until the panel renders.
    requestAnimationFrame(() => this.sendTextBox()?.nativeElement.focus());
  }

  /** Leaves the text where it is: a panel closed by accident should not lose what was typed. */
  protected closeSendText(): void {
    if (!this.sendTextOpen()) return;

    this.sendTextOpen.set(false);
    // The key pad puts typing back where it was, if it is still on.
    if (this.showKeyPad()) requestAnimationFrame(() => this.restoreTyping());
  }

  protected onSendTextInput(value: string): void {
    this.textToSend.set(value);
  }

  protected toggleSendTextEnter(): void {
    this.sendTextEnter.update((on) => !on);
  }

  /**
   * Hands the whole block to the server, which foregrounds the window once and replays it with
   * SendInput - see ForegroundInjector.TrySendText.
   *
   * The panel closes on success so the app is visible again, and stays open with the text intact
   * on failure, because the text is the thing worth keeping when a send does not land.
   */
  protected async sendText(): Promise<void> {
    if (!this.canSendText()) return;

    this.sending.set(true);
    try {
      const result = await this.argus.sendText(
        this.handle(),
        this.textToSend(),
        this.sendTextEnter(),
      );

      if (!result.delivered) {
        this.lastKeyResult.set(result.reason ?? 'Could not type that into the window');
        return;
      }

      this.lastKeyResult.set(null);
      this.textToSend.set('');
      this.sendTextOpen.set(false);

      // The window is in the foreground on the host now, so leave typing armed the way the key
      // pad would - the next keystroke here lands in the app rather than nowhere.
      if (this.showKeyPad()) requestAnimationFrame(() => this.restoreTyping());
    } finally {
      this.sending.set(false);
    }
  }

  // ------------------------------------------------------------------ input

  /**
   * Brings the target window to the foreground on the host desktop and arms typing here, so the
   * next keystroke lands in the app rather than nowhere. Reached through the key pad - showing it,
   * un-minimizing it or touching it is what asks for the app.
   */
  private async focusApp(): Promise<void> {
    this.sendKeys.set(true);
    this.lastKeyResult.set(null);

    // Focusing a real input is what brings up the soft keyboard on a phone, and it only counts
    // while the tap that got here is still being handled - after the await below it no longer is.
    this.keyboardSink()?.nativeElement.focus();

    const result = await this.argus.focusWindow(this.handle());
    if (!result.focused && result.reason) this.lastKeyResult.set(result.reason);

    // Again once the host has been foregrounded, so the input keeps focus whatever that did.
    this.keyboardSink()?.nativeElement.focus();
  }

  /**
   * Typing is armed for the page, not for the hidden input.
   *
   * Listening on the sink alone only worked while the sink held focus, and on a desktop the very
   * next click took it away - a toolbar button takes focus itself, and the stage is not focusable
   * so clicking the picture hands focus to the body. The pad still said it was on and every
   * keystroke after that went nowhere. The sink stays for what it is actually needed for: raising
   * a phone's soft keyboard, and the beforeinput events that come back from it.
   *
   * Capture phase, so a control that happens to have focus - the zoom slider - does not act on the
   * key before it is cancelled here.
   */
  private readonly onKeyDown = (event: KeyboardEvent): void => {
    // The Send Text box is a field on this page, not a keyboard for the host: forwarding while it
    // is open would type every character into the app as well as into the box.
    if (this.sendTextOpen()) return;
    // Belt and braces with the disarming effect, which only runs once change detection flushes:
    // a key pressed in between must not reach the host, least of all one meant for the password.
    if (this.session.locked()) return;
    if (!this.sendKeys()) return;
    if (SWALLOWED_CODES.has(event.code)) return;

    event.preventDefault();
    void this.dispatch(event, 0);
  };

  private readonly onKeyUp = (event: KeyboardEvent): void => {
    if (this.sendTextOpen()) return;
    if (this.session.locked()) return;
    if (!this.sendKeys()) return;
    if (SWALLOWED_CODES.has(event.code)) return;

    event.preventDefault();
    void this.dispatch(event, 1);
  };

  /**
   * Soft keyboards on phones frequently report code/key as "Unidentified" on keydown, so the text
   * they insert is picked up here instead and replayed as character events.
   */
  protected onBeforeInput(event: Event): void {
    if (this.sendTextOpen()) return;
    if (this.session.locked()) return;
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

/**
 * How far one wheel notch turns the app being driven, in the WHEEL_DELTA units Windows expects.
 */
const WHEEL_DELTA = 120;

/** Pixels of deltaY that count as one notch on a device that reports in pixels. */
const NOTCH_PIXELS = 100;

/** Notches per press of a scroll button, and how often a held button sends another. */
const BUTTON_SCROLL_NOTCHES = 3;
const SCROLL_REPEAT_MS = 200;

/**
 * A wheel event's deltaY in notches, sign-corrected for Windows.
 *
 * deltaMode decides the unit and it varies by device and browser: pixels on a trackpad, lines on
 * most wheels, pages on a few. Browsers count scrolling down as positive; the Windows wheel counts
 * it as negative, hence the flip.
 */
function wheelNotches(event: WheelEvent): number {
  const perUnit =
    event.deltaMode === WheelEvent.DOM_DELTA_LINE
      ? 1
      : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
        ? 3
        : 1 / NOTCH_PIXELS;

  return -event.deltaY * perUnit;
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
