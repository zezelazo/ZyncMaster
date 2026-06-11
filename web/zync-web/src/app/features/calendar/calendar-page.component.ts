import { Component, inject, OnInit } from '@angular/core';
import { CalendarStore } from './calendar-store';
import { EventDetailComponent } from './event-detail.component';
import { clampBlock, localMinutes } from './day-layout';
import { DayAccount, DayEvent, Freshness } from './calendar.models';

const START_HOUR = 7, END_HOUR = 21, PX_PER_HOUR = 40;

@Component({
  selector: 'zw-calendar-page',
  imports: [EventDetailComponent],
  templateUrl: './calendar-page.component.html',
  styleUrl: './calendar-page.component.css',
})
export class CalendarPageComponent implements OnInit {
  readonly store = inject(CalendarStore);
  readonly gridHeight = (END_HOUR - START_HOUR) * PX_PER_HOUR;
  readonly hours = Array.from({ length: END_HOUR - START_HOUR }, (_, i) => START_HOUR + i);

  ngOnInit(): void { void this.store.load(this.store.date()); }

  shift(days: number): void {
    const [y, m, d] = this.store.date().split('-').map(Number);
    const dt = new Date(Date.UTC(y, m - 1, d + days));
    void this.store.load(dt.toISOString().slice(0, 10));
  }

  today(): void { void this.store.load(CalendarStore.todayIso()); }

  // az/aq/cy accent per account index, same palette contract as the desktop view.
  accent(i: number): string { return ['az', 'aq', 'cy'][i % 3]; }

  block(ev: DayEvent): { top: number; height: number } | null {
    const s = localMinutes(ev.start);
    const e = localMinutes(ev.end);
    if (s === null || e === null) return null;
    return clampBlock(s, e, { startHour: START_HOUR, endHour: END_HOUR, pxPerHour: PX_PER_HOUR });
  }

  // Backend decision 3: freshness is the STRING "live" | "snapshot_unavailable" in v1 — no
  // badge for live Graph accounts, an explicit degradation badge for COM. The minutes/device
  // variant arrives only when a later backend phase persists COM snapshots.
  freshness(f: Freshness): string | null {
    return f === 'snapshot_unavailable' ? 'snapshot unavailable' : null;
  }

  trackAccount(_: number, a: DayAccount): string { return a.accountId; }
}
