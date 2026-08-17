import { ChangeDetectionStrategy, Component, OnInit, effect, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ArgusService } from './core/argus.service';
import { SessionService } from './core/session.service';
import { LockComponent } from './pages/lock.component';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, LockComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  protected readonly argus = inject(ArgusService);
  protected readonly session = inject(SessionService);

  constructor() {
    // The connections follow the lock rather than the page load. Connecting while locked would be
    // pointless anyway - the server refuses the hub and the frame socket without a session.
    effect(() => {
      if (this.session.unlocked()) void this.argus.start();
      else this.argus.stop();
    });
  }

  ngOnInit(): void {
    void this.session.refresh();
  }

  protected lock(): void {
    void this.session.lock();
  }
}
