import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { App } from './app';
import { ArgusService } from './core/argus.service';

/** Stubbed so the shell test never opens a SignalR connection or a WebSocket. */
class ArgusServiceStub {
  readonly agentOnline = signal(false);
  readonly connected = signal(false);
  readonly lastError = signal<string | null>(null);
  readonly keysReleased = signal(0);
  startCalls = 0;
  stopCalls = 0;
  released: string[] = [];
  releaseReason: string | null = null;

  async start(): Promise<void> {
    this.startCalls++;
  }

  stop(): void {
    this.stopCalls++;
  }

  async releaseKeys(): Promise<{ released: string[]; reason?: string | null }> {
    return { released: this.released, reason: this.releaseReason };
  }
}

describe('App shell', () => {
  let stub: ArgusServiceStub;

  beforeEach(async () => {
    stub = new ArgusServiceStub();
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [provideRouter([]), { provide: ArgusService, useValue: stub }],
    }).compileComponents();
  });

  it('creates the shell and starts the connection', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    expect(fixture.componentInstance).toBeTruthy();
    expect(stub.startCalls).toBe(1);
  });

  // The tab strip is gone: picking apps happens on the dashboard itself, so the brand is the only
  // navigation left and it has to keep pointing home.
  it('links home from the brand', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const brand = (fixture.nativeElement as HTMLElement).querySelector('a.brand');

    expect(brand?.getAttribute('href')).toBe('/dashboard');
    expect(brand?.textContent).toContain('Argus');
  });

  it('reports the agent as offline until a heartbeat arrives', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const label = (fixture.nativeElement as HTMLElement).querySelector('.agent-label');
    expect(label?.textContent).toContain('Agent offline');
  });

  // The button is gated on the connection rather than on the lock: with nothing connected there is
  // no keyboard to clear.
  it('offers Release keys only once connected', async () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    expect(element.querySelector('.release-button')).toBeNull();

    stub.connected.set(true);
    fixture.detectChanges();
    await fixture.whenStable();

    expect(element.querySelector('.release-button')).not.toBeNull();
  });

  // Telling you it found nothing is as useful as telling you what it fixed.
  it('says so when nothing was stuck', async () => {
    stub.connected.set(true);
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    element.querySelector<HTMLButtonElement>('.release-button')?.click();
    // The click handler awaits the hub call, so the banner is a task away, not a tick away.
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();

    expect(element.querySelector('.banner.good')?.textContent).toContain('Nothing was stuck');
  });

  it('names what it released', async () => {
    stub.connected.set(true);
    stub.released = ['Ctrl', 'Shift'];
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const element = fixture.nativeElement as HTMLElement;
    element.querySelector<HTMLButtonElement>('.release-button')?.click();
    // The click handler awaits the hub call, so the banner is a task away, not a tick away.
    await new Promise((resolve) => setTimeout(resolve, 0));
    fixture.detectChanges();

    expect(element.querySelector('.banner.good')?.textContent).toContain('Released Ctrl, Shift');
  });

  it('shows a banner when the service reports an error', async () => {
    stub.lastError.set('Cannot reach the Argus agent');
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const banner = (fixture.nativeElement as HTMLElement).querySelector('.global-error');
    expect(banner?.textContent).toContain('Cannot reach the Argus agent');
  });
});
