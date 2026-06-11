import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CalendarStore } from './calendar-store';

describe('CalendarStore', () => {
  let store: CalendarStore;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    store = TestBed.inject(CalendarStore);
    http = TestBed.inject(HttpTestingController);
  });

  it('load publishes the day and clears loading; selection resolves the event', async () => {
    const p = store.load('2026-06-10');
    expect(store.loading()).toBe(true);
    http.expectOne('/zync/api/calendar/day?date=2026-06-10').flush({
      date: '2026-06-10',
      accounts: [{
        accountId: 'a1', email: 'z@job1.com', kind: 'graph', scope: 'ReadWrite',
        freshness: 'live',
        events: [{ accountId: 'a1', calendarId: 'c1', eventId: 'e1', stableId: 's1',
                   title: 'X', start: '2026-06-10T14:00:00Z', end: '2026-06-10T15:00:00Z',
                   isAllDay: false, showAs: 'busy', isCancelled: false, isOrganizer: false,
                   isReplica: false, canWrite: true, replicas: [] }],
      }],
    });
    await p;
    expect(store.loading()).toBe(false);
    expect(store.day()?.accounts.length).toBe(1);

    store.select({ accountId: 'a1', eventId: 'e1' });
    expect(store.selected()?.event.eventId).toBe('e1');
    expect(store.selected()?.account.accountId).toBe('a1');
    store.select(null);
    expect(store.selected()).toBeNull();
  });

  it('load failure lands in error, not an exception', async () => {
    const p = store.load('2026-06-11');
    http.expectOne('/zync/api/calendar/day?date=2026-06-11')
      .flush('boom', { status: 500, statusText: 'Server Error' });
    await p;
    expect(store.error()).toBeTruthy();
    expect(store.loading()).toBe(false);
  });
});
