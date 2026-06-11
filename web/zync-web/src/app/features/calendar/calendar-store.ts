import { computed, inject, Injectable, signal } from '@angular/core';
import { CalendarApiService } from './calendar-api.service';
import { DayAccount, DayEvent, DayView, EventKey } from './calendar.models';

// Signals-only store for the calendar management view (web-ui spec §3: a light class with
// exposed signals per module; no NgRx). One date at a time — the view is a day manager.
@Injectable({ providedIn: 'root' })
export class CalendarStore {
  private readonly api = inject(CalendarApiService);

  readonly date = signal<string>(CalendarStore.todayIso());
  readonly day = signal<DayView | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  private readonly selectedKey = signal<EventKey | null>(null);

  // The selected event resolved against the loaded day (survives a reload of the same day).
  // Keyed by accountId+eventId — the event's full identity (backend decision 1); a bare
  // eventId is NOT unique across accounts.
  readonly selected = computed<{ event: DayEvent; account: DayAccount } | null>(() => {
    const key = this.selectedKey();
    const view = this.day();
    if (!key || !view) return null;
    const account = view.accounts.find((a) => a.accountId === key.accountId);
    const event = account?.events.find((e) => e.eventId === key.eventId);
    return account && event ? { event, account } : null;
  });

  async load(dateIso: string): Promise<void> {
    this.date.set(dateIso);
    this.loading.set(true);
    this.error.set(null);
    try {
      this.day.set(await this.api.getDay(dateIso));
    } catch {
      this.error.set('Could not load the calendar. Check your connection and try again.');
    } finally {
      this.loading.set(false);
    }
  }

  select(key: EventKey | null): void { this.selectedKey.set(key); }

  reload(): Promise<void> { return this.load(this.date()); }

  static todayIso(): string {
    const d = new Date();
    const p = (n: number) => String(n).padStart(2, '0');
    return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
  }
}
