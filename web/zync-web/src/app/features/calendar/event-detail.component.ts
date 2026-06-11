import { Component, computed, inject, input, output, signal } from '@angular/core';
import { CalendarApiService } from './calendar-api.service';
import { DayAccount, DayEvent, RespondAction } from './calendar.models';

// Right-hand detail panel of the management view (mock-web-calendar.html): metadata, the
// replicas section, respond-to-organizer with an optional message, and organizer-only cancel
// behind an inline confirmation. Writes are only enabled on Graph readwrite accounts; read
// accounts degrade VISIBLY with the upgrade-scope hint (calendar-v2 spec §4/§6).
@Component({
  selector: 'zw-event-detail',
  imports: [],
  templateUrl: './event-detail.component.html',
  styleUrl: './event-detail.component.css',
})
export class EventDetailComponent {
  private readonly api = inject(CalendarApiService);

  readonly event = input.required<DayEvent>();
  readonly account = input.required<DayAccount>();
  readonly changed = output<void>();

  readonly action = signal<Exclude<RespondAction, 'cancel'>>('accept');
  message = '';
  readonly busy = signal(false);
  readonly error = signal<string | null>(null);
  readonly confirmingCancel = signal(false);
  readonly confirmingReplica = signal<string | null>(null);

  // canWrite travels PER EVENT on the wire (the server derives it from the account scope);
  // the kind check guards COM accounts, whose write-back is the explicit v1.1 deferral.
  readonly writable = computed(() =>
    this.account().kind === 'graph' && this.event().canWrite);

  onMessage(value: string): void { this.message = value; }

  async send(): Promise<void> {
    await this.post(this.action(), this.message);
  }

  startCancel(): void { this.confirmingCancel.set(true); }

  async confirmCancel(): Promise<void> {
    this.confirmingCancel.set(false);
    await this.post('cancel', this.message);
  }

  startRemoveReplica(linkId: string): void { this.confirmingReplica.set(linkId); }

  async confirmRemoveReplica(linkId: string): Promise<void> {
    this.confirmingReplica.set(null);
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.api.deleteReplica(linkId);
      this.changed.emit();
    } catch {
      this.error.set('Could not remove the replica. Try again.');
    } finally {
      this.busy.set(false);
    }
  }

  fmtRange(): string {
    const f = (iso: string) => {
      const d = new Date(iso);
      const p = (n: number) => String(n).padStart(2, '0');
      return `${p(d.getHours())}:${p(d.getMinutes())}`;
    };
    return `${f(this.event().start)} – ${f(this.event().end)}`;
  }

  private async post(action: RespondAction, message: string): Promise<void> {
    this.busy.set(true);
    this.error.set(null);
    try {
      await this.api.respond(this.event().accountId, this.event().eventId, action, message);
      this.changed.emit();
    } catch {
      this.error.set('The action failed. Try again.');
    } finally {
      this.busy.set(false);
    }
  }
}
