import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  effect,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { ArgusService } from './argus.service';
import { Frame } from './models';

/**
 * Draws one window's live frames onto a canvas.
 *
 * Decoding is done with createImageBitmap, which decodes off the main thread. A decode already in
 * flight causes the next frame to be dropped rather than queued - on a slow phone, queueing would
 * build an ever-growing backlog and show video that falls further behind real time.
 */
@Component({
  selector: 'argus-frame-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div #shell class="frame-shell" [class.empty]="!hasFrame()">
      <!-- translate before scale, so a pan offset stays in screen pixels and tracks the finger
           1:1 at any zoom level. -->
      <canvas
        #canvas
        [style.transform]="
          'translate(' + panX() + 'px,' + panY() + 'px) scale(' + zoom() + ')'
        "
      ></canvas>
      @if (!hasFrame()) {
        <span class="placeholder">{{ placeholder() }}</span>
      }
    </div>
  `,
  styles: [
    `
      :host {
        display: block;
        width: 100%;
        height: 100%;
      }
      .frame-shell {
        position: relative;
        width: 100%;
        height: 100%;
        display: grid;
        place-items: center;
        overflow: hidden;
        background: var(--surface-sunken, #0d1117);
      }
      /* Absolutely positioned so the canvas's intrinsic size (the captured window's pixel
         dimensions) never feeds back into layout. In flow it would stretch a tile past the
         aspect-ratio box its grid cell sets, and tiles would end up different heights. */
      canvas {
        position: absolute;
        inset: 0;
        margin: auto;
        max-width: 100%;
        max-height: 100%;
        display: block;
        transform-origin: center center;
        image-rendering: auto;
      }
      .frame-shell.empty canvas {
        display: none;
      }
      .placeholder {
        color: var(--text-muted, #7d8590);
        font-size: 0.8rem;
        text-align: center;
        padding: 0.5rem;
      }
    `,
  ],
})
export class FrameViewComponent implements OnDestroy {
  readonly handle = input.required<number>();
  readonly zoom = input(1);
  readonly panX = input(0);
  readonly panY = input(0);
  readonly placeholder = input('Waiting for frames…');

  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');
  private readonly shellRef = viewChild.required<ElementRef<HTMLElement>>('shell');
  readonly hasFrame = signal(false);

  /** Pixel size of the frames currently arriving - changes on a quality switch or a host resize. */
  readonly frameSize = signal({ width: 0, height: 0 });

  /**
   * Untransformed geometry, which is what pan clamping needs.
   *
   * offsetWidth/Height rather than getBoundingClientRect: the latter reports the *transformed*
   * box, so feeding it back into the transform that produced it compounds every frame.
   */
  metrics(): { containerW: number; containerH: number; contentW: number; contentH: number } {
    const shell = this.shellRef().nativeElement;
    const canvas = this.canvasRef().nativeElement;
    return {
      containerW: shell.clientWidth,
      containerH: shell.clientHeight,
      contentW: canvas.offsetWidth,
      contentH: canvas.offsetHeight,
    };
  }

  /** Centre of the stage in client coordinates - the origin the transform scales about. */
  centre(): { x: number; y: number } {
    const rect = this.shellRef().nativeElement.getBoundingClientRect();
    return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
  }

  /**
   * Where a screen point falls on the captured frame, as a 0..1 fraction, clamped to the edges.
   *
   * getBoundingClientRect here on purpose - unlike metrics(), this wants the *transformed* box,
   * because that is the pixels the user is actually pointing at. Zoom and pan therefore need no
   * arithmetic of their own: the browser has already applied them.
   */
  normalise(clientX: number, clientY: number): { x: number; y: number } {
    const rect = this.canvasRef().nativeElement.getBoundingClientRect();
    if (rect.width <= 0 || rect.height <= 0) return { x: 0, y: 0 };

    return {
      x: clamp01((clientX - rect.left) / rect.width),
      y: clamp01((clientY - rect.top) / rect.height),
    };
  }

  /** True when the point is on the frame itself rather than the letterboxing around it. */
  contains(clientX: number, clientY: number): boolean {
    const rect = this.canvasRef().nativeElement.getBoundingClientRect();
    return (
      rect.width > 0 &&
      rect.height > 0 &&
      clientX >= rect.left &&
      clientX <= rect.right &&
      clientY >= rect.top &&
      clientY <= rect.bottom
    );
  }

  private unsubscribe?: () => void;
  private context: CanvasRenderingContext2D | null = null;
  private decoding = false;

  constructor(private readonly argus: ArgusService) {
    effect((onCleanup) => {
      const handle = this.handle();
      this.hasFrame.set(false);
      this.unsubscribe = this.argus.onFrame(handle, (frame) => this.draw(frame));
      onCleanup(() => this.unsubscribe?.());
    });
  }

  private draw(frame: Frame): void {
    if (this.decoding) return; // drop rather than queue
    this.decoding = true;

    const blob = new Blob([frame.bytes as unknown as BlobPart], { type: 'image/jpeg' });
    createImageBitmap(blob)
      .then((bitmap) => {
        const canvas = this.canvasRef().nativeElement;
        if (canvas.width !== frame.width || canvas.height !== frame.height) {
          canvas.width = frame.width;
          canvas.height = frame.height;
          this.context = null;
          this.frameSize.set({ width: frame.width, height: frame.height });
        }

        this.context ??= canvas.getContext('2d', { alpha: false });
        this.context?.drawImage(bitmap, 0, 0);
        bitmap.close();

        if (!this.hasFrame()) this.hasFrame.set(true);
      })
      .catch(() => {
        // A malformed or truncated frame is not worth surfacing; the next one will land.
      })
      .finally(() => {
        this.decoding = false;
      });
  }

  ngOnDestroy(): void {
    this.unsubscribe?.();
  }
}

function clamp01(value: number): number {
  return Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 0;
}
