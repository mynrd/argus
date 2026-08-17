import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { SessionService } from '../core/session.service';
import { SettingsService } from '../core/settings.service';

@Component({
  selector: 'argus-settings',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './settings.component.html',
  styleUrl: './settings.component.scss',
})
export class SettingsComponent {
  protected readonly settings = inject(SettingsService);
  protected readonly session = inject(SessionService);

  protected toggleAutoLock(enabled: boolean): void {
    this.settings.autoLock.set(enabled);
  }

  protected setIdleMinutes(value: string): void {
    this.settings.setIdleMinutes(Number(value));
  }

  protected lockNow(): void {
    void this.session.lock();
  }
}
