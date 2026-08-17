import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { SessionService } from '../core/session.service';

/**
 * The password screen. Drawn over the shell rather than routed to, so unlocking puts the user back
 * on the page they were already looking at instead of bouncing them to the dashboard.
 */
@Component({
  selector: 'argus-lock',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './lock.component.html',
  styleUrl: './lock.component.scss',
})
export class LockComponent implements AfterViewInit {
  private readonly session = inject(SessionService);

  private readonly passwordBox = viewChild<ElementRef<HTMLInputElement>>('passwordBox');

  protected readonly password = signal('');
  protected readonly error = signal<string | null>(null);
  protected readonly busy = signal(false);

  ngAfterViewInit(): void {
    // Desktops get to type straight away. Phones will not raise the keyboard without a tap here,
    // and focus() on load is worth nothing there either way.
    this.passwordBox()?.nativeElement.focus();
  }

  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (this.busy()) return;

    const password = this.password();
    if (!password) return;

    this.busy.set(true);
    this.error.set(null);

    const reason = await this.session.unlock(password);

    this.busy.set(false);
    this.password.set('');
    this.error.set(reason);

    if (reason) this.passwordBox()?.nativeElement.focus();
  }
}
