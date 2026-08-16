import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { signal } from '@angular/core';
import { App } from './app';
import { ArgusService } from './core/argus.service';

/** Stubbed so the shell test never opens a SignalR connection or a WebSocket. */
class ArgusServiceStub {
  readonly agentOnline = signal(false);
  readonly lastError = signal<string | null>(null);
  startCalls = 0;

  async start(): Promise<void> {
    this.startCalls++;
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

  it('shows a banner when the service reports an error', async () => {
    stub.lastError.set('Cannot reach the Argus agent');
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    await fixture.whenStable();

    const banner = (fixture.nativeElement as HTMLElement).querySelector('.global-error');
    expect(banner?.textContent).toContain('Cannot reach the Argus agent');
  });
});
