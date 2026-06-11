import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { CalendarPageComponent } from './calendar-page.component';

const DAY = {
  date: '2026-06-10',
  accounts: [
    { accountId: 'a1', email: 'z@job1.com', kind: 'graph', scope: 'ReadWrite',
      freshness: 'live',
      events: [{ accountId: 'a1', calendarId: 'c1', eventId: 'e1', stableId: 's1',
                 title: 'Comité de arquitectura',
                 start: '2026-06-10T14:00:00Z', end: '2026-06-10T15:30:00Z',
                 isAllDay: false, showAs: 'busy', isCancelled: false, isOrganizer: false,
                 isReplica: false, canWrite: true,
                 replicas: [{ linkId: 'l1', maskTitle: 'Busy', destinationAccountId: 'a2',
                              destinationCalendarId: 'c2', status: 'active' }] }] },
    { accountId: 'com1', email: 'classic@outlook.com', kind: 'com', scope: 'Read',
      freshness: 'snapshot_unavailable', events: [] },
  ],
};

describe('CalendarPageComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [CalendarPageComponent],
      providers: [provideHttpClient(), provideHttpClientTesting(), provideRouter([])],
    });
    http = TestBed.inject(HttpTestingController);
  });

  function flushDay(date = '2026-06-10') {
    http.expectOne((r) => r.url.includes('/api/calendar/day')).flush({ ...DAY, date });
  }

  it('renders one column per account with the COM freshness badge', async () => {
    const fixture = TestBed.createComponent(CalendarPageComponent);
    fixture.detectChanges();
    flushDay();
    await fixture.whenStable();
    fixture.detectChanges();

    const heads = fixture.nativeElement.querySelectorAll('[data-testid="col-head"]');
    expect(heads.length).toBe(2);
    // Backend decision 3: COM freshness is the STRING "snapshot_unavailable" — the badge says
    // exactly that; there is no minutes/device variant in v1.
    expect(fixture.nativeElement.textContent).toContain('snapshot unavailable');
    expect(fixture.nativeElement.querySelectorAll('[data-testid="event"]').length).toBe(1);
  });

  it('clicking an event opens the detail with its replicas section', async () => {
    const fixture = TestBed.createComponent(CalendarPageComponent);
    fixture.detectChanges();
    flushDay();
    await fixture.whenStable();
    fixture.detectChanges();

    (fixture.nativeElement.querySelector('[data-testid="event"]') as HTMLElement).click();
    fixture.detectChanges();

    const detail = fixture.nativeElement.querySelector('zw-event-detail');
    expect(detail).toBeTruthy();
    expect(detail.textContent).toContain('Comité de arquitectura');
    expect(detail.textContent).toContain('Busy');     // replica mask
    expect(detail.textContent).toContain('active');   // replica status
  });
});
