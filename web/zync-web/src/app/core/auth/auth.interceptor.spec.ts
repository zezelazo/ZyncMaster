import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { authInterceptor } from './auth.interceptor';
import { TokenStore } from './token-store';

describe('authInterceptor', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
      ],
    });
    localStorage.clear();
  });

  it('adds the bearer when signed in and nothing when signed out', () => {
    const client = TestBed.inject(HttpClient);
    const http = TestBed.inject(HttpTestingController);
    const tokens = TestBed.inject(TokenStore);

    client.get('/zync/api/calendar/day?date=2026-06-10').subscribe();
    expect(http.expectOne('/zync/api/calendar/day?date=2026-06-10').request.headers.has('Authorization')).toBe(false);

    tokens.setSession('acc-1', 'ref-1');
    client.get('/zync/api/calendar/prefix-rules').subscribe();
    expect(http.expectOne('/zync/api/calendar/prefix-rules').request.headers.get('Authorization')).toBe('Bearer acc-1');
    http.verify();
  });
});
