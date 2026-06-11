import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { CalendarApiService } from './calendar-api.service';

describe('CalendarApiService', () => {
  let api: CalendarApiService;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    api = TestBed.inject(CalendarApiService);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('getDay hits /api/calendar/day with the date', async () => {
    const p = api.getDay('2026-06-10');
    const req = http.expectOne('/zync/api/calendar/day?date=2026-06-10');
    expect(req.request.method).toBe('GET');
    req.flush({ date: '2026-06-10', accounts: [] });
    expect((await p).accounts).toEqual([]);
  });

  it('respond posts action and message to the two-segment respond endpoint', async () => {
    const p = api.respond('acc/1', 'evt-1', 'decline', 'Cannot make it');
    const req = http.expectOne('/zync/api/calendar/events/acc%2F1/evt-1/respond');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ action: 'decline', message: 'Cannot make it' });
    req.flush({ status: 'ok' });
    await p;
  });

  it('respond omits a blank message', async () => {
    const p = api.respond('a1', 'evt-1', 'accept', '   ');
    const req = http.expectOne('/zync/api/calendar/events/a1/evt-1/respond');
    expect(req.request.body).toEqual({ action: 'accept' });
    req.flush({ status: 'ok' });
    await p;
  });

  it('deleteReplica deletes the link', async () => {
    const p = api.deleteReplica('lnk-1');
    const req = http.expectOne('/zync/api/calendar/replicas/lnk-1');
    expect(req.request.method).toBe('DELETE');
    req.flush({});
    await p;
  });
});
