import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { EventDetailComponent } from './event-detail.component';
import { DayAccount, DayEvent } from './calendar.models';

const EVENT: DayEvent = {
  accountId: 'a1', calendarId: 'c1', eventId: 'e1', stableId: 's1', title: 'Comité',
  start: '2026-06-10T14:00:00Z', end: '2026-06-10T15:30:00Z',
  isAllDay: false, showAs: 'busy', isCancelled: false, isOrganizer: false,
  isReplica: false, canWrite: true,
  replicas: [{ linkId: 'l1', maskTitle: 'Busy', destinationAccountId: 'a2',
               destinationCalendarId: 'c2', status: 'active' }],
};
const ACCOUNT: DayAccount = {
  accountId: 'a1', email: 'z@job1.com', kind: 'graph',
  scope: 'ReadWrite', freshness: 'live', events: [EVENT],
};

function create(event = EVENT, account = ACCOUNT) {
  const fixture = TestBed.createComponent(EventDetailComponent);
  fixture.componentRef.setInput('event', event);
  fixture.componentRef.setInput('account', account);
  fixture.detectChanges();
  return fixture;
}

describe('EventDetailComponent', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [EventDetailComponent],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('sends the selected response WITH the message and emits changed', async () => {
    const fixture = create();
    let changed = false;
    fixture.componentInstance.changed.subscribe(() => { changed = true; });

    (fixture.nativeElement.querySelector('[data-testid="respond-decline"]') as HTMLElement).click();
    fixture.detectChanges();
    const ta = fixture.nativeElement.querySelector('[data-testid="respond-message"]') as HTMLTextAreaElement;
    ta.value = 'No puedo asistir';
    ta.dispatchEvent(new Event('input'));
    (fixture.nativeElement.querySelector('[data-testid="respond-send"]') as HTMLElement).click();

    const req = http.expectOne('/zync/api/calendar/events/a1/e1/respond');
    expect(req.request.body).toEqual({ action: 'decline', message: 'No puedo asistir' });
    req.flush({ status: 'ok' });
    await fixture.whenStable();
    expect(changed).toBe(true);
  });

  it('cancel asks for confirmation before posting and is organizer-only', () => {
    // Invitee: the cancel button is NOT rendered.
    const invitee = create();
    expect(invitee.nativeElement.querySelector('[data-testid="cancel-event"]')).toBeNull();

    // Organizer: button renders, first click opens the inline confirm, confirming posts.
    const organizer = create({ ...EVENT, eventId: 'e2', isOrganizer: true });
    (organizer.nativeElement.querySelector('[data-testid="cancel-event"]') as HTMLElement).click();
    organizer.detectChanges();
    http.expectNone('/zync/api/calendar/events/a1/e2/respond'); // not yet — confirmation first
    expect(organizer.nativeElement.querySelector('[data-testid="cancel-confirm"]')).toBeTruthy();

    (organizer.nativeElement.querySelector('[data-testid="cancel-confirm"]') as HTMLElement).click();
    const req = http.expectOne('/zync/api/calendar/events/a1/e2/respond');
    expect(req.request.body).toEqual({ action: 'cancel' });
    req.flush({ status: 'ok' });
  });

  it('read-scope accounts get disabled actions with the upgrade hint, never silence', () => {
    // canWrite travels PER EVENT (the server derives it from the account scope).
    const fixture = create({ ...EVENT, canWrite: false }, { ...ACCOUNT, scope: 'Read' });
    const send = fixture.nativeElement.querySelector('[data-testid="respond-send"]') as HTMLButtonElement;
    expect(send.disabled).toBe(true);
    expect(fixture.nativeElement.textContent).toContain('upgrade');
  });

  it('removing a replica confirms, deletes the link and emits changed', async () => {
    const fixture = create();
    let changed = false;
    fixture.componentInstance.changed.subscribe(() => { changed = true; });

    (fixture.nativeElement.querySelector('[data-testid="replica-remove"]') as HTMLElement).click();
    fixture.detectChanges();
    (fixture.nativeElement.querySelector('[data-testid="replica-remove-confirm"]') as HTMLElement).click();

    const req = http.expectOne('/zync/api/calendar/replicas/l1');
    expect(req.request.method).toBe('DELETE');
    req.flush({});
    await fixture.whenStable(); // deleteReplica resolves in a microtask before changed fires
    expect(changed).toBe(true);
  });
});
